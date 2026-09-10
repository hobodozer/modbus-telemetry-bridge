<#
.SYNOPSIS
    One entry point for the things this project does constantly.

.DESCRIPTION
    Exists to keep command lines short. Every task here was previously a long inline command
    retyped from memory, which is where the avoidable effort went.

.EXAMPLE
    .\rig.ps1 status                 # is anything running, what does the log say
    .\rig.ps1 build                  # stop the app, build, restart it
    .\rig.ps1 test                   # full build.ps1: smoke test + UI self-test
    .\rig.ps1 start / stop / restart
    .\rig.ps1 read 200 67            # holding registers, non-zero only
    .\rig.ps1 read 144 16 string
    .\rig.ps1 map 16                 # regenerate the HMI register map
    .ig.ps1 capture 8              # what the HMI actually asks for, via tshark
    .ig.ps1 verify-clone           # clone from the remote and build it, clean
    .\rig.ps1 log 30
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'build', 'test', 'start', 'stop', 'restart', 'read', 'map', 'capture', 'log', 'scan', 'verify-clone')]
    [string]$Task = 'status',
    [Parameter(Position = 1)][string]$A,
    [Parameter(Position = 2)][string]$B,
    [Parameter(Position = 3)][string]$C
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$data = Join-Path $root 'rig'
$exe = Join-Path $root 'src\ModbusBridge.App\bin\Release\net8.0-windows\ModbusBridge.exe'
# Capture adapter. Set RIG_NIC to a \Device\NPF_{...} name from 'tshark -D' to pin one;
# otherwise the first Ethernet interface is used.
$nic = $env:RIG_NIC
$tshark = "$env:ProgramFiles\Wireshark\tshark.exe"

function Stop-Bridge {
    $p = Get-Process ModbusBridge -ErrorAction SilentlyContinue
    if ($p) { $p | Stop-Process -Force; Start-Sleep -Seconds 2; "stopped" } else { "not running" }
}

function Start-Bridge {
    Start-Process $exe -ArgumentList '--data', "`"$data`"" | Out-Null
    Start-Sleep -Seconds 7
    "started"
}

function Latest-Log { Get-ChildItem "$data\logs\*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }

switch ($Task) {
    'status' {
        $p = Get-Process ModbusBridge -ErrorAction SilentlyContinue
        "bridge   : $(if ($p) { "running (pid $($p.Id))" } else { 'stopped' })"
        "simhub   : $(if (Get-Process SimHubWPF -ErrorAction SilentlyContinue) { 'running' } else { 'stopped' })"
        $log = Latest-Log
        if ($log) { ""; "--- $($log.Name) ---"; Get-Content $log.FullName -Tail 6 }
    }
    'stop'    { Stop-Bridge }
    'start'   { Start-Bridge }
    'restart' { Stop-Bridge; Start-Bridge }
    'build' {
        # The running app locks its own exe, so it has to go down first.
        Stop-Bridge | Out-Null
        dotnet build (Join-Path $root 'ModbusBridge.sln') -c Release --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "build failed" }
        Start-Bridge
    }
    'test' {
        Stop-Bridge | Out-Null
        & (Join-Path $root 'build.ps1')
    }
    'read' {
        if (-not $A) { throw "usage: .\rig.ps1 read <address> [count] [type]" }
        $args = @('-NoProfile', '-File', (Join-Path $root 'tools\modbus-read.ps1'), '-Address', $A)
        if ($B) { $args += @('-Count', $B) }
        if ($C) { $args += @('-Type', $C) } else { $args += '-NonZero' }
        & pwsh @args
    }
    'map' {
        $n = if ($A) { $A } else { '16' }
        python (Join-Path $root 'tools\make-hmi-map.py') (Join-Path $data 'config\bridge.json') --components $n
        "config hot-reloads; check .\rig.ps1 log"
    }
    'capture' {
        if (-not (Test-Path $tshark)) { throw "tshark not found at $tshark" }
        if (-not $nic) {
            $line = & $tshark -D 2>$null | Where-Object { $_ -match 'Ethernet' } | Select-Object -First 1
            if (-not $line) { throw "No Ethernet interface found. Set RIG_NIC - see 'tshark -D'." }
            $nic = ($line -split ' ')[1]
        }
        $seconds = if ($A) { $A } else { '8' }
        "capturing $seconds s of HMI Modbus traffic..."
        $out = & $tshark -i $nic -f 'tcp port 502' -a "duration:$seconds" -Y 'modbus' `
                         -T fields -e ip.src -e modbus.func_code -e modbus.reference_num -e modbus.word_cnt 2>$null
        # Only requests carry a reference number; responses do not. That separates the HMI's
        # reads from the PLC's replies without hardcoding anyone's address.
        $req = $out | Where-Object { $f = $_ -split "`t"; $f.Count -ge 3 -and $f[2] -match '^\d' }
        "requests: $($req.Count)"
        ""
        "func  start  count  requests"
        $req | ForEach-Object { $f = $_ -split "`t"; "{0}|{1}|{2}" -f $f[1], $f[2], $f[3] } |
            Group-Object | Sort-Object { [int](($_.Name -split '\|')[1]) } |
            ForEach-Object { $p = $_.Name -split '\|'; "{0,-5} {1,-6} {2,-6} {3}" -f $p[0], $p[1], $p[2], $_.Count }
    }
    'scan' {
        # The PLC serves one TCP connection, so it will not answer a scan while the bridge is
        # polling it. Stop the bridge first if it is missing from the results.
        $scanArgs = @('--scan')
        if ($A) { $scanArgs += $A }
        if ($B) { $scanArgs += @('--timeout', $B) }
        & $exe @scanArgs
    }
    'verify-clone' {
        # Building the working tree proves nothing about what was committed. An ignore rule once
        # kept a whole source directory out of the repository and every local build still passed.
        $temp = Join-Path $env:TEMP "mbb-verify-$(Get-Random)"
        $url = git -C $root remote get-url origin
        if (-not $url) { throw "No 'origin' remote to clone from." }

        "cloning $url"
        git clone -q --depth 1 $url $temp
        if ($LASTEXITCODE -ne 0) { throw "Clone failed." }

        try {
            Push-Location $temp
            dotnet build ModbusBridge.sln -c Release --nologo -v minimal
            if ($LASTEXITCODE -ne 0) { throw "The committed tree does not build." }
            "the committed tree builds"
            "for the full suite run build.ps1 in the clone - stop this rig first, it owns vJoy"
        }
        finally {
            Pop-Location
            Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
        }
    }
    'log' {
        $n = if ($A) { [int]$A } else { 20 }
        $log = Latest-Log
        if ($log) { Get-Content $log.FullName -Tail $n } else { "no log yet" }
    }
}
