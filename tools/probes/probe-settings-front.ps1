# probe-settings-front.ps1 - clicking the tray icon brings the settings window to the
# front (user report 2026-10-13: it sometimes stayed behind other windows).
#
# A tray left click runs SettingsModule.Show. Before the fix an open window only got
# Activate(): a minimized window stayed minimized, and an activation refused by the
# foreground lock just flashed the taskbar button. The DEBUG command "settings-front"
# runs Show four ways and logs the window state and whether it holds the foreground:
# first open; after minimizing; after maximizing then minimizing (it must come back
# maximized); and after another window of the same process covered it.
#
# The cross-process lock itself (the click lands on the taskbar, so Shiyu may not take
# the foreground) cannot be staged here. Its fallback sequence is pinned by the Core
# tests in ForegroundReclaimTests.
#
# Checks:
#   - flow:settings-front-open              the first open holds the foreground
#   - flow:settings-front-restores-minimized a minimized window comes back normal and in front
#   - flow:settings-front-keeps-maximized   a window minimized from maximized comes back maximized
#   - flow:settings-front-over-other-window a window covered by another comes back in front
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\settings-front'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-Null
$log = Join-Path $dataDir 'settings-front.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'settings-front'
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:settings-front-open' 'FAIL' 'the command wrote no log within 40s'
    return $script:Checks
}

$lines = @{}
foreach ($line in Get-Content $log) {
    if ($line -match '^([\w-]+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $lines[$Matches[1]] = $fields
    }
}

function Step($name) {
    $s = $lines[$name]
    if (-not $s) { return "no $name line" }
    "{0}: state {1}, in front {2}" -f $name, $s['state'], $s['foreground']
}

function InFront($name, $state) {
    $s = $lines[$name]
    $s -and $s['state'] -eq $state -and $s['foreground'] -eq 'True'
}

$ok = InFront 'open' 'Normal'
Add-Check 'flow:settings-front-open' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Step 'open')

$ok = InFront 'restored' 'Normal'
Add-Check 'flow:settings-front-restores-minimized' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Step 'restored')

$ok = InFront 'maximized' 'Maximized'
Add-Check 'flow:settings-front-keeps-maximized' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Step 'maximized')

$cover = $lines['cover']
$ok = $cover -and $cover['inFront'] -eq 'True' -and (InFront 'uncovered' 'Normal')
$detail = "the cover took the front {0}; {1}" -f $(if ($cover) { $cover['inFront'] } else { '?' }), (Step 'uncovered')
Add-Check 'flow:settings-front-over-other-window' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
