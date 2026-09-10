<#
.SYNOPSIS
    Reads a tag off the wire BY NAME, decoding and scaling it the way the bridge would.

.DESCRIPTION
    Checking one value used to be three steps done by hand: find the tag's address in the config,
    read the raw register, then apply the gain mentally. Getting any of the three wrong looks
    exactly like a broken data path, and the scaling step in particular has already sent one
    investigation down the wrong road.

    This does all three. It resolves the tag through tools/config-query.py, reads through
    tools/modbus-read.ps1, and prints the raw value, the engineering value and where it came
    from - so a wrong number can be blamed on the right thing.

    Reads the BRIDGE's own server by default, not the PLC. The PLC's MB_SERVER accepts exactly one
    TCP connection, so while the bridge is polling, anything else aimed at it is refused.

.EXAMPLE
    .\tools\read-tag.ps1 -Tag fs.fuelLevel
    .\tools\read-tag.ps1 -Tag speed -Config rig\config\bridge.json
    .\tools\read-tag.ps1 -Tag plc1.di.btn01 -Target 127.0.0.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Tag,
    [string]$Config,
    [string]$Target = '127.0.0.1',
    [int]$Port = 0,
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Config) { $Config = Join-Path $root 'rig\config\bridge.json' }
if (-not (Test-Path $Config)) { throw "no config at $Config" }

# --- resolve the tag to wire details -------------------------------------------------------
$resolved = @{}
python (Join-Path $PSScriptRoot 'config-query.py') $Config resolve $Tag | ForEach-Object {
    $pair = $_ -split '=', 2
    if ($pair.Count -eq 2) { $resolved[$pair[0]] = $pair[1] }
}

if ($resolved['found'] -ne '1') {
    Write-Host "no point named '$Tag' in $Config" -ForegroundColor Yellow
    Write-Host "try: python tools\config-query.py `"$Config`" tags" -ForegroundColor DarkGray
    exit 1
}

if ($resolved['matches'] -ne '1') {
    Write-Host "$($resolved['matches']) tags matched; using '$($resolved['tag'])'" -ForegroundColor DarkGray
}

$address = [int]$resolved['address']
$area = $resolved['area']
$type = $resolved['dataType'].ToLower()
$gain = [double]$resolved['gain']
$offset = [double]$resolved['offset']
$size = [int]$resolved['size']
$bitIndex = [int]$resolved['bitIndex']
if ($Port -eq 0) { $Port = [int]$resolved['port'] }

# modbus-read.ps1 speaks a smaller set of types than the config does. Anything it cannot decode
# natively is read as raw registers and reported as such rather than silently mis-decoded.
$readable = @('uint16', 'int16', 'uint32', 'int32', 'float32', 'string')
$readType = if ($readable -contains $type) { $type } else { 'uint16' }
$native = $readable -contains $type

$call = @(
    '-NoProfile', '-File', (Join-Path $PSScriptRoot 'modbus-read.ps1'),
    '-Target', $Target, '-Port', $Port, '-Area', $area,
    '-Address', $address, '-Count', $size, '-Type', $readType,
    '-Unit', $resolved['unit']
)
# modbus-read.ps1 prints the scaled value alongside the raw one when -Gain is not 1, which is
# exactly the comparison worth seeing. It has no -Offset, so a non-zero offset is noted below.
if ($gain -ne 1.0) { $call += @('-Gain', $gain) }

Write-Host ""
Write-Host "$($resolved['tag'])" -ForegroundColor Cyan
$detail = "  $area $address"
if ($size -gt 1) { $detail += "..$($address + $size - 1)" }
$detail += "  $type"
if ($bitIndex -ge 0) { $detail += " bit $bitIndex" }
$detail += "  gain $gain offset $offset  access $($resolved['access'])"
Write-Host $detail -ForegroundColor DarkGray
if ($resolved['description']) { Write-Host "  $($resolved['description'])" -ForegroundColor DarkGray }
Write-Host ""

$output = & pwsh @call 2>&1
$output | ForEach-Object { Write-Host "  $_" }

if (-not $native) {
    Write-Host ""
    Write-Host "  note: '$type' is read as raw registers - modbus-read cannot decode it" -ForegroundColor Yellow
}
elseif ($gain -ne 1.0 -or $offset -ne 0.0) {
    Write-Host ""
    Write-Host "  columns are: address, raw, raw x $gain" -ForegroundColor DarkGray
    if ($offset -ne 0.0) {
        Write-Host "  add offset $offset for the engineering value - modbus-read has no -Offset" -ForegroundColor Yellow
    }
    Write-Host "  (a value that looks 1000x wrong is usually scaling, not a dead source)" -ForegroundColor DarkGray
}
