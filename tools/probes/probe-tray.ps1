# probe-tray.ps1 - a left click on the tray icon opens settings (user request
# 2026-10-05: clicking the app's icon opens settings; it used to open the
# library window).
#
# The probe instance has its own tray icon, and its callbacks land on the app's
# one hidden message window (class "ShiyuMessageWindow_<guid>"). Posting the
# tray callback message with WM_LBUTTONUP in its low word is exactly what the
# shell sends on a left click: no mouse moves, and the user's real instance is
# never addressed (the window is looked up by the probe's own pid).
#
# Checks:
#   - window:tray-message-window      the probe pid's message window was found
#   - flow:tray-click-opens-settings  the settings window appears after the click
#   - flow:tray-click-not-library     and the library window does not
#
# ASCII only (see lib.ps1 header): window titles are built from code points.

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if (-not ('Shiyu.Probe.TrayPoke' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Shiyu.Probe
{
    public static class TrayPoke
    {
        private delegate bool EnumProc(IntPtr h, IntPtr lp);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

        private static uint PidOf(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return pid;
        }

        public static IntPtr FindByClassPrefix(uint pid, string prefix)
        {
            var found = IntPtr.Zero;
            EnumWindows((h, lp) =>
            {
                if (PidOf(h) != pid) { return true; }
                var sb = new StringBuilder(256);
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString().StartsWith(prefix, StringComparison.Ordinal)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static string[] VisibleTitles(uint pid)
        {
            var titles = new List<string>();
            EnumWindows((h, lp) =>
            {
                if (PidOf(h) == pid && IsWindowVisible(h))
                {
                    var sb = new StringBuilder(256);
                    GetWindowText(h, sb, sb.Capacity);
                    titles.Add(sb.ToString());
                }
                return true;
            }, IntPtr.Zero);
            return titles.ToArray();
        }
    }
}
'@
}

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

# "<shiyu> . <settings>" and "<shiyu> . <history>" - the two windows a click could open.
$prefix = -join ([char[]](0x62FE, 0x8BED, 0x20, 0xB7, 0x20))
$settingsTitle = $prefix + (-join ([char[]](0x8BBE, 0x7F6E)))
$libraryTitle = $prefix + (-join ([char[]](0x5386, 0x53F2)))

$WM_TRAYICON = 0x8001     # NativeMethods.WmTrayIcon (WM_APP + 1)
$WM_LBUTTONUP = 0x0202

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\tray'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd ''
try {
    $window = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds(25)
    while ($window -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $window = [Shiyu.Probe.TrayPoke]::FindByClassPrefix([uint32]$p.Id, 'ShiyuMessageWindow_')
    }
    if ($window -eq [IntPtr]::Zero) {
        Add-Check 'window:tray-message-window' 'FAIL' 'no message window of the probe pid'
        return $script:Checks
    }
    Add-Check 'window:tray-message-window' 'PASS' ("hwnd={0}" -f $window)

    # Startup settles (modules attach, the tray icon is added) before the click.
    Start-Sleep -Milliseconds 1500
    $before = [Shiyu.Probe.TrayPoke]::VisibleTitles([uint32]$p.Id)
    if ($before -contains $settingsTitle) {
        Add-Check 'flow:tray-click-opens-settings' 'SKIP' 'settings already open before the click'
        return $script:Checks
    }

    [void][Shiyu.Probe.TrayPoke]::PostMessage($window, $WM_TRAYICON, [IntPtr]1, [IntPtr]$WM_LBUTTONUP)

    $titles = @()
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $titles = [Shiyu.Probe.TrayPoke]::VisibleTitles([uint32]$p.Id)
        if ($titles -contains $settingsTitle) { break }
    }

    $opened = $titles -contains $settingsTitle
    Add-Check 'flow:tray-click-opens-settings' $(if ($opened) { 'PASS' } else { 'FAIL' }) `
        ("{0} visible window(s) after the click; settings {1}" -f $titles.Count, $(if ($opened) { 'open' } else { 'missing' }))

    $library = $titles -contains $libraryTitle
    Add-Check 'flow:tray-click-not-library' $(if ($library) { 'FAIL' } else { 'PASS' }) `
        $(if ($library) { 'the library window opened too' } else { 'no library window' })
} finally {
    if ($p) { Stop-ProbeApp $p }
}

return $script:Checks
