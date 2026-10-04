# probe-reverse.ps1 - the reverse input box, end to end (ticket 43).
#
# NOT part of run-all.ps1, on purpose: it types into a real Notepad window and
# replaces the clipboard for a few seconds. Run it on its own, and only when
# nobody is working at the machine (it stands down - SKIP - when it sees the
# cursor move; pass -Force to run anyway).
#
# The flow (SHIYU_PROBE_CMD=reverse; probes own no global hotkey, so the box is
# opened by the command itself, with Notepad in the foreground first):
#
#   1. seed a probe data dir (N synthetic entries), put a known text on the
#      clipboard, start Notepad on a temp file and bring it to the front;
#   2. start the probe instance with SHIYU_FAKE_BACKEND=1 (a deterministic fake
#      backend that answers "[EN] " + the original text - no network) and
#      SHIYU_PROBE_CLIPBOARD=1 (the clipboard monitor stays on in probe mode,
#      so "no history entry from the paste" is a real assertion);
#   3. type the two characters "ni hao" into the box (UIA ValuePattern), wait for
#      the output "[EN] " + the same two characters, press Enter;
#   4. read Notepad back through UIA: it must contain the output.
#
# Checks:
#   - window:reverse-found                  the 520-DIP box opened
#   - flow:reverse-output                   the fake backend's answer reached the box
#   - a11y:reverse-all-controls-named       zero unnamed interactive elements
#   - flow:reverse-paste                    Notepad received the output on Enter
#   - flow:reverse-clipboard-restored       the clipboard is back to the known text
#   - flow:reverse-history-unchanged        the paste and the restore left no history entry
#
# Side effects worth knowing: a running REAL Shiyu instance sees these clipboard
# writes too (they are ordinary copies to it) and will record up to two probe
# strings into the real history - delete them by hand. A Windows 11 Notepad may
# keep the pasted, unsaved text as a restored tab; close that tab without saving.
#
# ASCII only (see lib.ps1 header): the Chinese text is built from code points.

param(
    [string]$Exe = '',
    [switch]$Force
)

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$CheckNames = @(
    'window:reverse-found', 'flow:reverse-output', 'a11y:reverse-all-controls-named',
    'flow:reverse-paste', 'flow:reverse-clipboard-restored', 'flow:reverse-history-unchanged')

function Skip-All([string]$Reason) {
    foreach ($name in $CheckNames) { Add-Check $name 'SKIP' $Reason }
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class ReverseProbeWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@ -ErrorAction SilentlyContinue

# What the user is typing, as code points: "ni hao" (two Han characters).
$Typed = New-Zh @(0x4F60, 0x597D)
$Expected = '[EN] ' + $Typed
$KnownClip = 'SHIYU-PROBE-43 original clipboard text'

# --- guards: never run over a human, over their Notepad, or over their clipboard ---

if (-not $Force -and (Test-UserPresent)) {
    Skip-All 'user present (cursor moved); this probe types into a real window - rerun with -Force if you mean it'
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

# --- helpers ------------------------------------------------------------------

function Read-ElementText($Element) {
    try {
        $value = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($value) { return [string]$value.Current.Value }
    } catch { }
    try {
        $text = $Element.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        if ($text) { return [string]$text.DocumentRange.GetText(-1) }
    } catch { }
    return $null
}

# Everything Notepad shows, from every top-level window of the given pids.
function Get-NotepadText([int[]]$Pids) {
    $all = ''
    foreach ($id in $Pids) {
        foreach ($window in (Get-UiaWindowsOfPid $id)) {
            foreach ($item in (Get-UiaTree $window)) {
                if ($item.Type -in @('Document', 'Edit')) {
                    $read = Read-ElementText $item.Element
                    if ($read) { $all += $read }
                }
            }
        }
    }
    return $all
}

# The box is 520 DIP wide; match it at any common monitor scale.
function Get-ReverseTree([int]$ProcessId) {
    foreach ($window in (Get-UiaWindowsOfPid $ProcessId)) {
        $rect = $window.Current.BoundingRectangle
        foreach ($scale in @(1.0, 1.25, 1.5, 1.75, 2.0)) {
            if ([Math]::Abs($rect.Width - 520 * $scale) -le 12) {
                return [pscustomobject]@{ Window = $window; Tree = (Get-UiaTree $window) }
            }
        }
    }
    return $null
}

# --- the run ------------------------------------------------------------------

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\reverse'
$seedOutput = & (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-String
$seeded = if ($seedOutput -match 'total=(\d+)') { [int]$Matches[1] } else { -1 }

$savedClipboard = $null
try { $savedClipboard = Get-Clipboard -Raw } catch { }

$targetFile = Join-Path $env:TEMP 'shiyu-probe-run\reverse-target.txt'
New-Item -ItemType Directory -Force -Path (Split-Path $targetFile) | Out-Null
Set-Content -Path $targetFile -Value '' -Encoding ASCII

$notepad = $null
$p = $null
try {
    # The known clipboard text goes up BEFORE the probe app starts: its monitor
    # only sees changes made while it listens, so this copy is never recorded
    # and the history count stays comparable with the seeded one.
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
    [void][ReverseProbeWin]::SetForegroundWindow($npWindow.MainWindowHandle)
    Start-Sleep -Milliseconds 500
    if ([ReverseProbeWin]::GetForegroundWindow() -ne $npWindow.MainWindowHandle) {
        Skip-All 'could not bring Notepad to the foreground (focus lock); nothing was sent'
        return $script:Checks
    }

    $p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'reverse' -ExtraEnv @{
        SHIYU_FAKE_BACKEND = '1'
        SHIYU_PROBE_CLIPBOARD = '1'
    }

    $hwnd = Wait-ProbeWindow $p.Id 520
    if ($hwnd -eq [IntPtr]::Zero) {
        Add-Check 'window:reverse-found' 'FAIL' 'no 520-DIP window of the probe pid'
        foreach ($name in $CheckNames[1..5]) { Add-Check $name 'SKIP' 'the box did not open' }
        return $script:Checks
    }
    Add-Check 'window:reverse-found' 'PASS' ("hwnd={0}" -f $hwnd)
    Start-Sleep -Milliseconds 800

    # --- type, wait for the fake backend's answer -----------------------------
    $box = Get-ReverseTree $p.Id
    $edit = if ($box) { @($box.Tree | Where-Object { $_.Type -eq 'Edit' })[0] } else { $null }
    if (-not $edit) {
        Add-Check 'flow:reverse-output' 'FAIL' 'no Edit element in the box (input missing from the UIA tree)'
        foreach ($name in $CheckNames[2..5]) { Add-Check $name 'SKIP' 'no input to type into' }
        return $script:Checks
    }
    $edit.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Typed)

    $answered = $false
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline -and -not $answered) {
        Start-Sleep -Milliseconds 400
        $box = Get-ReverseTree $p.Id
        if ($box -and @($box.Tree | Where-Object { $_.Type -eq 'Text' -and $_.Name -eq $Expected }).Count -ge 1) {
            $answered = $true
        }
    }
    $shot = Get-WindowShot $hwnd
    if ($shot) { [void](Save-Shot $shot 'reverse-output') }
    if ($answered) {
        Add-Check 'flow:reverse-output' 'PASS' 'the box shows "[EN] " + the typed text (300ms debounce, fake backend)'
    } else {
        Add-Check 'flow:reverse-output' 'FAIL' 'no Text element named "[EN] " + the typed text within 12s'
    }

    # --- a11y: every interactive element carries a name ------------------------
    $interactive = @()
    if ($box) {
        $interactive = @($box.Tree | Where-Object {
            $_.Type -in @('Button', 'CheckBox', 'RadioButton', 'ComboBox', 'ListItem', 'TabItem', 'Edit') `
            -and $_.ClassName -ne 'RepeatButton' })
    }
    $unnamed = @($interactive | Where-Object { [string]::IsNullOrWhiteSpace($_.Name) })
    $a11yDetail = '{0} interactive elements, {1} unnamed' -f $interactive.Count, $unnamed.Count
    if ($interactive.Count -gt 0 -and $unnamed.Count -eq 0) {
        Add-Check 'a11y:reverse-all-controls-named' 'PASS' $a11yDetail
    } else {
        Add-Check 'a11y:reverse-all-controls-named' 'FAIL' $a11yDetail
    }

    # --- Enter: paste into Notepad ---------------------------------------------
    # Enter is posted to the BOX (not a global keystroke); the box then hides,
    # restores the foreground to Notepad and pastes - the very path under test.
    if (-not $answered) {
        foreach ($name in $CheckNames[3..5]) { Add-Check $name 'SKIP' 'no output to paste' }
        return $script:Checks
    }
    Send-ProbeKey $hwnd 0x0D
    Start-Sleep -Milliseconds 80
    Send-ProbeKey $hwnd 0x0D -KeyUp

    # Paste lands at once; the restore waits 400ms. Give both room.
    Start-Sleep -Milliseconds 2000
    $npText = [string](Get-NotepadText @($npWindow.Id))
    # Contains, not -like: the expected text starts with "[EN]", which -like
    # would read as a wildcard character class.
    if ($npText.Contains($Expected)) {
        Add-Check 'flow:reverse-paste' 'PASS' 'Notepad contains the output'
    } else {
        Add-Check 'flow:reverse-paste' 'FAIL' ('Notepad does not contain the output (read {0} chars)' -f $npText.Length)
    }

    $after = $null
    try { $after = Get-Clipboard -Raw } catch { }
    if ($null -ne $after -and $after.Trim() -eq $KnownClip) {
        Add-Check 'flow:reverse-clipboard-restored' 'PASS' 'the clipboard is back to the known text'
    } else {
        $holds = '(nothing)'
        if ($null -ne $after) { $holds = $after.Trim() }
        Add-Check 'flow:reverse-clipboard-restored' 'FAIL' ('clipboard now holds: ' + $holds)
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

# --- history: the paste and the restore are transport, not copies -----------------
# The probe app has exited; reading is safe now.
$countTool = Join-Path $env:TEMP 'shiyu-probe-seed\out\shiyu_seedtool.dll'
if ((Test-Path $countTool) -and $seeded -ge 0) {
    $line = (& dotnet $countTool --count $dataDir | Out-String)
    $counted = -1
    if ($line -match 'count=(\d+)') { $counted = [int]$Matches[1] }
    $historyDetail = 'seeded {0}, now {1} (the write and the restore must not be recorded)' -f $seeded, $counted
    if ($counted -eq $seeded) {
        Add-Check 'flow:reverse-history-unchanged' 'PASS' $historyDetail
    } else {
        Add-Check 'flow:reverse-history-unchanged' 'FAIL' $historyDetail
    }
} else {
    Add-Check 'flow:reverse-history-unchanged' 'SKIP' 'seed tool or seeded total unavailable'
}

return $script:Checks
