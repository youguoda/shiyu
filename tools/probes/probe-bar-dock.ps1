# probe-bar-dock.ps1 - the edge-docked bar (user request 2026-10-20): the pinned bar
# collapses to a thin strip at the top edge, expands when the mouse enters the strip,
# collapses again when it leaves, and stays hidden while a fullscreen app is foreground.
#
# The DEBUG command "bar-dock" turns the setting on (with pin + lightweight off), summons,
# dismisses (which re-docks), reads the visible strip, raises MouseEnter/MouseLeave on the
# bar window, and finally turns the setting off (which restores the full position).
#
# Checks:
#   - flow:bar-dock-strip       after dismiss only a thin strip is visible at the top
#   - flow:bar-dock-expand      MouseEnter on the strip expands the bar to the work top
#   - flow:bar-dock-recollapse  MouseLeave collapses it back to the strip
#   - flow:bar-dock-restore     turning the setting off restores the full position
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\bar-dock'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'bar-dock.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar-dock'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:bar-dock-strip' 'FAIL' 'the command wrote no log within 60s'
    return $script:Checks
}

$lines = @{}
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^(\w+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $lines[$Matches[1]] = $fields
    }
}

if ($lines['error']) {
    Add-Check 'flow:bar-dock-strip' 'FAIL' ("the command threw {0}" -f $lines['error']['type'])
    return $script:Checks
}

$collapsed = $lines['collapsed']
$ok = $collapsed -and [double]$collapsed['strip'] -le 4
$detail = if ($collapsed) { "visible strip after dismiss: {0} DIP" -f $collapsed['strip'] } else { 'no collapsed line' }
Add-Check 'flow:bar-dock-strip' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$expanded = $lines['expanded']
$ok = $expanded -and $expanded['top'] -eq '0'
$detail = if ($expanded) { "expanded top: {0} (work-area top)" -f $expanded['top'] } else { 'no expanded line' }
Add-Check 'flow:bar-dock-expand' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$recollapsed = $lines['recollapsed']
$ok = $recollapsed -and $recollapsed['ok'] -eq 'True'
$detail = if ($recollapsed) { "re-collapsed on mouse leave: {0}" -f $recollapsed['ok'] } else { 'no recollapse line' }
Add-Check 'flow:bar-dock-recollapse' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$restored = $lines['restored']
$ok = $restored -and $restored['top'] -eq '0'
$detail = if ($restored) { "top restored after the setting turned off: {0}" -f $restored['top'] } else { 'no restored line' }
Add-Check 'flow:bar-dock-restore' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
