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

# The settings switch while the bar is open from a summon and pinned (the bar's gear leads to
# settings in exactly that state): on must collapse it at once, off must bring it back whole.
$on = $lines['toggleon']
$ok = $on -and ([double]$on['strip'] -le 4) -and $on['collapsed'] -eq 'True' -and $on['hints'] -eq '0'
$detail = if ($on) { "switch turned on while the summoned bar is open at the top: strip {0} DIP, collapsed {1}, drag hints {2}" -f $on['strip'], $on['collapsed'], $on['hints'] } else { 'no toggleon line' }
Add-Check 'flow:bar-dock-switch-on-summoned' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# User request 2026-10-22: edge-dock only acts on a bar resting at the top edge. Switched on
# while the bar sits elsewhere, it stays put and the user is told once to drag it up.
$away = $lines['toggleaway']
$ok = $away -and $away['expanded'] -eq 'True' -and ([double]$away['strip'] -gt 100) -and $away['hints'] -eq '1'
$detail = if ($away) { "switch turned on with the bar away from the top: expanded {0}, visible {1} DIP, drag hints {2}" -f $away['expanded'], $away['strip'], $away['hints'] } else { 'no toggleaway line' }
Add-Check 'flow:bar-dock-switch-on-away' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# User report 2026-10-22: with the pin off, dragging the bar to the top switched pin and
# edge-dock on. A drag only moves the bar: with edge-dock off it rests at the top, unhidden.
$off0 = $lines['dropoff']
$ok = $off0 -and $off0['pinned'] -eq 'False' -and $off0['dock'] -eq 'False' -and $off0['expanded'] -eq 'True' -and ([double]$off0['strip'] -gt 100)
$detail = if ($off0) { "top drop with pin and edge-dock off: pinned {0}, edge-dock {1}, expanded {2}, visible {3} DIP" -f $off0['pinned'], $off0['dock'], $off0['expanded'], $off0['strip'] } else { 'no dropoff line' }
Add-Check 'flow:bar-dock-drop-leaves-settings' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$off = $lines['toggleoff']
$ok = $off -and $off['top'] -eq '0' -and $off['expanded'] -eq 'True'
$detail = if ($off) { "switch turned off again: top {0} (work-area top), expanded {1}" -f $off['top'], $off['expanded'] } else { 'no toggleoff line' }
Add-Check 'flow:bar-dock-switch-off' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# Quick paste while the bar sits as a strip: it must come out whole, not take focus up there.
$sc = $lines['summoncollapsed']
$ok = $sc -and ([double]$sc['before'] -le 4) -and ([double]$sc['after'] -gt 100) -and $sc['expanded'] -eq 'True'
$detail = if ($sc) { "quick paste on the strip: visible {0} DIP before, {1} DIP after, expanded {2}" -f $sc['before'], $sc['after'], $sc['expanded'] } else { 'no summoncollapsed line' }
Add-Check 'flow:bar-dock-summon-from-strip' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$drop = $lines['drop']
if ($userPresent) {
    Add-Check 'flow:bar-dock-droptop' 'SKIP' 'user present (cursor moved between samples) - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-dropaway' 'SKIP' 'user present - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-livedrag' 'SKIP' 'user present - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-summondrop' 'SKIP' 'user present - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-capture-lost' 'SKIP' 'user present - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-summon-hover' 'SKIP' 'user present - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-dragdown-stays' 'SKIP' 'user present - cursor-coupled steps skipped'
    Add-Check 'flow:bar-dock-menu-keeps-bar' 'SKIP' 'user present - cursor-coupled steps skipped'
    return $script:Checks
}

$ok = $drop -and $drop['topCollapsed'] -eq 'True'
$detail = if ($drop) { "a drag ending at the top docks the bar: {0}" -f $drop['topCollapsed'] } else { 'no drop line' }
Add-Check 'flow:bar-dock-droptop' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$ok = $drop -and $drop['awayExpanded'] -eq 'True' -and $drop['awaySettingOn'] -eq 'True' -and $drop['awayStaysOnLeave'] -eq 'True'
$detail = if ($drop) { "a drag ending away from the top: shown {0}, edge-dock still on {1}, stays out when the mouse leaves {2}" -f $drop['awayExpanded'], $drop['awaySettingOn'], $drop['awayStaysOnLeave'] } else { 'no drop line' }
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

# User report 2026-10-21: a bar summoned by the hotkey and dragged to the top collapsed fine,
# but hovering the strip never brought it back out.
$hover = $lines['summonhover']
$ok = $hover -and $hover['expanded'] -eq 'True'
$detail = if ($hover) { "hovering the strip after a summoned bar docked: expanded {0}, strip {1} DIP" -f $hover['expanded'], $hover['strip'] } else { 'no summonhover line' }
Add-Check 'flow:bar-dock-summon-hover' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# User report 2026-10-21: dragged down from the top, the bar snapped back into the strip on
# release (the drop rule read the collapsed window's off-screen top edge), and the hints
# seemed to flash. The bar must stay where it was dropped, and no key hint may light up.
$drag = $lines['dragdown']
if ($drag -and $drag['cursor'] -eq 'False') {
    Add-Check 'flow:bar-dock-dragdown-stays' 'SKIP' $covered
} else {
    $raw = (Get-Content $log -Encoding UTF8 | Where-Object { $_ -like 'dragdown|*' } | Select-Object -First 1)
    $samples = @{}
    if ($raw) {
        foreach ($part in $raw.Substring('dragdown|'.Length).Split(';')) {
            if ($part -match '^(\w+):(.*)$') { $samples[$Matches[1]] = $Matches[2] }
        }
    }
    $topOf = { param($s) if ($s -match 'top=(-?\d+)') { [int]$Matches[1] } else { $null } }
    $dropped = & $topOf $samples['d5']
    $after = & $topOf $samples['up']
    $lit = @($samples.Values | Where-Object { $_ -match 'keys=True|badges=[1-9]' }).Count
    # After the release the pointer moves up but stays over the bar: the bar must not jump back
    # to the strip and slide out again (that was the remaining "flash", 2026-10-22).
    $moved = @(1..8 | ForEach-Object { & $topOf $samples["m$_"] } | Where-Object { ($null -eq $_) -or ([math]::Abs($_ - $dropped) -gt 2) })
    $setting = $lines['dragdownsetting']
    $ok = ($null -ne $dropped) -and ($null -ne $after) -and ([math]::Abs($after - $dropped) -le 2) -and ($lit -eq 0) -and ($moved.Count -eq 0) -and $setting -and $setting['dock'] -eq 'True'
    $detail = "top while dragging {0}, after release {1}; samples with key hints lit: {2}; samples after release where the bar moved: {3}; edge-dock still on: {4}" -f $dropped, $after, $lit, $moved.Count, $(if ($setting) { $setting['dock'] } else { 'n/a' })
    Add-Check 'flow:bar-dock-dragdown-stays' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail
}

# User report 2026-10-22: on the expanded docked bar, clicking the tag drop-down or the filter
# button collapsed the bar into the strip and left the menu hanging alone. A menu takes the
# mouse capture, which reads as "mouse left"; the bar must stay out while a menu is open and
# after an item is picked, and go back to the strip only once the menu closes with the
# pointer elsewhere.
$menu = $lines['menuclick']
if ($menu -and $menu['cursor'] -eq 'False') {
    Add-Check 'flow:bar-dock-menu-keeps-bar' 'SKIP' $covered
} else {
    $keys = 'open', 'withMenu', 'picked', 'afterPick', 'funnel', 'withFunnel', 'awayWithMenu', 'collapsedAfterClose'
    $ok = [bool]$menu
    foreach ($k in $keys) { if (-not $menu -or $menu[$k] -ne 'True') { $ok = $false } }
    $detail = if ($menu) { ($keys | ForEach-Object { "$_=$($menu[$_])" }) -join ', ' } else { 'no menuclick line' }
    Add-Check 'flow:bar-dock-menu-keeps-bar' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail
}

return $script:Checks
