# probe-preview.ps1 - the preview panel beside the bar (ticket 20 / U-03). Took
# over from probe-connector.ps1 when the connector line and its bridge were
# removed at the user's request (2026-10-05).
#
# Parks the bar low on the primary screen so the placement must clamp the panel
# upward - the arrangement that used to earn the connector curve - selects the
# long-text row and holds Space, then checks:
#   - flow:preview-opens          Space opened the preview window
#   - flow:preview-no-connector   the probe pid shows nothing besides the bar and
#                                 the panel: no connector sheet, in any layout
#   - defect:preview-bar-overlap  the panel never overlaps the bar (U-03 P1: two
#                                 floats never overlap; the panel anchors to the
#                                 bar's outer edge + 8)
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

# Window class names: only Shiyu's own WPF windows ("HwndWrapper[Shiyu.App;;...]")
# count as layers. An input method parks its status bar in whichever process has
# the keyboard focus - observed 2026-10-09: "FyPY_Status", 257x66, attributed to
# the probe pid - and that is the user's IME, not a connector coming back.
if (-not ('Shiyu.Probe.WindowClass' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Shiyu.Probe
{
    public static class WindowClass
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

        public static string Of(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
'@
}

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$VK_DOWN  = 0x28
$VK_SPACE = 0x20

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\preview'
# Seed from the SAME build the probe runs (-o overrides leave the worktree's
# own bin absent; the seeder must reference the exe's Core, not a stale one).
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar'
try {
    $bar = Wait-ProbeWindow $p.Id 384
    if ($bar -eq [IntPtr]::Zero) {
        Add-Check 'flow:preview-opens' 'FAIL' 'no 384-DIP bar window of the probe pid'
        return $script:Checks
    }

    Start-Sleep -Milliseconds 1200   # entrance + content settle
    $barRect = Get-WindowRectInfo $bar 384

    # Park the bar low: its lower rows sit past the work-area bottom, so the
    # panel placement clamps the panel UP - panel and row genuinely offset.
    Add-Type -AssemblyName System.Windows.Forms
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $barTop = $wa.Bottom - [int][Math]::Round(230 * $barRect.Scale)
    # SWP_NOSIZE(0x1) | SWP_NOZORDER(0x4) | SWP_NOACTIVATE(0x10)
    [void][Shiyu.Probe.Native]::SetWindowPos($bar, [IntPtr]::Zero, 100, $barTop, 0, 0, 0x15)
    Start-Sleep -Milliseconds 600

    # Nine Downs select the long-text row (row 9 of the seeded list): its
    # panel is tall, so the upward clamp - and the offset - are large.
    for ($i = 0; $i -lt 9; $i++) {
        Send-ProbeKey $bar $VK_DOWN
        Start-Sleep -Milliseconds 120
    }
    Start-Sleep -Milliseconds 700

    # Space held (no key-up) previews the active row in full.
    Send-ProbeKey $bar $VK_SPACE
    Start-Sleep -Milliseconds 1600   # panel measure + placement

    # The panel carries a window title; any other visible Shiyu window from this
    # pid besides the bar would be a stray layer (the old connector sheet had none).
    $preview = [IntPtr]::Zero
    $strays = @()
    foreach ($h in [Shiyu.Probe.Native]::ListWindows()) {
        if ([Shiyu.Probe.Native]::PidOf($h) -ne $p.Id) { continue }
        if ($h -eq $bar) { continue }
        if (-not [Shiyu.Probe.WindowClass]::Of($h).StartsWith('HwndWrapper[Shiyu.App')) { continue }
        if ($preview -eq [IntPtr]::Zero -and [Shiyu.Probe.Native]::TitleLenOf($h) -gt 0) {
            $preview = $h
        } else {
            $strays += $h
        }
    }

    if ($preview -eq [IntPtr]::Zero) {
        Add-Check 'flow:preview-opens' 'FAIL' 'preview window not found (Space path broken?)'
        Add-Check 'flow:preview-no-connector' 'SKIP' 'no preview to stand beside'
        Add-Check 'defect:preview-bar-overlap' 'SKIP' 'no preview to measure'
        return $script:Checks
    }
    Add-Check 'flow:preview-opens' 'PASS' ("hwnd={0}" -f $preview)

    if ($strays.Count -eq 0) {
        Add-Check 'flow:preview-no-connector' 'PASS' 'only the bar and the panel are visible'
    } else {
        $sizes = @($strays | ForEach-Object { $r = [Shiyu.Probe.Native]::RectOf($_); '{0}x{1}' -f $r[2], $r[3] })
        Add-Check 'flow:preview-no-connector' 'FAIL' ("{0} extra window(s) beside the panel: {1}" -f $strays.Count, ($sizes -join ', '))
    }

    $barRect = Get-WindowRectInfo $bar 384
    $pv = [Shiyu.Probe.Native]::RectOf($preview)
    $ovW = [Math]::Max(0, [Math]::Min($barRect.L + $barRect.W, $pv[0] + $pv[2]) - [Math]::Max($barRect.L, $pv[0]))
    $ovH = [Math]::Max(0, [Math]::Min($barRect.T + $barRect.H, $pv[1] + $pv[3]) - [Math]::Max($barRect.T, $pv[1]))
    $overlapPx = $ovW * $ovH
    Add-Check 'defect:preview-bar-overlap' `
        $(if ($overlapPx -eq 0) { 'PASS' } else { 'FAIL' }) `
        ("bar {0}x{1} at {2},{3}; preview {4}x{5} at {6},{7}; overlap {8} px^2 (anchor: bar outer edge + 8)" -f `
            $barRect.W, $barRect.H, $barRect.L, $barRect.T, $pv[2], $pv[3], $pv[0], $pv[1], $overlapPx)
} finally {
    if ($p) { Stop-ProbeApp $p }
}

return $script:Checks
