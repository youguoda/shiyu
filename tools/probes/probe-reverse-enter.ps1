# probe-reverse-enter.ps1 - the reverse input box has an Enter button: clicking it is
# pressing Enter (user request 2026-10-13). Enter moves one step: run, paste back, or retry.
#
# The DEBUG command "reverse-enter" (with SHIYU_FAKE_BACKEND=1: a deterministic
# "[EN] " + input, no network) opens the box on the standard template and looks at the
# button empty and with a settled translation. It then switches to a rewrite template,
# clicks once while the input method is "composing" (nothing may happen), and clicks for
# real. A rewrite stops after its first Enter for review, so the click never pastes:
# the user's clipboard and other windows are never touched. reverse-enter.png lands in
# the data dir for a human look.
#
# Checks:
#   - flow:reverse-enter-button           the button sits right of the hint, shows the Enter
#                                         glyph, takes no focus and is not a drag handle
#   - flow:reverse-enter-follows-state    greyed while empty; on a settled translation it is
#                                         live and named "paste"; on a rewrite template it is
#                                         live and named "arrange"
#   - flow:reverse-enter-click-is-enter   a click while composing does nothing; a real click
#                                         starts the rewrite (greyed while it runs), which then
#                                         stops for review with the box still open
#   - flow:reverse-enter-bar-fits         under the longest hint the hint gives way: the chips
#                                         end before it starts and it ends before the button
#                                         (the first build let the hint squeeze the template chip)
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\reverse-enter'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'reverse-enter.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'reverse-enter' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:reverse-enter-button' 'FAIL' 'the command wrote no log within 40s'
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
    "{0}: enabled {1}, named {2}, tooltip {3}, status {4}" -f $name, $s['enabled'], $s['named'], $s['tip'], $s['status']
}

$b = $lines['button']
$ok = $b -and $b['visible'] -eq 'True' -and $b['glyph'] -eq 'True' -and $b['focusable'] -eq 'False' `
    -and $b['grip'] -eq 'False' -and $b['rightOfHint'] -eq 'True'
$detail = if ($b) { "visible {0}, Enter glyph {1}, focusable {2}, drag handle {3}, right of the hint {4}" -f `
    $b['visible'], $b['glyph'], $b['focusable'], $b['grip'], $b['rightOfHint'] } else { 'no button line' }
Add-Check 'flow:reverse-enter-button' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$empty = $lines['empty']
$ready = $lines['ready']
$needs = $lines['needs']
$ok = $empty -and $empty['enabled'] -eq 'False' -and $empty['named'] -eq 'True' `
    -and $lines['translated'] -and $lines['translated']['ok'] -eq 'True' `
    -and $ready -and $ready['enabled'] -eq 'True' -and $ready['named'] -eq 'True' -and $ready['tip'] -eq 'True' -and $ready['status'] -eq 'Ready' `
    -and $needs -and $needs['enabled'] -eq 'True' -and $needs['named'] -eq 'True' -and $needs['tip'] -eq 'True' -and $needs['status'] -eq 'NeedsEnter'
Add-Check 'flow:reverse-enter-follows-state' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("{0}; {1}; {2}" -f (Step 'empty'), (Step 'ready'), (Step 'needs'))

if ($lines['unsafe']) {
    Add-Check 'flow:reverse-enter-click-is-enter' 'FAIL' 'not on a rewrite template waiting for Enter - the click was skipped so it could not paste'
    return $script:Checks
}

$composing = $lines['composing']
$running = $lines['running']
$reviewed = $lines['reviewed']
$ok = $composing -and $composing['status'] -eq 'NeedsEnter' `
    -and $running -and $running['enabled'] -eq 'False' -and $running['status'] -eq 'Running' `
    -and $lines['rewritten'] -and $lines['rewritten']['ok'] -eq 'True' `
    -and $reviewed -and $reviewed['enabled'] -eq 'True' -and $reviewed['named'] -eq 'True' `
    -and $reviewed['status'] -eq 'Ready' -and $reviewed['visible'] -eq 'True'
$detail = "composing click left {0}; {1}; {2}, box visible {3}" -f `
    $(if ($composing) { $composing['status'] } else { '?' }), (Step 'running'), (Step 'reviewed'), $(if ($reviewed) { $reviewed['visible'] } else { '?' })
Add-Check 'flow:reverse-enter-click-is-enter' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$bar = $lines['bar']
$ok = $bar -and $bar['chipsFit'] -eq 'True' -and $bar['hintClear'] -eq 'True'
$detail = if ($bar) { "template chip ends at {0}, hint {1}-{2}, button starts at {3} DIP" -f `
    $bar['chipRight'], $bar['hintStart'], $bar['hintEnd'], $bar['buttonStart'] } else { 'no bar line' }
Add-Check 'flow:reverse-enter-bar-fits' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
