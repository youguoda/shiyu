# probe-reverse-select.ps1 - the reverse input box's finished output can be selected
# with the mouse and copied (user request 2026-10-05).
#
# The DEBUG command "reverse-select" (with SHIYU_FAKE_BACKEND=1: a deterministic
# "[EN] " + input, no network) puts a sentence into the box, waits for the result,
# and checks that the settled output is the read-only selectable box rather than the
# streaming label. It then selects the first characters and asks whether Copy can
# execute - asked, never executed, so the user's clipboard is never touched.
# reverse-select.png lands in the data dir for a human look.
#
# Checks:
#   - flow:reverse-output-selectable      the settled output is the read-only box; the label is gone
#   - flow:reverse-output-copyable        the selection holds the expected text and Copy can execute
#   - flow:reverse-output-same-line-pitch the box's line pitch equals the streaming label's, so a
#                                         multi-line result does not jump when it settles
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\reverse-select'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'reverse-select.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'reverse-select' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:reverse-output-selectable' 'FAIL' 'the command wrote no log within 40s'
    Add-Check 'flow:reverse-output-copyable' 'SKIP' 'nothing to select'
    return $script:Checks
}

$settled = $null
$selected = $null
$pitch = $null
foreach ($line in Get-Content $log) {
    if ($line -match '^settled\|box=(\w+)\|readOnly=(\w+)\|label=(\w+)\|text=(.*)$') {
        $settled = @{ Box = $Matches[1]; ReadOnly = $Matches[2]; Label = $Matches[3]; Text = $Matches[4] }
    } elseif ($line -match '^pitch\|box=([^|]+)\|label=([^|]+)\|lines=(\d+)$') {
        $pitch = @{ Box = $Matches[1]; Label = $Matches[2]; Lines = [int]$Matches[3] }
    } elseif ($line -match '^selected\|text=(.*)\|canCopy=(\w+)$') {
        $selected = @{ Text = $Matches[1]; CanCopy = $Matches[2] }
    }
}

$expected = '[EN] hello probe, this sentence runs long enough to wrap onto a second line of the reverse box'
$ok = $settled -and $settled.Box -eq 'True' -and $settled.ReadOnly -eq 'True' -and $settled.Label -eq 'False' -and $settled.Text -eq $expected
$detail = if ($settled) { "box {0}, read-only {1}, label {2}, text '{3}'" -f $settled.Box, $settled.ReadOnly, $settled.Label, $settled.Text } else { 'no settled line' }
Add-Check 'flow:reverse-output-selectable' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$copyable = $selected -and $selected.Text -eq '[EN]' -and $selected.CanCopy -eq 'True'
$detail = if ($selected) { "selected '{0}', copy can execute: {1}" -f $selected.Text, $selected.CanCopy } else { 'no selection line' }
Add-Check 'flow:reverse-output-copyable' $(if ($copyable) { 'PASS' } else { 'FAIL' }) $detail

# Same line pitch as the streaming label, or a multi-line result jumps when it settles.
if (-not $pitch -or $pitch.Lines -lt 2) {
    Add-Check 'flow:reverse-output-same-line-pitch' 'FAIL' 'the output never wrapped onto a second line'
} else {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $gap = [Math]::Abs([double]::Parse($pitch.Box, $inv) - [double]::Parse($pitch.Label, $inv))
    Add-Check 'flow:reverse-output-same-line-pitch' $(if ($gap -le 0.5) { 'PASS' } else { 'FAIL' }) `
        ("{0} lines; selectable box pitch {1} DIP, streaming label {2} DIP" -f $pitch.Lines, $pitch.Box, $pitch.Label)
}

return $script:Checks
