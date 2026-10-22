# probe-bar-keys.ps1 - which keys type into the bar's search box and which act on the selected
# card (user report 2026-10-22: with a card selected, pressing P to pin typed "p" into the
# search box).
#
# The rule: the bar is either searching (focus in the search box - keys type) or picking (a
# card was clicked, or the arrows were used with an empty search - focus on the list, the card
# letters act). Ctrl+letter acts in both. A key that types text while picking goes back to
# the search box and lands there.
#
# The DEBUG command "bar-keys" clicks a card through the real handler, then sends P, K, Esc,
# Down and S through WPF's own input pipeline (the key down goes to the focus of that moment;
# when unhandled, its character follows to the focus of THAT moment, the way WM_CHAR lands).
# That never touches the foreground window, so it runs while someone uses the machine.
# Ctrl+P needs the real keyboard (modifier state cannot be synthesized): it is pressed with
# SendInput only while the bar itself is the foreground window, and skipped otherwise.
# Only keys that cannot paste anything are used; digits are covered by the Core unit tests.
#
# Checks:
#   - flow:bar-keys-click-picks     clicking a card moves focus from the search box to the list
#   - flow:bar-keys-letter-acts     P then pins the card and types nothing
#   - flow:bar-keys-typing-returns  K (not a card letter) goes back to the search box as "k"
#   - flow:bar-keys-ctrl-acts       Ctrl+P in the search box unpins, still typing nothing
#   - flow:bar-keys-arrow-picks     Down with an empty search picks; S then toggles favorite
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$names = 'flow:bar-keys-click-picks', 'flow:bar-keys-letter-acts', 'flow:bar-keys-typing-returns', 'flow:bar-keys-ctrl-acts', 'flow:bar-keys-arrow-picks'

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\bar-keys'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'bar-keys.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar-keys'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    foreach ($n in $names) { Add-Check $n 'FAIL' 'the command wrote no log within 60s' }
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
    foreach ($n in $names) { Add-Check $n 'FAIL' ("the command threw {0}" -f $lines['error']['type']) }
    return $script:Checks
}

if ($lines['keys'] -and $lines['keys']['card'] -eq 'False') {
    foreach ($n in $names) { Add-Check $n 'FAIL' 'no card in the list to click' }
    return $script:Checks
}

$click = $lines['click']
$ok = $click -and $click['focus'] -eq 'List'
$detail = if ($click) { "focus after clicking a card: {0} (was {1})" -f $click['focus'], $lines['start']['focus'] } else { 'no click line' }
Add-Check 'flow:bar-keys-click-picks' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$pl = $lines['p']
$ok = $pl -and $pl['pinned'] -eq 'True' -and $pl['query'] -eq '0'
$detail = if ($pl) { "after P: pinned {0}, search text length {1}, focus {2}" -f $pl['pinned'], $pl['query'], $pl['focus'] } else { 'no p line' }
Add-Check 'flow:bar-keys-letter-acts' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$kl = $lines['k']
$ok = $kl -and $kl['focus'] -eq 'Search' -and $kl['query'] -eq 'k'
$detail = if ($kl) { "after K: focus {0}, search text '{1}'" -f $kl['focus'], $kl['query'] } else { 'no k line' }
Add-Check 'flow:bar-keys-typing-returns' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$cl = $lines['ctrlp']
if ($cl -and $cl['fg'] -eq 'False') {
    Add-Check 'flow:bar-keys-ctrl-acts' 'SKIP' 'the bar was not the foreground window - Ctrl+P needs the real keyboard, nothing was pressed'
} else {
    $ok = $cl -and $cl['pinned'] -ne $cl['before'] -and $cl['query'] -eq '0' -and $cl['focus'] -eq 'Search'
    $detail = if ($cl) { "Ctrl+P in the search box: pinned {0} -> {1}, search text length {2}, focus {3}" -f $cl['before'], $cl['pinned'], $cl['query'], $cl['focus'] } else { 'no ctrlp line' }
    Add-Check 'flow:bar-keys-ctrl-acts' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail
}

$dl = $lines['down']
$ok = $dl -and $dl['focus'] -eq 'List' -and $dl['favoriteToggled'] -eq 'True' -and $dl['query'] -eq '0'
$detail = if ($dl) { "after Down with an empty search: focus {0}; S toggled favorite {1}, search text length {2}" -f $dl['focus'], $dl['favoriteToggled'], $dl['query'] } else { 'no down line' }
Add-Check 'flow:bar-keys-arrow-picks' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
