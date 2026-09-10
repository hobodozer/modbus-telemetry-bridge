<#
.SYNOPSIS
    Builds the SimHub plugin and installs it into a SimHub installation.

.DESCRIPTION
    SimHub loads plugins from its own program folder, which normally sits under Program Files and
    needs administrator rights to write to. The script tells you plainly if that is the case rather
    than half-installing.

    SimHub must be CLOSED while the plugin DLL is replaced, and it asks you to authorise a new
    plugin the first time it sees one.

.PARAMETER SimHubPath
    SimHub's install folder. Auto-detected if omitted.

.PARAMETER BridgePort
    Telemetry port the plugin should send to. Must match the bridge's SimHub listen port.

.PARAMETER WhatIf
    Show what would happen without copying anything.

.EXAMPLE
    .\install-simhub-plugin.ps1
    .\install-simhub-plugin.ps1 -BridgePort 15600 -SimHubPath "D:\SimHub"
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SimHubPath,
    [int]$BridgePort = 15600,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

# ---- Locate SimHub -----------------------------------------------------------------------

if (-not $SimHubPath) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\SimHub",
        "$env:ProgramFiles\SimHub"
    )
    $SimHubPath = $candidates | Where-Object { Test-Path (Join-Path $_ "SimHub.Plugins.dll") } | Select-Object -First 1
}

if (-not $SimHubPath -or -not (Test-Path (Join-Path $SimHubPath "SimHub.Plugins.dll"))) {
    throw "SimHub not found. Pass -SimHubPath ""C:\Path\To\SimHub""."
}

Step "SimHub found at $SimHubPath"

# ---- Warn if SimHub is running -----------------------------------------------------------

$running = Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "    SimHub is currently running (pid $($running.Id))." -ForegroundColor Yellow
    Write-Host "    Close SimHub before installing - the plugin DLL cannot be replaced while it is loaded." -ForegroundColor Yellow
    throw "SimHub is running. Close it and run this again."
}

# ---- Build -------------------------------------------------------------------------------

Step "Building the plugin ($Configuration)"
dotnet build "plugin\ModbusBridge.SimHubPlugin\ModbusBridge.SimHubPlugin.csproj" `
    -c $Configuration -p:SimHubPath="$SimHubPath" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }

$built = "plugin\ModbusBridge.SimHubPlugin\bin\$Configuration\ModbusBridge.SimHubPlugin.dll"
if (-not (Test-Path $built)) { throw "Built plugin not found at $built." }

# ---- Check we can actually write there ---------------------------------------------------

$target = Join-Path $SimHubPath "ModbusBridge.SimHubPlugin.dll"
try {
    $probe = Join-Path $SimHubPath ".write-probe"
    [System.IO.File]::WriteAllText($probe, "")
    Remove-Item $probe -Force
}
catch {
    Write-Host ""
    Write-Host "    Cannot write to $SimHubPath." -ForegroundColor Yellow
    Write-Host "    Re-run this script from an elevated PowerShell prompt (Run as administrator)." -ForegroundColor Yellow
    throw "Insufficient permissions to install into SimHub."
}

# ---- Install -----------------------------------------------------------------------------

Step "Installing to $target"
if ($PSCmdlet.ShouldProcess($target, "Copy plugin DLL")) {
    Copy-Item $built $target -Force
    Write-Host "    Copied ModbusBridge.SimHubPlugin.dll" -ForegroundColor Green
}

# The settings file goes to LocalAppData, which never needs elevation.
$settingsDir = Join-Path $env:LOCALAPPDATA "ModbusBridge"
$settingsPath = Join-Path $settingsDir "ModbusBridge.SimHubPlugin.json"
if ($PSCmdlet.ShouldProcess($settingsPath, "Write plugin settings")) {
    New-Item -ItemType Directory -Force $settingsDir | Out-Null
    $settings = [ordered]@{
        BridgeHost            = "127.0.0.1"
        BridgePort            = $BridgePort
        MinIntervalMs         = 10
        HelloIntervalMs       = 1000
        FeedbackPrefix        = "Bridge"
        RaiseEventsOnFeedback = $true
        VerboseLogging        = $false
    }
    $settings | ConvertTo-Json | Set-Content $settingsPath -Encoding utf8
    Write-Host "    Wrote $settingsPath (bridge port $BridgePort)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Start SimHub. It will ask you to authorise the new plugin - say yes,"
Write-Host "     then enable 'Modbus Telemetry Bridge' in SimHub's plugin list and restart SimHub."
Write-Host "  2. In the bridge, enable SimHub telemetry on the SimHub tab with listen port $BridgePort,"
Write-Host "     then Apply and restart."
Write-Host "  3. Use 'Browse SimHub properties...' to pick what to stream."
Write-Host ""
