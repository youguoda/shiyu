# probe-translation-log.ps1 - both translation boxes can be dragged, every translation
# is logged, and the log clears on its own schedule (user request 2026-10-09).
#
# The DEBUG command "translation-log" (with SHIYU_FAKE_BACKEND=1: a deterministic
# "[EN] " + text, no network) translates one sentence in the panel, one in the reverse
# input box, moves the reverse box far away and makes it grow, closes it the Esc way,
# then opens the library on its translation-log page and the settings window on the
# translation-log section.
#
# The system move loop needs a real mouse button; the probe never touches the user's
# mouse. What it checks is both ends of a drag: where a press starts one (the grip
# test) and what happens after one ends (the box keeps the dropped position).
#
# Checks:
#   - flow:log-panel        a finished panel translation is logged, origin Panel
#   - flow:log-reverse      closing the reverse box with a settled result logs it,
#                           origin ReverseInput, the final text (not a half-typed one)
#   - flow:drag-grip        the hint line is a grip; the input, the output and both
#                           chips keep their own presses
#   - flow:panel-grip       the panel's texts are grips; its buttons and the template
#                           list keep their own presses
#   - flow:drag-kept        after a drag the box grows from where it was dropped
#                           instead of snapping back beside the caret
#   - flow:log-library      the library opens on the log page and lists both records
#   - flow:log-settings     the settings row counts the records
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\translation-log'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'translation-log.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'translation-log' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:log-panel' 'FAIL' 'the command wrote no log within 60s'
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
    Add-Check 'flow:log-panel' 'FAIL' ("the command threw {0}" -f $lines['error']['type'])
    return $script:Checks
}

$panel = $lines['panel']
$ok = $panel -and $panel['logged'] -eq 'True' -and $panel['origin'] -eq 'Panel' `
    -and $panel['translated'] -eq '[EN] probe rollout plan' -and $panel['template'].Length -gt 0
$detail = if ($panel) { "logged {0}, origin {1}, {2} record(s), translated '{3}'" -f $panel['logged'], $panel['origin'], $panel['records'], $panel['translated'] } else { 'no panel line' }
Add-Check 'flow:log-panel' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# The box grew to four lines before it closed: the logged original is that final text,
# flattened to one line for the log file.
$reverse = $lines['reverse']
$final = (@('probe reverse sentence') * 4) -join ' '
$ok = $reverse -and $reverse['logged'] -eq 'True' -and $reverse['origin'] -eq 'ReverseInput' `
    -and $reverse['records'] -eq '2' -and $reverse['original'] -eq $final
$detail = if ($reverse) { "logged {0}, origin {1}, {2} record(s), original '{3}'" -f $reverse['logged'], $reverse['origin'], $reverse['records'], $reverse['original'] } else { 'no reverse line' }
Add-Check 'flow:log-reverse' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$grip = $lines['grip']
$ok = $grip -and $grip['hint'] -eq 'True' -and $grip['input'] -eq 'False' -and $grip['output'] -eq 'False' `
    -and $grip['template'] -eq 'False' -and $grip['direction'] -eq 'False'
$detail = if ($grip) { "hint {0}, input {1}, output {2}, template chip {3}, direction chip {4}" -f $grip['hint'], $grip['input'], $grip['output'], $grip['template'], $grip['direction'] } else { 'no grip line' }
Add-Check 'flow:drag-grip' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$panelGrip = $lines['panelGrip']
$ok = $panelGrip -and $panelGrip['original'] -eq 'True' -and $panelGrip['label'] -eq 'True' `
    -and $panelGrip['copy'] -eq 'False' -and $panelGrip['close'] -eq 'False' -and $panelGrip['list'] -eq 'False'
$detail = if ($panelGrip) { "original {0}, language label {1}, copy {2}, close {3}, template list {4}" -f $panelGrip['original'], $panelGrip['label'], $panelGrip['copy'], $panelGrip['close'], $panelGrip['list'] } else { 'no panelGrip line' }
Add-Check 'flow:panel-grip' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$drag = $lines['drag']
$ok = $false
$detail = 'no drag line'
if ($drag) {
    $from = $drag['from'].Split(',')
    $target = $drag['target'].Split(',')
    $after = $drag['after'].Split(',')
    $grew = [int]$after[2] -gt [int]$from[2]
    $ok = $drag['settled'] -eq 'True' -and $grew -and $after[0] -eq $target[0] -and $after[1] -eq $target[1]
    $detail = "dropped at {0},{1}; after growing {2}->{3} px it sits at {4},{5} (started at {6},{7})" -f `
        $target[0], $target[1], $from[2], $after[2], $after[0], $after[1], $from[0], $from[1]
}
Add-Check 'flow:drag-kept' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$library = $lines['library']
$ok = $library -and $library['logPage'] -eq 'True' -and $library['logTab'] -eq 'True' -and $library['items'] -eq '2' `
    -and $library['count'] -eq '2 / 2' -and $library['historyVisible'] -eq 'False' -and $library['empty'] -eq 'False'
$detail = if ($library) { "log page {0}, {1} item(s), count '{2}', history visible {3}" -f $library['logPage'], $library['items'], $library['count'], $library['historyVisible'] } else { 'no library line' }
Add-Check 'flow:log-library' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$settings = $lines['settings']
$ok = $settings -and $settings['count'] -eq 'True'
$detail = if ($settings) { "count shown {0}" -f $settings['count'] } else { 'no settings line' }
Add-Check 'flow:log-settings' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
