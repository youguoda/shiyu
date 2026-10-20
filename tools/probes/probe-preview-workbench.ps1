# probe-preview-workbench.ps1 - the pinned preview workbench's cross-show stale
# state matrix (user report 2026-10-20: doubled text, leftover chips over the
# teach row, draft bleed into other entries, image zoom/pan carried across).
#
# The DEBUG command "preview-workbench" drives the whole matrix in the probe
# instance: pin A, set a draft, unpin, preview B, click the leftover chips
# (save-as-new only - it writes the probe store, never the clipboard), three
# pin/unpin cycles then one more click, text -> image -> link, image pan then
# switch images, delete the pinned card, Esc in the edit box, double-click on
# a zoomed image. Log: w|step=<name>|field=value..., last line done.
#
# Checks: flow:* must be green; defect:* FAIL = the defect reproduces (the
# known stale-state family this probe was built to pin down).
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\preview-workbench'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'preview-workbench.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'preview-workbench' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    while ((Get-Date) -lt $deadline -and (Test-Path $log) -and (@(Select-String -Path $log -Pattern '^done').Count -eq 0)) {
        Start-Sleep -Milliseconds 500
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:workbench-log-complete' 'FAIL' 'the command wrote no log within 90s'
    return $script:Checks
}

$steps = @{}
$done = $false
$errored = $false
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -eq 'done') { $done = $true }
    if ($line -match '^error\|') { $errored = $true }
    if ($line -match '^w\|step=([\w-]+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $steps[$Matches[1]] = $fields
    }
}

function Get-Step([string]$Name) {
    if ($steps.ContainsKey($Name)) { return $steps[$Name] }
    return @{}
}

function Field($Step, [string]$Key, [string]$Fallback) {
    if ($null -ne $Step -and $Step.ContainsKey($Key)) { return [string]$Step[$Key] }
    return $Fallback
}

function ConvertTo-InvariantDouble([string]$Text) {
    $value = 0.0
    if ([double]::TryParse($Text, [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) { return $value }
    return -1.0
}

$a = Get-Step 'A-text-text'
$b = Get-Step 'B-chip-unpinned'
$c = Get-Step 'C-accumulate'
$d = Get-Step 'D-text-image-link'
$e = Get-Step 'E-image-image'
$f = Get-Step 'F-forced'
$g = Get-Step 'G-esc-dblclick'

# --- flow: the parts that already work ----------------------------------------

$pinChips = Field $a 'pinChips' 'missing'
$ok = $pinChips -eq 'copy,save-as-new'
Add-Check 'flow:workbench-chip-table-text' $(if ($ok) { 'PASS' } else { 'FAIL' }) "pinned text card chips: $pinChips"

$imgChips = Field $d 'imgChips' 'missing'
$ok = $imgChips -eq 'copy-image'
Add-Check 'flow:workbench-chip-table-image' $(if ($ok) { 'PASS' } else { 'FAIL' }) "pinned image card chips: $imgChips"

$ok = $done -and (-not $errored) -and ($steps.Count -ge 7)
Add-Check 'flow:workbench-log-complete' $(if ($ok) { 'PASS' } else { 'FAIL' }) "done=$done errorLine=$errored steps=$($steps.Count)"

# --- defect repros (FAIL = the defect reproduces) ------------------------------

$editVisible = (Field $a 'editVisible' '?') -eq 'True'
$textBodyVisible = (Field $a 'textBodyVisible' '?') -eq 'True'
$ok = -not ($editVisible -and $textBodyVisible)
Add-Check 'defect:preview-stale-edit-overprint' $(if ($ok) { 'PASS' } else { 'FAIL' }) "entry B: readonly body + edit box both visible: $textBodyVisible/$editVisible"

$draftIsA = (Field $a 'draftIsA' '?') -eq 'True'
$ok = -not $draftIsA
Add-Check 'defect:preview-draft-bleed' $(if ($ok) { 'PASS' } else { 'FAIL' }) "entry B still carries A's draft: draftIsA=$draftIsA len=$(Field $a 'draftLen' '?')"

$chipsVisible = (Field $a 'chipsVisible' '?') -eq 'True'
$teachVisible = (Field $a 'teachVisible' '?') -eq 'True'
$ok = -not ($chipsVisible -and $teachVisible)
Add-Check 'defect:preview-stale-chips-overprint' $(if ($ok) { 'PASS' } else { 'FAIL' }) "entry B: leftover chips over the teach row: chips=$chipsVisible teach=$teachVisible ($(Field $a 'chips' '?'))"

$fired = ConvertTo-InvariantDouble (Field $b 'fired' '0')
$ok = $fired -eq 0
Add-Check 'defect:preview-chip-fires-unpinned' $(if ($ok) { 'PASS' } else { 'FAIL' }) "leftover chip fired while unpinned: fired=$fired"

$line0 = Field $b 'line0' ''
$ok = -not ($line0 -match 'save-as-new:\d+:DRAFT-A')
Add-Check 'defect:preview-saveasnew-wrong-text' $(if ($ok) { 'PASS' } else { 'FAIL' }) "save-as-new carried A's draft into B's entry: $line0"

$clicks = ConvertTo-InvariantDouble (Field $c 'clicksFired' '0')
$ok = $clicks -le 1
Add-Check 'defect:preview-handler-accumulation' $(if ($ok) { 'PASS' } else { 'FAIL' }) "one chip click after 5 pins fired: $clicks handlers (store delta $(Field $c 'store' '?'))"

$clickedC = (Field $c 'clicked' '?') -eq 'True'
$ok = -not ($clickedC -and ($clicks -eq 0))
Add-Check 'defect:preview-chip-clicks-dead' $(if ($ok) { 'PASS' } else { 'FAIL' }) "pinned chip click raised but WorkbenchAction never fired (sender is the row, not the button): clicked=$clickedC fired=$clicks"

$crossFired = ConvertTo-InvariantDouble (Field $d 'fired' '0')
$linkChips = Field $d 'linkChips' ''
$ok = -not (($crossFired -ge 1) -and ($linkChips -eq 'copy-image'))
Add-Check 'defect:preview-chip-cross-kind' $(if ($ok) { 'PASS' } else { 'FAIL' }) "link preview keeps the image chip and it fires: chips=$linkChips fired=$crossFired"

$eScale = ConvertTo-InvariantDouble (Field $e 'scale' '-1')
$eOrigin = Field $e 'origin' '?'
$ok = ($eScale -ge 0) -and ($eScale -le 1.001) -and ($eOrigin -eq '0.000;0.000')
Add-Check 'defect:preview-stale-zoom' $(if ($ok) { 'PASS' } else { 'FAIL' }) "next image preview opens with the last zoom/pan: scale=$eScale origin=$eOrigin (pan left $(Field $e 'originAfterPan' '?'))"

$fPinned = (Field $f 'pinnedAfterRemove' '?') -eq 'True'
$fGlyph = Field $f 'glyph' '?'
$ok = $fPinned -or ($fGlyph -ne 'full')
Add-Check 'defect:preview-pin-glyph-stale' $(if ($ok) { 'PASS' } else { 'FAIL' }) "after forced takedown the glyph still reads full: pinned=$fPinned glyph=$fGlyph"

$whenVisible = (Field $f 'whenVisible' '?') -eq 'True'
$kindVisible = (Field $f 'kindVisible' '?') -eq 'True'
$ok = $whenVisible -and $kindVisible
Add-Check 'defect:preview-meta-missing' $(if ($ok) { 'PASS' } else { 'FAIL' }) "unpinned preview after a forced takedown lost its meta row: when=$whenVisible kind=$kindVisible"

$pinnedBefore = (Field $f 'pinnedBefore' '?') -eq 'True'
$pinnedAfter = (Field $f 'pinnedAfterClick' '?') -eq 'True'
$glyphBefore = Field $f 'glyphBeforeClick' '?'
$ok = $pinnedBefore -or ($glyphBefore -ne 'full') -or (-not $pinnedAfter)
Add-Check 'defect:preview-pin-click-inverted' $(if ($ok) { 'PASS' } else { 'FAIL' }) "glyph said pinned but the first click pins (inverted): before=$pinnedBefore glyph=$glyphBefore after=$pinnedAfter"

$editStill = (Field $g 'editStillVisible' '?') -eq 'True'
$ok = -not $editStill
Add-Check 'defect:preview-esc-no-exit' $(if ($ok) { 'PASS' } else { 'FAIL' }) "Esc in the edit box left it editing: editStillVisible=$editStill"

$gScale = ConvertTo-InvariantDouble (Field $g 'scaleAfterDblClick' '-1')
$ok = ($gScale -ge 0) -and ($gScale -le 1.001)
Add-Check 'defect:preview-dblclick-no-reset' $(if ($ok) { 'PASS' } else { 'FAIL' }) "double-click left the zoom at: $gScale"

# --- feature: the pinned preview drags by its body, and keeps its place -------
$h = Get-Step 'H-window-drag'
function Steps-XY([string]$Text) {
    if ($Text -eq '?') { return @() }
    return $Text -split ';'
}
$xy0 = Steps-XY (Field $h 'xy0' '?')
$xy1 = Steps-XY (Field $h 'xy1' '?')
$xy2 = Steps-XY (Field $h 'xy2' '?')
$ok = $false
$detail = 'no H-window-drag step line'
if (($xy0.Count -eq 2) -and ($xy1.Count -eq 2)) {
    $dx = [int]$xy1[0] - [int]$xy0[0]
    $dy = [int]$xy1[1] - [int]$xy0[1]
    $ok = ([math]::Abs($dx) -ge 20) -and ([math]::Abs($dy) -ge 15)
    $detail = "dragging the pinned preview moved it by ${dx};${dy} px"
}
Add-Check 'flow:preview-pin-drag' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$ok = $false
$detail = 'no H-window-drag step line'
if (($xy1.Count -eq 2) -and ($xy2.Count -eq 2)) {
    $rx = [int]$xy2[0] - [int]$xy1[0]
    $ry = [int]$xy2[1] - [int]$xy1[1]
    $ok = ([math]::Abs($rx) -le 3) -and ([math]::Abs($ry) -le 3)
    $detail = "re-show kept the dragged position (drift ${rx};${ry} px)"
}
Add-Check 'flow:preview-pin-drag-persists' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
