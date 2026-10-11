# smoke-installer.ps1 - install the setup silently into a throwaway folder, check what it
# left behind, then uninstall silently and check it is all gone (ADR-0015).
#
#   -WithDesktopIcon    also tick the desktop-shortcut task and check the shortcut
#   -DestructiveChecks  CI ONLY. Also runs "Shiyu.App.exe --remove-user-data" against fake
#                       data in %LOCALAPPDATA%\Shiyu, and checks the uninstaller removes an
#                       autostart entry that points into the install folder. On a real
#                       machine those are the user's history and the user's autostart, so
#                       the switch is refused unless GITHUB_ACTIONS is set.
#
# Refuses to start if a Shiyu is already registered as installed for this user, or if a
# shortcut with Shiyu's name already sits in the Start menu or on the desktop: the smoke
# install would take them over and the smoke uninstall would delete them.
#
# ASCII only. The Chinese app name is built from code points.

param(
    [Parameter(Mandatory = $true)][string]$Setup,
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$WithDesktopIcon,
    [switch]$DestructiveChecks
)

$ErrorActionPreference = 'Stop'

if ($DestructiveChecks -and $env:GITHUB_ACTIONS -ne 'true') {
    throw '-DestructiveChecks deletes %LOCALAPPDATA%\Shiyu and edits autostart; it only runs on CI'
}

$name = [string][char]0x62FE + [char]0x8BED
$appId = '{158C0196-252A-4126-8516-DC9C05C0B7BB}'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$dir = Join-Path ([IO.Path]::GetTempPath()) ('shiyu-setup-smoke-' + [guid]::NewGuid().ToString('N'))
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$name.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$name.lnk"

if (Test-Path $uninstallKey) { throw 'an installed Shiyu is registered for this user; not smoke-testing over it' }
if (Test-Path $startMenu) { throw "a Start menu shortcut named like Shiyu already exists: $startMenu" }
if ($WithDesktopIcon -and (Test-Path $desktop)) { throw "a desktop shortcut named like Shiyu already exists: $desktop" }

$failures = @()
function Check([string]$What, [bool]$Ok) {
    if ($Ok) { Write-Host "  [PASS] $What" } else { Write-Host "  [FAIL] $What"; $script:failures += $What }
}

# --- install ------------------------------------------------------------------------

$tasks = if ($WithDesktopIcon) { 'desktopicon' } else { '' }
$p = Start-Process -FilePath $Setup -Wait -PassThru -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$dir", "/TASKS=$tasks")
Check "setup exits 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
Check 'Shiyu.App.exe is in the chosen folder' (Test-Path (Join-Path $dir 'Shiyu.App.exe'))
Check 'the uninstaller is there' (Test-Path (Join-Path $dir 'unins000.exe'))

$entry = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Check 'registered in Apps for the current user only' ($null -ne $entry)
if ($entry) {
    Check "Apps shows version $Version (got $($entry.DisplayVersion))" ($entry.DisplayVersion -eq $Version)
    Check 'Apps knows the install folder' ($entry.InstallLocation.TrimEnd('\') -eq $dir.TrimEnd('\'))
}

Check 'Start menu shortcut created' (Test-Path $startMenu)
if ($WithDesktopIcon) {
    Check 'desktop shortcut created when asked for' (Test-Path $desktop)
} else {
    Check 'no desktop shortcut unless asked for' (-not (Test-Path $desktop))
}

# --- CI only: the "also delete my data" path and the autostart cleanup ---------------

if ($DestructiveChecks) {
    $data = Join-Path $env:LOCALAPPDATA 'Shiyu'
    New-Item -ItemType Directory -Force (Join-Path $data 'images') | Out-Null
    Set-Content (Join-Path $data 'settings.json') '{}'
    Set-Content (Join-Path $data 'history.db') 'x'
    Set-Content (Join-Path $data 'images\20261010-120000-abcdef.png') 'x'
    $r = Start-Process -FilePath (Join-Path $dir 'Shiyu.App.exe') -ArgumentList '--remove-user-data' -Wait -PassThru
    Check "--remove-user-data exits 0 (got $($r.ExitCode))" ($r.ExitCode -eq 0)
    Check 'the data folder is gone' (-not (Test-Path $data))

    # What the app itself writes on first start (StartupRegistration): a quoted exe path.
    Set-ItemProperty $runKey -Name 'Shiyu' -Value ('"' + (Join-Path $dir 'Shiyu.App.exe') + '"')
}

# --- uninstall ----------------------------------------------------------------------

# The uninstaller copies itself to %TEMP% and returns at once: wait for what it removes.
Start-Process -FilePath (Join-Path $dir 'unins000.exe') -Wait -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') | Out-Null
$deadline = (Get-Date).AddSeconds(90)
while (((Test-Path $dir) -or (Test-Path $uninstallKey)) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}

Check 'the install folder is gone' (-not (Test-Path $dir))
Check 'the Apps entry is gone' (-not (Test-Path $uninstallKey))
Check 'the Start menu shortcut is gone' (-not (Test-Path $startMenu))
Check 'no desktop shortcut left behind' (-not (Test-Path $desktop))
if ($DestructiveChecks) {
    $left = (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).Shiyu
    Check 'autostart pointing into the install folder is gone' (-not $left)
}

if ($failures.Count -gt 0) { throw ('installer smoke failed: ' + ($failures -join '; ')) }
Write-Host 'installer smoke: all checks passed'
