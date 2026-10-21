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

# The drop/livedrag steps couple to the REAL cursor (SetCursorPos + synthetic
# events): with someone at the machine they flake, so they stand down instead.
$userPresent = Test-UserPresent

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

$drop = $lines['drop']
if ($userPresent) {
    Add-Check 'flow:bar-dock-droptop' 'SKIP' 'user present (cursor moved between samples) - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-dropaway' 'SKIP' 'user present - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-livedrag' 'SKIP' 'user present - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-summondrop' 'SKIP' 'user present - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-capture-lost' 'SKIP' 'user present - cursor-coupled steps skipped'
    return $script:Checks
}

$ok = $drop -and $drop['topCollapsed'] -eq 'True'
$detail = if ($drop) { "a drag ending at the top docks the bar: {0}" -f $drop['topCollapsed'] } else { 'no drop line' }
Add-Check 'flow:bar-dock-droptop' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$ok = $drop -and $drop['awayUndocked'] -eq 'True'
$detail = if ($drop) { "a drag ending away from the top undocks: {0}" -f $drop['awayUndocked'] } else { 'no drop line' }
Add-Check 'flow:bar-dock-dropaway' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# The drag steps press the real left button, and only when the probe's own bar is under the
# cursor: "cursor=False" means it was covered (or buttons are swapped) and nothing was pressed.
$covered = 'the bar was not under the cursor (covered, or buttons swapped) - nothing was pressed'
$livedrag = $lines['livedrag']
$ok = $false
$detail = 'no livedrag line'
if ($livedrag -and $livedrag['cursor'] -eq 'False') {
    Add-Check 'flow:bar-dock-livedrag' 'SKIP' $covered
} else {
    if ($livedrag -and ($livedrag.ContainsKey('xy0')) -and ($livedrag.ContainsKey('xyLive'))) {
        $a = $livedrag['xy0'] -split ';'
        $b = $livedrag['xyLive'] -split ';'
        if (($a.Count -eq 2) -and ($b.Count -eq 2)) {
            $dx = [int]$b[0] - [int]$a[0]
            $dy = [int]$b[1] - [int]$a[1]
            $ok = ([math]::Abs($dx) -ge 40) -and ([math]::Abs($dy) -ge 40)
            $detail = "the bar followed the cursor live, mid-drag move: ${dx};${dy} px"
        }
    }
    Add-Check 'flow:bar-dock-livedrag' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail
}

# Capture taken away mid-drag (Alt+Tab, the Win key, the window hiding): the button-up never
# arrives, so the drag must end right there - a later hover must not carry the window along.
# The drag must have really moved the bar first, or "it stayed put" proves nothing.
$lost = $lines['capturelost']
if ($lost -and $lost['cursor'] -eq 'False') {
    Add-Check 'flow:bar-dock-capture-lost' 'SKIP' $covered
} else {
    $ok = $lost -and $lost['xyAfterLoss'] -and ($lost['xyAfterLoss'] -ne $lost['xyStart']) -and ($lost['xyAfterLoss'] -eq $lost['xyHover'])
    $detail = if ($lost) { "start {0}, dragged to {1} before losing capture, after hovering on: {2}" -f $lost['xyStart'], $lost['xyAfterLoss'], $lost['xyHover'] } else { 'no capturelost line' }
    Add-Check 'flow:bar-dock-capture-lost' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail
}

$summondrop = $lines['summondrop']
$ok = $summondrop -and ([double]$summondrop['strip'] -le 4)
$detail = if ($summondrop) { "a top drop while summoned docks at once: {0} DIP strip" -f $summondrop['strip'] } else { 'no summondrop line' }
Add-Check 'flow:bar-dock-summondrop' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
