# probe-capture-restore.ps1 - the clipboard a capture borrows comes back, on a real
# clipboard that other programs are busy reading (user report 2026-10-15: with the
# translate badge and the drag-select badge on, "could not restore your clipboard"
# kept popping up).
#
# NOT part of run-all.ps1, on purpose: it drives a real Notepad window and replaces
# the clipboard for about a minute. Run it on its own, and only when nobody is
# working at the machine (it stands down - SKIP - when it sees the cursor move;
# pass -Force to run anyway).
#
# The flow (SHIYU_PROBE_CMD=capture-restore; a probe installs no mouse hook, so the
# drag path is entered directly, with Notepad in front and its text selected):
#
#   1. put a known text on the clipboard, open Notepad on a temp file holding one
#      English sentence, select all;
#   2. start the probe instance with SHIYU_PROBE_CLIPBOARD=1 (its clipboard monitor
#      runs, as in real use). The command turns the drag badge on, runs four
#      scenarios and writes what it saw to capture-restore.log. "Another program
#      reading the clipboard" is a second thread of the probe holding the
#      clipboard open: Shiyu's window cannot open it then, exactly as when another
#      process holds it.
#
# Checks:
#   - flow:capture-drag-captures          the drag path took the sentence and showed the badge
#   - flow:capture-restore-waits-busy     the fade met a clipboard held for 700 ms, waited it
#                                         out and put the known text back
#   - flow:capture-keeps-own-copy         a Ctrl+C of one's own after the drag survives the fade
#   - flow:capture-skips-screenshot       a drag over a picture on the clipboard sends no Ctrl+C
#                                         and leaves the picture where it was
#   - flow:capture-hotkey-under-contention  8 hotkey-path captures (capture, then restore at
#                                         once) while two simulated readers grab the clipboard
#                                         after every change: none fails to restore. The same
#                                         8 rounds the old way (no read or restore retries) are
#                                         reported alongside for comparison.
#   - flow:capture-no-failure-logged      no ClipboardRestoreFailed in the probe's own log
#
# Side effects worth knowing: a running REAL Shiyu instance sees these clipboard
# writes too (they are ordinary copies to it) and may record the probe sentence, the
# known text and a 2x2 picture into the real history - delete them by hand. A
# Windows 11 Notepad may keep the temp file as a restored tab; close it.
#
# ASCII only (see lib.ps1 header).

param(
    [string]$Exe = '',
    [switch]$Force
)

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$CheckNames = @(
    'flow:capture-drag-captures', 'flow:capture-restore-waits-busy', 'flow:capture-keeps-own-copy',
    'flow:capture-skips-screenshot', 'flow:capture-hotkey-under-contention', 'flow:capture-no-failure-logged')

function Skip-All([string]$Reason) {
    foreach ($name in $CheckNames) { Add-Check $name 'SKIP' $Reason }
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class CaptureProbeWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@ -ErrorAction SilentlyContinue

$KnownClip = 'SHIYU-PROBE capture-restore original clipboard text'
$Sentence = 'Shiyu probe sentence: the quick brown fox jumps over the lazy dog.'

# --- guards: never run over a human, over their Notepad, or over their clipboard ---

if (-not $Force -and (Test-UserPresent)) {
    Skip-All 'user present (cursor moved); this probe drives a real window - rerun with -Force if you mean it'
    return $script:Checks
}

if (@(Get-Process -Name 'notepad' -ErrorAction SilentlyContinue).Count -gt 0) {
    Skip-All 'a Notepad is already running; refusing to share an instance with the user''s windows'
    return $script:Checks
}

Add-Type -AssemblyName System.Windows.Forms
$clipboardIsText = $true
try {
    if ([System.Windows.Forms.Clipboard]::ContainsImage() -or
        [System.Windows.Forms.Clipboard]::ContainsFileDropList()) {
        $clipboardIsText = $false
    }
} catch {
    $clipboardIsText = $false
}
if (-not $clipboardIsText) {
    Skip-All 'the clipboard holds an image or files (or could not be read); refusing to overwrite it'
    return $script:Checks
}

# --- the run ------------------------------------------------------------------

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\capture-restore'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'capture-restore.log'
Remove-Item $log -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dataDir 'logs') -Recurse -Force -ErrorAction SilentlyContinue

$savedClipboard = $null
try { $savedClipboard = Get-Clipboard -Raw } catch { }

$targetFile = Join-Path $env:TEMP 'shiyu-probe-run\capture-restore-target.txt'
New-Item -ItemType Directory -Force -Path (Split-Path $targetFile) | Out-Null
Set-Content -Path $targetFile -Value $Sentence -Encoding ASCII -NoNewline

$notepad = $null
$p = $null
$fields = @{}
try {
    Set-Clipboard -Value $KnownClip

    $notepad = Start-Process -FilePath 'notepad.exe' -ArgumentList ('"' + $targetFile + '"') -PassThru
    $npWindow = $null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not $npWindow) {
        Start-Sleep -Milliseconds 500
        $npWindow = @(Get-Process -Name 'notepad' -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })[0]
    }
    if (-not $npWindow) {
        Skip-All 'Notepad did not open a window'
        return $script:Checks
    }
    Start-Sleep -Milliseconds 800
    [void][CaptureProbeWin]::SetForegroundWindow($npWindow.MainWindowHandle)
    Start-Sleep -Milliseconds 500
    if ([CaptureProbeWin]::GetForegroundWindow() -ne $npWindow.MainWindowHandle) {
        Skip-All 'could not bring Notepad to the foreground (focus lock); nothing was sent'
        return $script:Checks
    }
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 300

    $p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'capture-restore' -ExtraEnv @{
        SHIYU_PROBE_CLIPBOARD = '1'
    }

    $deadline = (Get-Date).AddSeconds(150)
    $done = $false
    while ((Get-Date) -lt $deadline -and -not $done) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $log) {
            $done = @(Get-Content $log -Encoding UTF8 | Where-Object { $_ -eq 'done' }).Count -gt 0
        }
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
    # Only the Notepad this run started; never anything that was already there
    # (a running Notepad is a SKIP above).
    if ($notepad) {
        Get-Process -Name 'notepad' -ErrorAction SilentlyContinue |
            ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($null -ne $savedClipboard) {
        try { Set-Clipboard -Value $savedClipboard } catch { }
    } else {
        try { [System.Windows.Forms.Clipboard]::Clear() } catch { }
    }
}

if (Test-Path $log) {
    foreach ($line in Get-Content $log -Encoding UTF8) {
        $parts = $line.Split('|')
        $values = @{}
        foreach ($pair in $parts[1..($parts.Count - 1)]) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $values[$kv[0]] = $kv[1] }
        }
        $fields[$parts[0]] = $values
    }
}

if (-not $fields.ContainsKey('done')) {
    Skip-All 'the probe command did not finish (no "done" in capture-restore.log within 150s)'
    return $script:Checks
}

if ($fields['start']['original'] -ne 'True') {
    Skip-All 'the probe instance did not find the known text on the clipboard; nothing was measured'
    return $script:Checks
}

$fade = $fields['fade']
if ($fade['badge'] -eq 'True' -and $fade['captured'] -eq 'True') {
    Add-Check 'flow:capture-drag-captures' 'PASS' 'Notepad answered the Ctrl+C and the badge came up'
} else {
    Add-Check 'flow:capture-drag-captures' 'FAIL' ('badge={0} captured={1}' -f $fade['badge'], $fade['captured'])
}

$fadeDetail = 'held={0} first try met it={1} still owing after 3s={2} restored={3}' -f `
    $fade['held'], $fade['waiting'], $fade['owingAfter'], $fade['restored']
if ($fade['held'] -eq 'True' -and $fade['waiting'] -eq 'True' -and
    $fade['owingAfter'] -eq 'False' -and $fade['restored'] -eq 'True') {
    Add-Check 'flow:capture-restore-waits-busy' 'PASS' $fadeDetail
} else {
    Add-Check 'flow:capture-restore-waits-busy' 'FAIL' $fadeDetail
}

if ($fields['own-copy']['kept'] -eq 'True') {
    Add-Check 'flow:capture-keeps-own-copy' 'PASS' 'the hand-made copy of the selection is still on the clipboard after the fade'
} else {
    Add-Check 'flow:capture-keeps-own-copy' 'FAIL' 'the fade put the old clipboard back over the hand-made copy'
}

$image = $fields['image']
$imageDetail = 'picture set={0} clipboard untouched={1} picture kept={2}' -f $image['set'], $image['untouched'], $image['kept']
if ($image['set'] -ne 'True') {
    Add-Check 'flow:capture-skips-screenshot' 'SKIP' $imageDetail
} elseif ($image['untouched'] -eq 'True' -and $image['kept'] -eq 'True') {
    Add-Check 'flow:capture-skips-screenshot' 'PASS' $imageDetail
} else {
    Add-Check 'flow:capture-skips-screenshot' 'FAIL' $imageDetail
}

$single = $fields['hotkey-single']
$retry = $fields['hotkey-retry']
$hotkeyDetail = 'with retries: {0}/{1} restore failures, clipboard wrong {2}, captured {3}; old behaviour (no retries): {4}/{5} restore failures, wrong {6}, captured {8}; reader holds {7}' -f `
    $retry['restoreFailed'], $retry['rounds'], $retry['clipboardWrong'], $retry['captured'],
    $single['restoreFailed'], $single['rounds'], $single['clipboardWrong'], $retry['holds'], $single['captured']
if ($retry['restoreFailed'] -eq '0' -and $retry['clipboardWrong'] -eq '0') {
    Add-Check 'flow:capture-hotkey-under-contention' 'PASS' $hotkeyDetail
} else {
    Add-Check 'flow:capture-hotkey-under-contention' 'FAIL' $hotkeyDetail
}

$failures = 0
foreach ($file in @(Get-ChildItem (Join-Path $dataDir 'logs') -Filter '*.log' -ErrorAction SilentlyContinue)) {
    $failures += @(Get-Content $file.FullName -Encoding UTF8 | Where-Object { $_ -match 'ClipboardRestoreFailed' }).Count
}
if ($failures -eq 0) {
    Add-Check 'flow:capture-no-failure-logged' 'PASS' 'no ClipboardRestoreFailed in the probe log'
} else {
    Add-Check 'flow:capture-no-failure-logged' 'FAIL' ('{0} ClipboardRestoreFailed line(s) in the probe log' -f $failures)
}

return $script:Checks
