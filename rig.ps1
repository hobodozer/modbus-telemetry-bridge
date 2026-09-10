<#
.SYNOPSIS
    One entry point for the things this project does constantly.

.DESCRIPTION
    Exists to keep command lines short. Every task here was previously a long inline command
    retyped from memory, which is where the avoidable effort went.

.EXAMPLE
    .\rig.ps1 status                 # is anything running, what does the log say
    .\rig.ps1 health                 # the whole chain in one read: HMI, telemetry, game, host
    .\rig.ps1 build                  # stop the app, build, restart it
    .\rig.ps1 test                   # full build.ps1: smoke test + UI self-test
    .\rig.ps1 start / stop / restart
    .\rig.ps1 read 200 67            # holding registers, non-zero only
    .\rig.ps1 read 144 16 string
    .\rig.ps1 tag fs.fuelLevel         # read a tag BY NAME - address, decode and scaling
    .\rig.ps1 config find fuel         # what the config says about a tag or address
    .\rig.ps1 check                    # repository consistency, no build needed
    .\rig.ps1 probe                    # server data store: scaling + write-masking
    .\rig.ps1 outline ServerDataStore  # types and members of a C# file, with line numbers
    .\rig.ps1 errors 200               # warnings and errors from the newest log
    .\rig.ps1 map 16                 # regenerate the HMI register map
    .\rig.ps1 capture 8              # what the HMI actually asks for, via tshark
    .\rig.ps1 verify-clone           # clone from the remote and build it, clean
    .\rig.ps1 portable               # build a pristine copy with no git, no rig, no config
    .\rig.ps1 log 30
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'health', 'build', 'test', 'start', 'stop', 'restart', 'read', 'tag', 'map',
                 'capture', 'log', 'errors', 'scan', 'check', 'config', 'probe', 'outline',
                 'verify-clone', 'portable')]
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
        ""; "for the whole chain: .\rig.ps1 health"
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
    'portable' {
        # Simulates the machine that is NOT this one. Every portability breach so far was
        # found by a stranger's build failing: a .gitignore rule that hid nine source files,
        # a repo-check that assumed a git checkout, and Core pinned to net8.0-windows for no
        # reason. Copies the tree WITHOUT .git, rig/, bin/ or obj/, then builds and tests it
        # with nothing this machine happens to provide.
        $temp = Join-Path $env:TEMP "mbb-portable-$(Get-Random)"
        New-Item -ItemType Directory -Path $temp -Force | Out-Null
        "copying a pristine tree to $temp"

        $exclude = @('.git', 'bin', 'obj', 'rig', 'publish', '__pycache__', '.vs')
        Get-ChildItem -Path $root -Force | Where-Object { $exclude -notcontains $_.Name } |
            ForEach-Object { Copy-Item $_.FullName -Destination $temp -Recurse -Force -Exclude @() }
        Get-ChildItem -Path $temp -Recurse -Force -Directory |
            Where-Object { $exclude -contains $_.Name } |
            ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

        try {
            if (Test-Path (Join-Path $temp '.git')) { throw 'the copy still has a .git - the test would be meaningless' }

            "--- dotnet build, no scripts, no git, no config ---"
            dotnet build (Join-Path $temp 'ModbusBridge.sln') -c Release --nologo -v quiet
            if ($LASTEXITCODE -ne 0) { throw 'a pristine tree does not build' }

            "--- repo-check, with no git index to lean on ---"
            python (Join-Path $temp 'tools\repo-check.py') --quiet
            if ($LASTEXITCODE -ne 0) { throw 'repo-check fails on a pristine tree' }

            "--- smoke test ---"
            dotnet run --project (Join-Path $temp 'tests\ModbusBridge.SmokeTest\ModbusBridge.SmokeTest.csproj') -c Release --no-build
            if ($LASTEXITCODE -ne 0) { throw 'the smoke test fails on a pristine tree' }

            ""
            "a tree with no .git, no rig/ and no build output compiles and passes"
        }
        finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
    }
    'verify-clone' {
        # Building the working tree proves nothing about what was committed. An ignore rule once
        # kept a whole source directory out of the repository and every local build still passed.
        $temp = Join-Path $env:TEMP "mbb-verify-$(Get-Random)"
        $url = git -C $root remote get-url origin
        if (-not $url) { throw "No 'origin' remote to clone from." }

        # This clones the REMOTE, so unpushed commits are not in what it builds. Run before a push
        # it validates the previous remote and reports success, which is worse than not running it
        # - that already happened once and the result was believed.
        $ahead = git -C $root rev-list --count '@{upstream}..HEAD' 2>$null
        if ($LASTEXITCODE -eq 0 -and [int]$ahead -gt 0) {
            Write-Host "$ahead local commit(s) are not pushed." -ForegroundColor Yellow
            Write-Host "verify-clone builds the remote, so it would test the tree WITHOUT them." -ForegroundColor Yellow
            Write-Host "Push first, then run this again." -ForegroundColor Yellow
            break
        }

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
    'health' {
        & pwsh -NoProfile -File (Join-Path $root 'tools\health.ps1') -Config (Join-Path $data 'config\bridge.json')
    }
    'check' {
        python (Join-Path $root 'tools\repo-check.py') @($A, $B | Where-Object { $_ })
    }
    'config' {
        $cmd = if ($A) { $A } else { 'summary' }
        $call = @((Join-Path $root 'tools\config-query.py'), (Join-Path $data 'config\bridge.json'), $cmd)
        if ($B) { $call += $B }
        python @call
    }
    'tag' {
        if (-not $A) { throw "usage: .\rig.ps1 tag <tag-name>" }
        & pwsh -NoProfile -File (Join-Path $root 'tools\read-tag.ps1') -Tag $A -Config (Join-Path $data 'config\bridge.json')
    }
    'probe' {
        dotnet run --project (Join-Path $root 'tools\store-probe\StoreProbe.csproj') -c Release --nologo -v quiet
    }
    'outline' {
        if (-not $A) { throw "usage: .\rig.ps1 outline <file-or-type-name>" }
        python (Join-Path $root 'tools\code-map.py') $A
    }
    'errors' {
        # Warnings and errors only. The full log is mostly per-poll noise, and scrolling it
        # to find the one line that matters is where the time goes.
        $n = if ($A) { [int]$A } else { 400 }
        $log = Latest-Log
        if (-not $log) { 'no log yet'; break }
        "--- $($log.Name), last $n line(s) ---"
        Get-Content $log.FullName -Tail $n | Select-String -Pattern '\[(WARN|ERROR|FATAL)' 
    }
    'log' {
        $n = if ($A) { [int]$A } else { 20 }
        $log = Latest-Log
        if ($log) { Get-Content $log.FullName -Tail $n } else { "no log yet" }
    }
}
