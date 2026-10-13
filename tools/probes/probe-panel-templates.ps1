# probe-panel-templates.ps1 - the translation panel picks prompt templates the way the
# reverse input box does: a chip that opens the full list (user request 2026-10-09; the
# chip used to step to the next template on every click).
#
# The panel never takes focus, so its list grows inside the panel instead of a popup
# (a popup's click-outside-to-close leans on mouse capture, which a background thread
# does not fully get). The DEBUG command "panel-templates" (with SHIYU_FAKE_BACKEND=1: a
# deterministic "[EN] " + text, no network) opens the panel, clicks the chip, picks
# another template, then reopens the list and presses Esc.
#
# Then it opens the panel on a single word (user request 2026-10-13: a word had no chip,
# so no way to switch templates). The same switch gives a fake dictionary that counts
# its lookups, also without network. It moves to a rewrite template and back, then opens
# a new word under the rewrite template and moves back once more.
#
# Checks:
#   - flow:panel-template-chip       the chip shows (the seed's own-key backend runs templates)
#   - flow:panel-template-list       the chip opens a list of every template, the current one ticked
#   - flow:panel-template-pick       picking one closes the list, switches the chip, keeps the panel
#   - flow:panel-template-escape     Esc with the list open closes the list, not the panel
#   - flow:panel-word-chip           a single word keeps the chip and the dictionary layout
#                                    (head word and card, no original block, no footer speak)
#   - flow:panel-word-rewrite-layout a rewrite template lays the word out like a sentence (no
#                                    head word, no card, original back); switching back brings
#                                    the card back without a second lookup
#   - flow:panel-word-card-on-demand a word opened under a rewrite template is not looked up;
#                                    switching to a translation template looks it up once
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\panel-templates'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'panel-templates.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'panel-templates' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:panel-template-chip' 'FAIL' 'the command wrote no log within 40s'
    return $script:Checks
}

$lines = @{}
foreach ($line in Get-Content $log) {
    if ($line -match '^([\w-]+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            $fields[$kv[0]] = $kv[1]
        }
        $lines[$Matches[1]] = $fields
    }
}

$chip = $lines['chip']
$ok = $chip -and $chip['visible'] -eq 'True'
Add-Check 'flow:panel-template-chip' $(if ($ok) { 'PASS' } else { 'FAIL' }) $(if ($chip) { "chip visible: {0}" -f $chip['visible'] } else { 'no chip line' })

$list = $lines['list']
$ok = $list -and $list['visible'] -eq 'True' -and $list['rows'] -eq $list['templates'] -and $list['ticked'] -eq '1'
$detail = if ($list) { "visible {0}, {1} rows for {2} templates, ticked {3}" -f $list['visible'], $list['rows'], $list['templates'], $list['ticked'] } else { 'no list line' }
Add-Check 'flow:panel-template-list' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$picked = $lines['picked']
$ok = $picked -and $picked['listVisible'] -eq 'False' -and $picked['switched'] -eq 'True' -and $picked['panelVisible'] -eq 'True'
$detail = if ($picked) { "list visible {0}, switched {1}, panel visible {2}" -f $picked['listVisible'], $picked['switched'], $picked['panelVisible'] } else { 'no picked line' }
Add-Check 'flow:panel-template-pick' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$escape = $lines['escape']
$ok = $escape -and $escape['listWasOpen'] -eq 'True' -and $escape['listVisible'] -eq 'False' -and $escape['panelVisible'] -eq 'True'
$detail = if ($escape) { "list was open {0}, list visible {1}, panel visible {2}" -f $escape['listWasOpen'], $escape['listVisible'], $escape['panelVisible'] } else { 'no escape line' }
Add-Check 'flow:panel-template-escape' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

function Layout($step) {
    $s = $lines[$step]
    if (-not $s) { return "no $step line" }
    "{0}: chip {1}, head word {2}, original {3}, card {4}, speak {5}, lookups {6}" -f `
        $step, $s['chip'], $s['header'], $s['original'], $s['card'], $s['speak'], $s['lookups']
}

# A word laid out as a dictionary entry: head word and card, no original block, no footer speak.
function IsWordLayout($s) {
    $s -and $s['header'] -eq 'True' -and $s['card'] -eq 'True' -and $s['original'] -eq 'False' -and $s['speak'] -eq 'False'
}

# A word laid out as a sentence: original block and footer speak, no head word, no card.
function IsSentenceLayout($s) {
    $s -and $s['header'] -eq 'False' -and $s['card'] -eq 'False' -and $s['original'] -eq 'True' -and $s['speak'] -eq 'True'
}

$word = $lines['word']
$ok = (IsWordLayout $word) -and $word['chip'] -eq 'True' -and $word['lookups'] -eq '1'
Add-Check 'flow:panel-word-chip' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Layout 'word')

$rewrite = $lines['rewrite']
$back = $lines['back']
$ok = (IsSentenceLayout $rewrite) -and $rewrite['chip'] -eq 'True' `
    -and (IsWordLayout $back) -and $back['lookups'] -eq '1'
Add-Check 'flow:panel-word-rewrite-layout' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("{0}; {1}" -f (Layout 'rewrite'), (Layout 'back'))

$freshRewrite = $lines['fresh-rewrite']
$freshBack = $lines['fresh-back']
$ok = (IsSentenceLayout $freshRewrite) -and $freshRewrite['lookups'] -eq '1' `
    -and (IsWordLayout $freshBack) -and $freshBack['lookups'] -eq '2'
Add-Check 'flow:panel-word-card-on-demand' $(if ($ok) { 'PASS' } else { 'FAIL' }) ("{0}; {1}" -f (Layout 'fresh-rewrite'), (Layout 'fresh-back'))

return $script:Checks
