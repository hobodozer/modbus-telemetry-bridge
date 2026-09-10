<#
.SYNOPSIS
    Builds the TIA Portal Openness helper tools.

.DESCRIPTION
    Compiles TiaInspect.exe and TiaExport.exe with the .NET Framework compiler against
    the Openness assemblies of the installed TIA Portal. No SDK, project file or NuGet
    restore is involved - these are single-file tools on purpose.

    Output goes to .\bin next to this script.

.EXAMPLE
    .\build-tia-tools.ps1
    .\bin\TiaInspect.exe                        # attach and dump the open project
    .\bin\TiaExport.exe C:\some\out\dir         # export block source (needs STEP 7 Pro)
#>
[CmdletBinding()]
param(
    [string]$PortalVersion = 'V18',
    [string]$OutputPath = "$PSScriptRoot\bin"
)

$ErrorActionPreference = 'Stop'

$apiDir = "C:\Program Files\Siemens\Automation\Portal $PortalVersion\PublicAPI\$PortalVersion"
$engineering = Join-Path $apiDir 'Siemens.Engineering.dll'
if (-not (Test-Path $engineering)) {
    throw "Openness assemblies not found at $apiDir. Is TIA Portal $PortalVersion installed?"
}

# The Framework compiler, not Roslyn from the .NET SDK: Openness is .NET Framework 4.8
# and x64, and csc.exe here needs no project file or targeting pack.
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc." }

New-Item -ItemType Directory -Force $OutputPath | Out-Null

foreach ($name in @('TiaInspect', 'TiaExport')) {
    $src = Join-Path $PSScriptRoot "$name.cs"
    $exe = Join-Path $OutputPath "$name.exe"
    Write-Host "==> $name" -ForegroundColor Cyan
    & $csc /nologo /target:exe /platform:x64 /out:$exe $src /r:"$engineering"
    if ($LASTEXITCODE -ne 0) { throw "$name failed to compile." }
    Write-Host "    $exe" -ForegroundColor Green
}

Write-Host ""
Write-Host "Start TIA Portal and open the project before running either tool."
