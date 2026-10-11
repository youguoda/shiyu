# build-installer.ps1 - compile installer\Shiyu.iss into shiyu-win-x64-setup.exe (ADR-0015).
# The release workflow, CI and local runs all come through here, so the three never drift.
# Returns the path to the setup file.
#
#   -Version       what Windows shows in Apps and the wizard title (the tag without the v)
#   -PublishDir    a "dotnet publish -r win-x64 --self-contained" output (Shiyu.App.exe inside)
#   -Iscc          ISCC.exe; defaults to the usual Inno Setup 6 install folders
#   -AllowRunning  local smoke runs only: compile without the "Shiyu is running" check, so a
#                  test copy can be installed into a temp folder while the user's own Shiyu
#                  keeps running. Never for anything that ships.
#
# ASCII only (Windows PowerShell 5.1 reads BOM-less scripts as ANSI).

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [string]$OutputDir = (Get-Location).Path,
    [string]$Iscc = '',
    [switch]$AllowRunning
)

$ErrorActionPreference = 'Stop'

if ($Iscc -eq '') {
    $Iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw 'ISCC.exe not found: pass -Iscc, or fetch it with tools\installer\install-inno.ps1' }
}

$publish = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $publish 'Shiyu.App.exe'))) { throw "no Shiyu.App.exe in $publish" }

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$out = (Resolve-Path $OutputDir).Path
$script = Join-Path $PSScriptRoot '..\..\installer\Shiyu.iss'

$arguments = @('/Qp', "/DAppVersion=$Version", "/DPublishDir=$publish", "/O$out")
if ($AllowRunning) { $arguments += '/DSmokeAllowRunning' }

# Shown, not returned: the caller gets the setup path and nothing else.
& $Iscc @arguments $script | Out-Host
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Join-Path $out 'shiyu-win-x64-setup.exe'
if (-not (Test-Path $setup)) { throw "ISCC did not produce $setup" }
return $setup
