# probe-batch-paste.ps1 - "paste all" in the history window (review 2026-10-16 F-12;
# acceptance delegated by the user on 2026-10-17). NOT part of run-all: it writes the REAL
# clipboard and drives a real Notepad window.
#
# What it does:
#   1. saves your clipboard TEXT (Get-Clipboard) and restores it at the end; if your
#      clipboard holds an image, that image is NOT saved - copy it again afterwards;
#   2. starts an empty Notepad and brings it to the front (if Notepad is already running,
#      the probe opens a tab there and leaves that tab in place at the end);
#   3. opens the history window (so it records Notepad as the paste-back target), creates
#      three ASCII entries, selects them in a NON-list order, presses Paste all;
#   4. reads the text back out of Notepad and checks the order.
#
# Checks:
#   - flow:batch-paste-target    the paste-back target was Notepad and it took the foreground
#   - flow:batch-paste-order     Notepad ends up with the three lines in selection order
#
# If the desktop is busy (another window steals the foreground) the probe aborts without
# pasting rather than paste into some unknown window.
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$saved = $null
try { $saved = Get-Clipboard -Format Text -TextFormatType UnicodeText -ErrorAction SilentlyContinue } catch { }

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\batch-paste'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'batch-paste.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'batch-paste'
try {
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
    if ($null -ne $saved) { Set-Clipboard -Value $saved }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:batch-paste-target' 'FAIL' 'the command wrote no log within 90s'
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

$target = $lines['target']
$ok = $target -and $target['ok'] -eq 'True'
$detail = if ($target) { "paste-back target took the foreground {0}" -f $target['ok'] } else { 'no target line' }
Add-Check 'flow:batch-paste-target' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$pasted = $lines['pasted']
$ok = $pasted -and $pasted['ok'] -eq 'True'
$detail = if ($pasted) { "notepad holds: {0}" -f $pasted['actual'] } else { 'no pasted line' }
Add-Check 'flow:batch-paste-order' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
