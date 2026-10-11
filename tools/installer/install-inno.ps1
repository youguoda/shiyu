# install-inno.ps1 - fetch the pinned Inno Setup compiler (ADR-0015) and unpack it in
# portable mode: no registry entries, no uninstaller, nothing outside -Destination.
# Returns the path to ISCC.exe.
#
# Pinned on purpose: installer\Shiyu.iss is written for Inno Setup 6.7, and a runner
# that quietly picked up a new major version could change what we ship. The download
# is checked against a known SHA-256 before it runs.
#
# ASCII only (Windows PowerShell 5.1 reads BOM-less scripts as ANSI).

param([Parameter(Mandatory = $true)][string]$Destination)

$ErrorActionPreference = 'Stop'

$version = '6.7.3'
$sha256 = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
$url = "https://github.com/jrsoftware/issrc/releases/download/is-$($version -replace '\.', '_')/innosetup-$version.exe"

$iscc = Join-Path $Destination 'ISCC.exe'
if (Test-Path $iscc) { return $iscc }

$setup = Join-Path ([IO.Path]::GetTempPath()) "innosetup-$version.exe"
Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing
$actual = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $sha256) { throw "Inno Setup $version download does not match its pinned SHA-256 (got $actual)" }

$p = Start-Process -FilePath $setup -Wait -PassThru -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/PORTABLE=1', "/DIR=$Destination", '/NOICONS')
Remove-Item $setup -Force -ErrorAction SilentlyContinue
if ($p.ExitCode -ne 0 -or -not (Test-Path $iscc)) { throw "Inno Setup $version did not unpack (exit $($p.ExitCode))" }

return $iscc
