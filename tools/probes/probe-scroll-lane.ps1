# probe-scroll-lane.ps1 - pointing at a scrollbar never moves anything else
# (user report 2026-10-10: the thumb widened under the pointer and the
# controls beside it narrowed and stretched back, all over the app).
#
# The DEBUG command "scroll-lane" checks the implicit ScrollBar style, measures
# the pinned bar's list and the settings window's page scroller at rest, then
# writes the settings thumb's screen centre to scroll-lane.ready. This script
# moves the real pointer there - only when the user is not using the mouse -
# and puts it back afterwards; the app measures again while hovered.
#
# Checks:
#   - data:scroll-lane-log                  the command walked every step
#   - flow:scrollbar-no-hover-resize        no IsMouseOver trigger sizes the bar
#   - flow:scrollbar-lane-at-rest           8 DIP lanes, a 6 DIP pill centred in them
#   - flow:scrollbar-hover-keeps-content    hovered: the pill fills the lane, the
#                                           lane and the content width do not move
#                                           (SKIP when the user is present)
#   - flow:scrollbar-lane-kept-when-short   a page with nothing to scroll keeps the
#                                           empty lane: same content width, no thumb
#                                           (user request 2026-10-10)
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\scroll-lane'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-Null
$log = Join-Path $dataDir 'scroll-lane.log'
$ready = Join-Path $dataDir 'scroll-lane.ready'
Remove-Item $log, $ready -ErrorAction SilentlyContinue

$moved = $false
$restore = $null
$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'scroll-lane'
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $ready) -and -not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }

    if (Test-Path $ready) {
        $xy = (Get-Content $ready -Raw).Trim().Split(',')
        if (-not (Test-UserPresent)) {
            $restore = [Shiyu.Probe.Native]::Cursor()
            [void][Shiyu.Probe.Native]::MoveCursor([int]$xy[0], [int]$xy[1])
            $moved = $true
        }
    }

    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $log) -and ((Get-Content $log -Raw -Encoding UTF8) -match '(?m)^done\r?$')) { break }
        Start-Sleep -Milliseconds 200
    }
} finally {
    if ($moved -and $restore) { [void][Shiyu.Probe.Native]::MoveCursor($restore[0], $restore[1]) }
    if ($p) { Stop-ProbeApp $p }
}

$steps = @{}
if (Test-Path $log) {
    foreach ($line in Get-Content $log -Encoding UTF8) {
        $parts = $line.Split('|')
        $fields = @{}
        foreach ($pair in $parts | Select-Object -Skip 1) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $steps[$parts[0]] = $fields
    }
}

$expected = @('style', 'bar', 'settings-rest', 'settings-hover', 'settings-short', 'done')
$missing = @($expected | Where-Object { -not $steps.ContainsKey($_) })
if ($missing.Count -gt 0) {
    Add-Check 'data:scroll-lane-log' 'FAIL' ("log lacks: {0}" -f ($missing -join ', '))
    return $script:Checks
}
Add-Check 'data:scroll-lane-log' 'PASS' ("{0} steps logged" -f ($expected.Count - 1))

function Verdict([bool]$ok) { if ($ok) { 'PASS' } else { 'FAIL' } }
function Num([string]$text) { [double]::Parse($text, [Globalization.CultureInfo]::InvariantCulture) }

Add-Check 'flow:scrollbar-no-hover-resize' (Verdict ($steps['style']['size-on-hover'] -eq 'False')) `
    ("a hover trigger sizes the bar: {0}" -f $steps['style']['size-on-hover'])

$b = $steps['bar']
$r = $steps['settings-rest']
$ok = $b['visible'] -eq 'True' -and $r['visible'] -eq 'True' `
    -and (Num $b['lane']) -eq 8 -and (Num $r['lane']) -eq 8 `
    -and (Num $b['pill']) -eq 6 -and (Num $r['pill']) -eq 6 `
    -and (Num $b['inset']) -eq 1 -and (Num $r['inset']) -eq 1
Add-Check 'flow:scrollbar-lane-at-rest' (Verdict $ok) `
    ("bar lane {0} pill {1} inset {2}; settings lane {3} pill {4} inset {5}" -f $b['lane'], $b['pill'], $b['inset'], $r['lane'], $r['pill'], $r['inset'])

$h = $steps['settings-hover']
if (-not $moved) {
    Add-Check 'flow:scrollbar-hover-keeps-content' 'SKIP' 'user present (cursor moved between samples) - real-mouse hover skipped'
} elseif ($h['hovered'] -ne 'True') {
    Add-Check 'flow:scrollbar-hover-keeps-content' 'SKIP' 'the pointer never registered on the scrollbar (another window on top?)'
} else {
    $ok = (Num $h['content']) -eq (Num $r['content']) -and (Num $h['lane']) -eq (Num $r['lane']) `
        -and (Num $h['pill']) -eq 8 -and (Num $h['inset']) -eq 0
    Add-Check 'flow:scrollbar-hover-keeps-content' (Verdict $ok) `
        ("content {0} -> {1}, lane {2} -> {3}, pill {4} -> {5}" -f $r['content'], $h['content'], $r['lane'], $h['lane'], $r['pill'], $h['pill'])
}

$s = $steps['settings-short']
if ((Num $s['scrollable']) -gt 0) {
    Add-Check 'flow:scrollbar-lane-kept-when-short' 'SKIP' ("the page still scrolls {0} DIP at this screen height" -f $s['scrollable'])
} else {
    $ok = $s['visible'] -eq 'True' -and (Num $s['lane']) -eq 8 -and $s['thumb'] -eq 'False' `
        -and (Num $s['content']) -eq (Num $r['content'])
    Add-Check 'flow:scrollbar-lane-kept-when-short' (Verdict $ok) `
        ("lane {0} visible {1}, thumb shown {2}, content {3} (long page {4})" -f $s['lane'], $s['visible'], $s['thumb'], $s['content'], $r['content'])
}

return $script:Checks
