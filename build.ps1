<#
.SYNOPSIS
    Builds, tests and publishes the Modbus Telemetry Bridge.

.DESCRIPTION
    The default target produces a portable, self-contained folder under .\publish that can be
    copied to any 64-bit Windows machine - no .NET runtime install required. Configuration and
    logs are written next to the executable, so the whole folder moves as a unit.

.EXAMPLE
    .\build.ps1                 # build + run tests
    .\build.ps1 -Publish        # build + test + produce .\publish
    .\build.ps1 -Publish -SkipTests
#>
[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$SkipTests,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputPath = "$PSScriptRoot\publish"
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

if (-not $SkipTests) {
    # Static, needs no build, takes about a second, and catches what a green build cannot:
    # a source file .gitignore excludes, a project missing from the solution, a command
    # corrupted by an interpreted backslash escape. Run first so it fails fast.
    Step "Checking repository consistency"
    # The self-test first: a claim pattern that silently stops matching still reports "ok",
    # which is the one failure mode the checks below cannot survive. It already happened -
    # a case-sensitive pattern walked past a false trap in CLAUDE.md itself.
    python tools\repo-check.py --self-test | Select-String -Pattern "FAIL" -Quiet |
        ForEach-Object { if ($_) { throw "repo-check self-test failed - run: python tools\repo-check.py --self-test" } }
    python tools\repo-check.py --quiet
    if ($LASTEXITCODE -ne 0) { throw "repo-check failed - see above." }
    Write-Host "    repo-check passed." -ForegroundColor Green
}

# The running app holds an open handle to its own exe, so the copy step fails with a link
# error that says nothing about the real cause. This was documented as a trap for months
# and still cost a confusing failure, so handle it instead of warning about it.
$running = Get-Process ModbusBridge -ErrorAction SilentlyContinue
if ($running) {
    Step "Stopping ModbusBridge (pid $($running.Id)) - it locks its own exe"
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
    $script:WasRunning = $true
}

Step "Building ($Configuration)"
dotnet build ModbusBridge.sln -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# The SimHub plugin is intentionally outside the solution: it targets net48 and references
# assemblies out of a SimHub install, so it must not break the main build on a machine
# without SimHub.
$simHub = @("${env:ProgramFiles(x86)}\SimHub", "$env:ProgramFiles\SimHub") |
          Where-Object { Test-Path (Join-Path $_ "SimHub.Plugins.dll") } | Select-Object -First 1
if ($simHub) {
    Step "Building the SimHub plugin (SimHub found at $simHub)"
    dotnet build plugin\ModbusBridge.SimHubPlugin\ModbusBridge.SimHubPlugin.csproj `
        -c $Configuration -p:SimHubPath="$simHub" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "SimHub plugin build failed." }
}
else {
    Step "Skipping the SimHub plugin - SimHub is not installed on this machine"
}

if (-not $SkipTests) {
    Step "Running the end-to-end smoke test"
    # Binds loopback ports 15020 and 15502 briefly.
    dotnet run --project tests\ModbusBridge.SmokeTest\ModbusBridge.SmokeTest.csproj -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Smoke test failed." }

    Step "Probing the server data store"
    # Asserts the write-masking invariants and that read cost does not scale with map size.
    # The smoke test checks behaviour at one size; this is what catches a per-register cost.
    dotnet run --project tools\store-probe\StoreProbe.csproj -c $Configuration --nologo -v quiet -- --quiet
    if ($LASTEXITCODE -ne 0) { throw "Store probe failed." }

    Step "Running the UI self-test"
    $uiTemp = Join-Path $env:TEMP "mbbridge-selftest-$(Get-Random)"
    New-Item -ItemType Directory -Force $uiTemp | Out-Null
    try {
        $exe = "src\ModbusBridge.App\bin\$Configuration\net8.0-windows\ModbusBridge.exe"
        $p = Start-Process $exe -ArgumentList "--data", "`"$uiTemp`"", "--selftest" -PassThru -WindowStyle Minimized
        if (-not $p.WaitForExit(120000)) { $p.Kill(); throw "UI self-test timed out." }
        if ($p.ExitCode -ne 0) {
            Get-Content "$uiTemp\logs\*.log" | Select-String "binding|selftest"
            throw "UI self-test reported binding errors."
        }
        Write-Host "    UI self-test passed - no binding errors." -ForegroundColor Green
    }
    finally {
        Remove-Item -Recurse -Force $uiTemp -ErrorAction SilentlyContinue
    }
}

if ($Publish) {
    Step "Publishing a portable build to $OutputPath"
    if (Test-Path $OutputPath) { Remove-Item -Recurse -Force $OutputPath }

    dotnet publish src\ModbusBridge.App\ModbusBridge.App.csproj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $OutputPath `
        --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    # The single-file host still drops a .pdb next to the exe unless asked not to; tidy anything left.
    Get-ChildItem $OutputPath -Include *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

    $exe = Get-ChildItem "$OutputPath\ModbusBridge.exe" -ErrorAction SilentlyContinue
    if ($exe) {
        Write-Host ""
        Write-Host "    $($exe.FullName)" -ForegroundColor Green
        Write-Host ("    {0:N1} MB - copy this folder anywhere; no .NET install needed." -f ($exe.Length / 1MB))
    }
}

if ($script:WasRunning) {
    Write-Host ""
    Write-Host "    ModbusBridge was stopped for the build - restart it with: .\rig.ps1 start" -ForegroundColor Yellow
}

Step "Done"
