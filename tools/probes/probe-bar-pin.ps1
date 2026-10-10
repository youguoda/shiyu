# probe-bar-pin.ps1 - the narrow bar's pin (user request 2026-10-10: drop the
# resident bar and its hotkey/tray entry, put a pin and a settings gear at the
# bar's top right, and a pin switch in settings, off by default).
#
# The DEBUG command "bar-pin" walks the real paths inside the app (quick paste
# summon, the header pin through the settings store, a window of its own to
# take the focus away) and logs one line per step into <data dir>\bar-pin.log,
# plus bar-pin-header-off.png / bar-pin-header-on.png.
#
# Checks:
#   - data:bar-pin-log                        the command walked every step
#   - flow:tray-has-no-bar-row                the tray menu starts at quick paste
#   - flow:bar-pin-header                     pin and gear sit right of the search box
#   - flow:bar-pin-default-off                unpinned at first: hollow pin, quiet colour
#   - flow:bar-always-in-front                topmost pinned or not
#   - flow:bar-unpinned-hides-on-focus-loss   clicking elsewhere takes it away
#   - flow:bar-pin-click-writes-setting       the pin writes the setting, face follows
#   - flow:bar-pinned-stays-on-focus-loss     pinned, clicking elsewhere leaves it up
#   - flow:bar-pinned-notes-return-target     a press on it notes where to paste back
#   - flow:bar-pinned-summon-stays-put        quick paste again: same place, fresh query
#   - flow:bar-unpin-from-settings-hides      unpinned in settings while inactive: gone
#   - flow:bar-settings-gear                  the gear opens settings on the bar page
# The focus steps SKIP when the foreground lock refused the probe the focus.
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\bar-pin'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-Null
$log = Join-Path $dataDir 'bar-pin.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar-pin'
try {
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $log) -and ((Get-Content $log -Raw -Encoding UTF8) -match '(?m)^done\r?$')) { break }
        Start-Sleep -Milliseconds 300
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

# One hashtable per step: "<step>|field=value|field=value".
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

$expected = @('tray', 'default', 'header', 'unpinned-away', 'pinned', 'pinned-away', 'return-target', 'stay-put', 'unpin-from-settings', 'settings', 'done')
$missing = @($expected | Where-Object { -not $steps.ContainsKey($_) })
if ($missing.Count -gt 0) {
    Add-Check 'data:bar-pin-log' 'FAIL' ("log lacks: {0}" -f ($missing -join ', '))
    return $script:Checks
}
Add-Check 'data:bar-pin-log' 'PASS' ("{0} steps logged" -f ($expected.Count - 1))

function Verdict([bool]$ok) { if ($ok) { 'PASS' } else { 'FAIL' } }

# The tray: no "bar" row any more; quick paste is the first entry.
$rows = $steps['tray']['rows'].Split(',')
Add-Check 'flow:tray-has-no-bar-row' (Verdict ((@($rows) -notcontains 'bar') -and $rows[0] -eq 'quick-paste')) `
    ("rows: {0}" -f $steps['tray']['rows'])

# Header layout: [search][pin][gear], all inside the window.
$h = $steps['header']
$ok = $h['pin-visible'] -eq 'True' -and $h['settings-visible'] -eq 'True' `
    -and [int]$h['search-right'] -le [int]$h['pin'] `
    -and [int]$h['pin'] -lt [int]$h['settings'] `
    -and [int]$h['settings-right'] -le [int]$h['width']
Add-Check 'flow:bar-pin-header' (Verdict $ok) `
    ("search ends {0}, pin {1}, gear {2}..{3}, window {4}" -f $h['search-right'], $h['pin'], $h['settings'], $h['settings-right'], $h['width'])

$d = $steps['default']
Add-Check 'flow:bar-pin-default-off' (Verdict ($d['pinned'] -eq 'False' -and $d['glyph'] -eq 'E718' -and $d['accent'] -eq 'False')) `
    ("setting {0}, glyph {1}, accent {2}" -f $d['pinned'], $d['glyph'], $d['accent'])

$pinned = $steps['pinned']
$pinnedAway = $steps['pinned-away']
Add-Check 'flow:bar-always-in-front' (Verdict ($d['topmost'] -eq 'True' -and $pinned['topmost'] -eq 'True' -and $pinnedAway['topmost'] -eq 'True')) `
    ("topmost unpinned {0}, pinned {1}, pinned and away {2}" -f $d['topmost'], $pinned['topmost'], $pinnedAway['topmost'])

$u = $steps['unpinned-away']
if ($u['active-before'] -ne 'True' -or $u['elsewhere-active'] -ne 'True') {
    Add-Check 'flow:bar-unpinned-hides-on-focus-loss' 'SKIP' ("no focus to lose (bar active {0}, elsewhere active {1})" -f $u['active-before'], $u['elsewhere-active'])
} else {
    Add-Check 'flow:bar-unpinned-hides-on-focus-loss' (Verdict ($u['visible'] -eq 'False')) ("visible after focus loss: {0}" -f $u['visible'])
}

Add-Check 'flow:bar-pin-click-writes-setting' (Verdict ($pinned['setting'] -eq 'True' -and $pinned['glyph'] -eq 'E841' -and $pinned['accent'] -eq 'True')) `
    ("setting {0}, glyph {1}, accent {2}" -f $pinned['setting'], $pinned['glyph'], $pinned['accent'])

if ($pinnedAway['active-before'] -ne 'True' -or $pinnedAway['elsewhere-active'] -ne 'True') {
    Add-Check 'flow:bar-pinned-stays-on-focus-loss' 'SKIP' ("no focus to lose (bar active {0}, elsewhere active {1})" -f $pinnedAway['active-before'], $pinnedAway['elsewhere-active'])
} else {
    Add-Check 'flow:bar-pinned-stays-on-focus-loss' (Verdict ($pinnedAway['visible'] -eq 'True')) ("visible after focus loss: {0}" -f $pinnedAway['visible'])
}

$r = $steps['return-target']
if ($r['foreground'] -eq '0' -or $r['foreground'] -eq $r['bar']) {
    Add-Check 'flow:bar-pinned-notes-return-target' 'SKIP' ("the bar itself was the foreground ({0})" -f $r['foreground'])
} else {
    Add-Check 'flow:bar-pinned-notes-return-target' (Verdict ($r['noted'] -eq $r['foreground'])) `
        ("foreground {0} (elsewhere {1}), noted {2}" -f $r['foreground'], $r['elsewhere'], $r['noted'])
}

$s = $steps['stay-put']
Add-Check 'flow:bar-pinned-summon-stays-put' (Verdict ($s['before'] -eq $s['after'] -and $s['query'] -eq '0' -and $s['self-noted'] -eq 'False')) `
    ("at {0} -> {1}, query length {2}, noted itself {3}" -f $s['before'], $s['after'], $s['query'], $s['self-noted'])

$f = $steps['unpin-from-settings']
if ($f['bar-active'] -eq 'True') {
    Add-Check 'flow:bar-unpin-from-settings-hides' 'SKIP' 'the bar kept the focus (foreground lock)'
} else {
    Add-Check 'flow:bar-unpin-from-settings-hides' (Verdict ($f['visible'] -eq 'False' -and $f['setting'] -eq 'False' -and $f['glyph'] -eq 'E718')) `
        ("visible {0}, setting {1}, glyph {2}" -f $f['visible'], $f['setting'], $f['glyph'])
}

$g = $steps['settings']
Add-Check 'flow:bar-settings-gear' (Verdict ($g['opened'] -eq 'True' -and $g['page'] -eq 'bar')) `
    ("settings open {0}, page {1}" -f $g['opened'], $g['page'])

return $script:Checks
