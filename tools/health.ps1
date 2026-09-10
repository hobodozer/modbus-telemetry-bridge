<#
.SYNOPSIS
    One-shot health of the whole chain, read from the bridge's own status registers.

.DESCRIPTION
    "Is it working?" was costing a multi-step investigation every time: check the process, tail the
    log, read a register, decide whether a zero meant idle or dead. The bridge already publishes
    everything needed to answer it - protocol version, connected clients, telemetry age, game code,
    host statistics - so this reads them in two requests and says what is and is not alive.

    It reads the BRIDGE's server, which is the only thing that can be read while the bridge is
    running: the PLC's MB_SERVER accepts exactly one TCP connection, and the bridge holds it.

    Addresses are resolved from the config rather than hard-coded, so regenerating the map with
    tools/make-hmi-map.py does not silently break this.

.EXAMPLE
    .\tools\health.ps1
    .\tools\health.ps1 -Target 192.0.2.61 -Config rig\config\bridge.json
#>
[CmdletBinding()]
param(
    [string]$Config,
    [string]$Target = '127.0.0.1',
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Config) { $Config = Join-Path $root 'rig\config\bridge.json' }
if (-not (Test-Path $Config)) { throw "no config at $Config" }

function Resolve-Tag($tag) {
    $found = @{}
    python (Join-Path $PSScriptRoot 'config-query.py') $Config resolve $tag 2>$null | ForEach-Object {
        $pair = $_ -split '=', 2
        if ($pair.Count -eq 2) { $found[$pair[0]] = $pair[1] }
    }
    if ($found['found'] -eq '1') { return $found }
    return $null
}

function Say($label, $value, $verdict, $colour) {
    Write-Host ("  {0,-22} {1,-18} " -f $label, $value) -NoNewline
    Write-Host $verdict -ForegroundColor $colour
}

# --- process ---------------------------------------------------------------------------------
Write-Host ""
$process = Get-Process ModbusBridge -ErrorAction SilentlyContinue
if (-not $process) {
    Write-Host "  bridge is not running" -ForegroundColor Red
    Write-Host "  start it with: .\rig.ps1 start" -ForegroundColor DarkGray
    Write-Host ""
    exit 1
}
$uptime = [int]((Get-Date) - $process.StartTime).TotalMinutes
Say 'bridge process' "pid $($process.Id)" "up $uptime min" 'Green'

# --- resolve the status block ------------------------------------------------------------------
$wanted = @(
    @{ Tag = 'bridge.protocolVersion';  Label = 'protocol version' },
    @{ Tag = 'bridge.connectedClients'; Label = 'modbus clients' },
    @{ Tag = 'bridge.telemetryAgeMs';   Label = 'telemetry age' },
    @{ Tag = 'bridge.gameCode';         Label = 'game' },
    @{ Tag = 'pc.cpuPercent';           Label = 'host cpu' },
    @{ Tag = 'pc.gpuPercent';           Label = 'host gpu' },
    @{ Tag = 'pc.memPercent';           Label = 'host ram' }
)

$resolved = @{}
foreach ($item in $wanted) {
    $info = Resolve-Tag $item.Tag
    if ($info) { $resolved[$item.Tag] = $info }
}
if ($resolved.Count -eq 0) { throw "no bridge.* or pc.* points in $Config - is this the right config?" }

if ($Port -eq 0) { $Port = [int]($resolved.Values | Select-Object -First 1).port }

# Group the addresses into spans no wider than one FC3 can carry. Reading min..max in a single
# request looks tidier but silently drops anything past the 125-register limit - bridge.gameCode
# sits at 142 and vanished that way.
$addresses = @($resolved.Values | ForEach-Object { [int]$_.address } | Sort-Object -Unique)
$spans = @()
$spanStart = $addresses[0]
$spanEnd = $addresses[0]
foreach ($address in $addresses) {
    if ($address - $spanStart + 1 -le 125) { $spanEnd = $address; continue }
    $spans += , @($spanStart, $spanEnd)
    $spanStart = $address
    $spanEnd = $address
}
$spans += , @($spanStart, $spanEnd)

$values = @{}
try {
    foreach ($span in $spans) {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'modbus-read.ps1') `
            -Target $Target -Port $Port -Address $span[0] -Count ($span[1] - $span[0] + 1) -Type uint16 2>&1 |
            ForEach-Object {
                if ($_ -match '^\s*(\d+)\s+(-?\d+)') { $values[[int]$Matches[1]] = [int]$Matches[2] }
            }
    }
}
catch {
    Write-Host "  cannot read the bridge's server on ${Target}:${Port} - $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    exit 1
}

if ($values.Count -eq 0) {
    Say 'modbus server' "${Target}:${Port}" 'NO RESPONSE' 'Red'
    Write-Host ""
    exit 1
}
Say 'modbus server' "${Target}:${Port}" "$($values.Count) register(s) in $($spans.Count) request(s)" 'Green'

# Every address asked for must have come back, or a missing line below would read as "not mapped"
# when it actually means "not fetched".
$absent = @($addresses | Where-Object { -not $values.ContainsKey($_) })
if ($absent.Count) {
    Say 'coverage' "$($absent.Count) missing" ("no value for " + ($absent -join ', ')) 'Yellow'
}

function Value($tag) {
    if (-not $resolved.ContainsKey($tag)) { return $null }
    $address = [int]$resolved[$tag].address
    if (-not $values.ContainsKey($address)) { return $null }
    return $values[$address] * [double]$resolved[$tag].gain
}

Write-Host ""

# --- interpret ---------------------------------------------------------------------------------
$version = Value 'bridge.protocolVersion'
if ($null -ne $version) {
    Say 'protocol version' ('{0:N2}' -f $version) $(if ($version -gt 0) { 'ok' } else { 'ZERO' }) `
        $(if ($version -gt 0) { 'Green' } else { 'Red' })
}

$clients = Value 'bridge.connectedClients'
if ($null -ne $clients) {
    # This script is itself a client while it reads, so 1 is the floor, not a sign of the HMI.
    $verdict = if ($clients -ge 2) { 'HMI attached' } elseif ($clients -ge 1) { 'only this script' } else { 'none' }
    $colour = if ($clients -ge 2) { 'Green' } else { 'Yellow' }
    Say 'modbus clients' ([int]$clients) $verdict $colour
}

$age = Value 'bridge.telemetryAgeMs'
if ($null -ne $age) {
    # 65535 is the map's "never / stale for a long time" value.
    if ($age -ge 65535) { Say 'telemetry' 'never' 'SimHub not sending' 'Yellow' }
    elseif ($age -lt 1000) { Say 'telemetry' "$([int]$age) ms old" 'streaming' 'Green' }
    else { Say 'telemetry' "$([int]$age) ms old" 'STALE' 'Yellow' }
}

$game = Value 'bridge.gameCode'
if ($null -ne $game) {
    $name = switch ([int]$game) {
        0 { 'none' } 1 { 'FS25' } 2 { 'Forza Horizon 6' } 3 { 'BeamNG' } 255 { 'other' }
        default { "code $([int]$game)" }
    }
    Say 'game' $name $(if ($game -gt 0) { 'running' } else { 'not running' }) `
        $(if ($game -gt 0) { 'Green' } else { 'DarkGray' })
}

Write-Host ""

$cpu = Value 'pc.cpuPercent'
$gpu = Value 'pc.gpuPercent'
$ram = Value 'pc.memPercent'
foreach ($stat in @(@('host cpu', $cpu), @('host gpu', $gpu), @('host ram', $ram))) {
    if ($null -eq $stat[1]) { continue }
    # A host stat pinned at exactly zero usually means the collector is not running, not that the
    # machine is idle - idle still reports a fraction of a percent.
    $verdict = if ($stat[1] -gt 0) { 'collected' } else { 'ZERO - collector may be down' }
    $colour = if ($stat[1] -gt 0) { 'Green' } else { 'Yellow' }
    Say $stat[0] ('{0:N1} %' -f $stat[1]) $verdict $colour
}

Write-Host ""
Write-Host "  warnings and errors:  .\rig.ps1 errors" -ForegroundColor DarkGray
Write-Host "  one tag in detail:    .\rig.ps1 tag <name>" -ForegroundColor DarkGray
Write-Host ""
