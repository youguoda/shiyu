# probe-window-resize.ps1 - drag the bottom-right corner of the translation panel, the reverse
# input box and the narrow bar (user request 2026-10-28).
#
# The DEBUG command "window-resize" opens each window (the panel and the reverse box pinned so they
# stay put), raises its corner grip's DragStarted / DragDelta / DragCompleted, and logs the size
# while dragging, after release, the height cap, and what the settings remembered. No real input.
#
# Rules: the width is remembered; for the panel and the reverse box the dragged height becomes a
# cap - with little content the window goes back to fitting it; the bar is a list, so its height
# is the dragged height.
#
# Checks:
#   - flow:resize-panel     follows the drag, keeps the width, fits its content under the cap, saved
#   - flow:resize-reverse   the same for the reverse input box (cap shows in MaxHeight)
#   - flow:resize-bar       width and height follow the drag and are saved
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$names = 'flow:resize-panel', 'flow:resize-reverse', 'flow:resize-bar'

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\window-resize'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'window-resize.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'window-resize' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    foreach ($n in $names) { Add-Check $n 'FAIL' 'the command wrote no log within 90s' }
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
    foreach ($n in $names) { Add-Check $n 'FAIL' ("the command threw {0}: {1}" -f $lines['error']['type'], $lines['error']['message']) }
    return $script:Checks
}

function Size([string]$Text) {
    if ($Text -match '^(-?\d+)x(-?\d+)$') { return @([double]$Matches[1], [double]$Matches[2]) }
    return @(-1, -1)
}

function Near([double]$A, [double]$B, [double]$Slack = 2) { return [math]::Abs($A - $B) -le $Slack }

function Show($Key) { if ($lines[$Key]) { ($lines[$Key].GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ' } else { "no $Key line" } }

# Panel / reverse: the drag shows (+dx, +dy) live; after release the width stays, the height is
# back to fitting the short content (at most the dragged height), and both are saved.
foreach ($case in @(@{ Key = 'panel'; Check = 'flow:resize-panel'; Dx = 120; Dy = 260 }, @{ Key = 'reverse'; Check = 'flow:resize-reverse'; Dx = 100; Dy = 240 })) {
    $l = $lines[$case.Key]
    $ok = $false
    if ($l -and $l['before']) {
        $b = Size $l['before']; $d = Size $l['during']; $a = Size $l['after']
        $ok = (Near $d[0] ($b[0] + $case.Dx)) -and (Near $d[1] ($b[1] + $case.Dy)) `
            -and (Near $a[0] $d[0]) -and ($a[1] -le $d[1] + 1) `
            -and (Near ([double]$l['savedWidth']) $d[0]) -and (Near ([double]$l['savedMax']) $d[1])
        if ($case.Key -eq 'reverse') { $ok = $ok -and (Near ([double]$l['maxHeight']) $d[1]) }
    }
    Add-Check $case.Check $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show $case.Key)
}

$l = $lines['bar']
$ok = $false
if ($l -and $l['before']) {
    $b = Size $l['before']; $d = Size $l['during']; $a = Size $l['after']
    $ok = (Near $d[0] ($b[0] + 100)) -and (Near $d[1] ($b[1] - 80)) -and (Near $a[0] $d[0]) -and (Near $a[1] $d[1]) `
        -and (Near ([double]$l['savedWidth']) $d[0]) -and (Near ([double]$l['savedHeight']) $d[1])
}
Add-Check 'flow:resize-bar' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show 'bar')

return $script:Checks
