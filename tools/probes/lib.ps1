# lib.ps1 - shared plumbing for the Shiyu visual probes (ticket 15).
#
# Dot-source this from every probe-*.ps1; it provides:
#   - a Win32/UIA host made DPI-aware (probes run on 4K@150% + 2K@100%)
#   - starting and stopping the probe app instance (isolated data dir only,
#     only ever the pid we started ourselves)
#   - finding a window of that instance by its DIP width (windows of this
#     app are size-fixed: bar 384 / panel 420 / quickbar 460 / settings 880 /
#     library 1100 (ticket 24, was 1150) / reverse input 440 (ticket 43, 520 until 2026-10-09; its
#     height grows with the content); the app's WPF class names are
#     per-instance GUIDs, so a width match plus pid match is the structural
#     way to tell them apart)
#   - PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT) screenshots (reads the
#     DWM surface, reliable for the layered Acrylic windows)
#   - read-only UIA enumeration with cached properties
#   - PostMessage keyboard injection (no global hotkeys, no SendInput)
#   - user-presence detection and pixel sampling helpers
#
# ASCII ONLY: PowerShell 5 renders un-BOM'd non-ASCII characters in .ps1
# files as mojibake. Any non-ASCII matching (e.g. the keycap arrow string)
# is built at runtime from code points, never written as a literal.

$ErrorActionPreference = 'Stop'

# --- results location (never inside the repo) -------------------------------
if (-not $script:ProbeResultRoot) {
    $script:ProbeResultRoot = Join-Path $env:TEMP 'shiyu-probe-results'
}
New-Item -ItemType Directory -Force -Path $script:ProbeResultRoot | Out-Null

# --- native interop ----------------------------------------------------------
if (-not ('Shiyu.Probe.Native' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing;

namespace Shiyu.Probe
{
    public static class Native
    {
        public delegate bool EnumProc(IntPtr h, IntPtr lp);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint vk, uint type);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);

        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        // All visible top-level windows as a flat list of handles; the caller
        // filters by pid. Enumerated inside C# so the delegate never dies
        // under PowerShell's GC the way a script-block delegate can.
        public static IntPtr[] ListWindows()
        {
            var list = new List<IntPtr>();
            EnumWindows((h, lp) => { if (IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
            return list.ToArray();
        }

        public static uint PidOf(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return pid;
        }

        // L, T, W, H in physical pixels.
        public static int[] RectOf(IntPtr h)
        {
            RECT r;
            GetWindowRect(h, out r);
            return new[] { r.L, r.T, r.R - r.L, r.B - r.T };
        }

        public static int TitleLenOf(IntPtr h) { return GetWindowTextLength(h); }

        public static int DpiOf(IntPtr h)
        {
            uint dpi = GetDpiForWindow(h);
            return dpi == 0 ? 96 : (int)dpi;
        }

        // One posted key event. Extended flag matters for arrows/Enter; the
        // previous-state and transition bits make it a well-formed pair.
        public static void Key(IntPtr h, uint vk, bool up, bool extended)
        {
            uint scan = MapVirtualKey(vk, 0);
            uint lp = scan << 16;
            if (extended) lp |= 0x01000000;
            if (up) lp |= 0xC0000000; else lp |= 0x00000001;
            PostMessage(h, up ? WM_KEYUP : WM_KEYDOWN, (IntPtr)vk, (IntPtr)lp);
        }

        public static int[] Cursor()
        {
            POINT p;
            GetCursorPos(out p);
            return new[] { p.X, p.Y };
        }

        public static bool MoveCursor(int x, int y) { return SetCursorPos(x, y); }

        // PW_RENDERFULLCONTENT (2): read the DWM-composed surface, the only
        // reliable capture for layered/Acrylic WPF windows.
        public static IntPtr CaptureHbitmap(IntPtr h)
        {
            RECT r;
            GetWindowRect(h, out r);
            int w = r.R - r.L, ht = r.B - r.T;
            if (w <= 0 || ht <= 0) return IntPtr.Zero;

            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr bmp = CreateCompatibleBitmap(screen, w, ht);
            IntPtr old = SelectObject(mem, bmp);
            PrintWindow(h, mem, 2);
            SelectObject(mem, old);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            return bmp; // caller owns it; Bitmap.FromHbitmap copies the pixels
        }

        public static void FreeHbitmap(IntPtr bmp) { DeleteObject(bmp); }
    }

    // Pixel analysis on System.Drawing bitmaps. PowerShell per-pixel loops
    // over a window-sized bitmap cost tens of seconds; these cost none.
    public static class Pixels
    {
        static int[] Px(byte[] b, int stride, int x, int y)
        {
            int i = y * stride + x * 4;
            return new int[] { b[i + 2], b[i + 1], b[i] };
        }

        static bool Near(int[] a, int r, int g, int bl, int tol)
        {
            return Math.Abs(a[0] - r) + Math.Abs(a[1] - g) + Math.Abs(a[2] - bl) <= tol;
        }

        static byte[] Lock(Bitmap bmp, out int stride)
        {
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            int rowBytes = Math.Abs(data.Stride);
            var bytes = new byte[rowBytes * data.Height];
            if (data.Stride > 0)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            }
            else
            {
                // bottom-up DIB: Scan0 is the LAST row; walk row by row and
                // flip into a top-down buffer (a single bulk copy would read
                // past the end of the buffer and crash the process).
                for (int y = 0; y < data.Height; y++)
                {
                    var row = (IntPtr)((long)data.Scan0 + (long)data.Stride * y);
                    System.Runtime.InteropServices.Marshal.Copy(row, bytes,
                        (data.Height - 1 - y) * rowBytes, rowBytes);
                }
            }
            stride = rowBytes;
            bmp.UnlockBits(data);
            return bytes;
        }

        // First x in row y where runLen consecutive pixels sit within tol of
        // refColor; -1 when no such run. Used for the corner-radius scan.
        public static int[] RunScan(Bitmap bmp, int y, int maxOffset, int[] refColor, int tol, int runLen)
        {
            int stride;
            var b = Lock(bmp, out stride);
            for (int x = 0; x <= Math.Min(maxOffset, bmp.Width - runLen); x++)
            {
                bool ok = true;
                for (int k = 0; k < runLen; k++)
                    if (!Near(Px(b, stride, x + k, y), refColor[0], refColor[1], refColor[2], tol)) { ok = false; break; }
                if (ok) return new[] { x };
            }
            return new[] { -1 };
        }

        // Bounding box and count of pixels NOT within tol of bg ("ink").
        public static int[] InkBox(Bitmap bmp, int L, int T, int W, int H, int[] bg, int tol)
        {
            int stride;
            var b = Lock(bmp, out stride);
            int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1, count = 0;
            for (int y = T; y < T + H && y < bmp.Height; y++)
                for (int x = L; x < L + W && x < bmp.Width; x++)
                    if (!Near(Px(b, stride, x, y), bg[0], bg[1], bg[2], tol))
                    {
                        count++;
                        if (x < minx) minx = x;
                        if (x > maxx) maxx = x;
                        if (y < miny) miny = y;
                        if (y > maxy) maxy = y;
                    }
            if (count == 0) return new[] { 0, 0, 0, 0, 0, 0 };
            return new[] { 1, minx, miny, maxx - minx + 1, maxy - miny + 1, count };
        }

        // The same, for the pixels WITHIN tol of the colour (e.g. the accent
        // block of a keycap badge, wherever exactly it ended up).
        public static int[] ColorBox(Bitmap bmp, int L, int T, int W, int H, int[] color, int tol)
        {
            int stride;
            var b = Lock(bmp, out stride);
            int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1, count = 0;
            for (int y = T; y < T + H && y < bmp.Height; y++)
                for (int x = L; x < L + W && x < bmp.Width; x++)
                    if (Near(Px(b, stride, x, y), color[0], color[1], color[2], tol))
                    {
                        count++;
                        if (x < minx) minx = x;
                        if (x > maxx) maxx = x;
                        if (y < miny) miny = y;
                        if (y > maxy) maxy = y;
                    }
            if (count == 0) return new[] { 0, 0, 0, 0, 0, 0 };
            return new[] { 1, minx, miny, maxx - minx + 1, maxy - miny + 1, count };
        }

        static int[] MedianOf(List<int[]> px)
        {
            px.Sort((a, c) =>
            {
                int s = a[0] + a[1] + a[2], t = c[0] + c[1] + c[2];
                return s != t ? s.CompareTo(t) : 0;
            });
            return px[px.Count / 2];
        }

        // The representative "ink" colour: median of the ink pixels that sit
        // FARTHEST from bg (the glyph core, not the anti-aliased fringe).
        public static int[] InkCoreColor(Bitmap bmp, int L, int T, int W, int H, int[] bg, int tol)
        {
            int stride;
            var b = Lock(bmp, out stride);
            var ink = new List<int[]>();
            for (int y = T; y < T + H && y < bmp.Height; y++)
                for (int x = L; x < L + W && x < bmp.Width; x++)
                {
                    var p = Px(b, stride, x, y);
                    if (!Near(p, bg[0], bg[1], bg[2], tol)) ink.Add(p);
                }
            if (ink.Count == 0) return new[] { -1, -1, -1 };
            ink.Sort((a, c) => Dist(a, bg).CompareTo(Dist(c, bg)));
            var core = ink.GetRange(ink.Count * 2 / 3, ink.Count - ink.Count * 2 / 3);
            return MedianOf(core);
        }

        static int Dist(int[] a, int[] b)
        {
            return Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]);
        }

        // Median colour of a rectangle (the local "background").
        public static int[] MedianColor(Bitmap bmp, int L, int T, int W, int H)
        {
            int stride;
            var b = Lock(bmp, out stride);
            var px = new List<int[]>();
            for (int y = T; y < T + H && y < bmp.Height; y++)
                for (int x = L; x < L + W && x < bmp.Width; x++)
                    px.Add(Px(b, stride, x, y));
            if (px.Count == 0) return new[] { -1, -1, -1 };
            return MedianOf(px);
        }

        // Pixels within tol of any of the given colours (the accent in either
        // theme); the largest connected blob of them; its bounding box, median
        // colour (the blob's "background") and the median colour of the 15%
        // extreme-luminance tail farthest from that median (the "text").
        // colours arrives flat: r,g,b triplets.
        public static int[] AccentBlob(Bitmap bmp, int[] flat, int tol)
        {
            int stride;
            var b = Lock(bmp, out stride);
            int w = bmp.Width, h = bmp.Height;
            var mask = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = Px(b, stride, x, y);
                    for (int c = 0; c + 2 < flat.Length; c += 3)
                        if (Near(p, flat[c], flat[c + 1], flat[c + 2], tol)) { mask[y * w + x] = true; break; }
                }

            var seen = new bool[w * h];
            int[] best = null; int bestArea = 0;
            var stack = new Stack<int>();
            var comp = new List<int>();
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || seen[start]) continue;
                stack.Clear(); comp.Clear();
                stack.Push(start); seen[start] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    comp.Add(i);
                    int x = i % w, y = i / w;
                    if (x > 0 && mask[i - 1] && !seen[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                    if (x < w - 1 && mask[i + 1] && !seen[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                    if (y > 0 && mask[i - w] && !seen[i - w]) { seen[i - w] = true; stack.Push(i - w); }
                    if (y < h - 1 && mask[i + w] && !seen[i + w]) { seen[i + w] = true; stack.Push(i + w); }
                }
                if (comp.Count > bestArea) { bestArea = comp.Count; best = comp.ToArray(); }
            }
            if (best == null || bestArea < 60) return new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

            int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1;
            foreach (int i in best)
            {
                int x = i % w, y = i / w;
                if (x < minx) minx = x; if (x > maxx) maxx = x;
                if (y < miny) miny = y; if (y > maxy) maxy = y;
            }

            var blobPx = new List<int[]>();
            foreach (int i in best) blobPx.Add(Px(b, stride, i % w, i / w));
            var bg = MedianOf(new List<int[]>(blobPx));

            // luminance order, then split by which extreme is farther
            var byLum = new List<int[]>(blobPx);
            byLum.Sort((p, q) => Lum(p).CompareTo(Lum(q)));
            double medLum = Lum(byLum[byLum.Count / 2]);
            double darkGap = medLum - Lum(byLum[byLum.Count / 20]);
            double lightGap = Lum(byLum[byLum.Count * 19 / 20]) - medLum;
            int tailCount = Math.Max(4, blobPx.Count * 15 / 100);
            List<int[]> tail = lightGap >= darkGap
                ? byLum.GetRange(byLum.Count - tailCount, tailCount)
                : byLum.GetRange(0, tailCount);
            var txt = MedianOf(tail);

            return new[] { 1, minx, miny, maxx - minx + 1, maxy - miny + 1, bestArea,
                           bg[0], bg[1], bg[2], txt[0], txt[1], txt[2] };
        }

        static double Lum(int[] p)
        {
            Func<int, double> f = c =>
            {
                double v = c / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            };
            return 0.2126 * f(p[0]) + 0.7152 * f(p[1]) + 0.0722 * f(p[2]);
        }

        // Count of pixels within tol of the colour, inside a rect.
        public static int CountColor(Bitmap bmp, int L, int T, int W, int H, int[] color, int tol)
        {
            int stride;
            var b = Lock(bmp, out stride);
            int count = 0;
            for (int y = T; y < T + H && y < bmp.Height; y++)
                for (int x = L; x < L + W && x < bmp.Width; x++)
                    if (Near(Px(b, stride, x, y), color[0], color[1], color[2], tol)) count++;
            return count;
        }
    }
}
'@ -ReferencedAssemblies System.Drawing
}

# Per-monitor-v2 first: with only system DPI awareness, every window on a
# non-primary-scale monitor gets its rect, cursor and UIA coordinates
# virtualized into the system scale, and cross-window measurements quietly
# disagree with PrintWindow output (observed as 2x2 captures and windows
# thousands of pixels off). PMv2 makes everything physical. If the host
# process already picked an awareness, fall back to system-aware.
if (-not [Shiyu.Probe.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))) {
    [void][Shiyu.Probe.Native]::SetProcessDPIAware()
}
Add-Type -AssemblyName System.Drawing

# --- UIA (read-only enumeration) ---------------------------------------------
[void][System.Reflection.Assembly]::LoadWithPartialName('UIAutomationClient')
[void][System.Reflection.Assembly]::LoadWithPartialName('UIAutomationTypes')

function Get-UiaRoot {
    return [System.Windows.Automation.AutomationElement]::RootElement
}

# All top-level windows of one process, as UIA elements.
function Get-UiaWindowsOfPid([int]$ProcessId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    return (Get-UiaRoot).FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
}

# Descendants of an element with all properties the probes need, cached into
# plain objects: crossing to the COM wrapper per property in a PS loop is
# slow, so each element is touched exactly once per scan.
function Get-UiaTree([System.Windows.Automation.AutomationElement]$Element) {
    $out = New-Object System.Collections.ArrayList
    $all = $Element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                            [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($el in $all) {
        $cur = $el.Current
        $r = $cur.BoundingRectangle
        if ([double]::IsInfinity($r.X) -or [double]::IsInfinity($r.Y) -or $r.Width -le 0 -or $r.Height -le 0) {
            continue  # off-screen / placeholder rect
        }
        $item = [pscustomobject]@{
            Name      = $cur.Name
            ClassName = $cur.ClassName
            Type      = $cur.ControlType.ProgrammaticName -replace '^ControlType\.',''
            L = [int][Math]::Floor($r.X); T = [int][Math]::Floor($r.Y)
            W = [int][Math]::Floor($r.Width); H = [int][Math]::Floor($r.Height)
            Element   = $el
        }
        $null = $out.Add($item)
    }
    return $out
}

# Toggle state for elements that support it (segment chips are ToggleButtons).
function Test-UiaToggled($Item) {
    try {
        $p = $Item.Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($p) { return [bool]$p.Current.IsOn }
    } catch { }
    return $false
}

# --- probe app lifecycle -----------------------------------------------------

function Get-DefaultProbeExe {
    return (Join-Path (Join-Path $PSScriptRoot '..\..\src\Shiyu.App\bin\Debug\net9.0-windows') 'Shiyu.App.exe')
}

# Starts the probe instance. Always check the exe path first: a probe must
# run the worktree's own build, never a user copy.
function Start-ProbeApp {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string]$DataDir,
        [string]$Cmd = '',
        [string]$Item = '',
        [string]$Text = '',
        # Extra DEBUG-only knobs for the probe instance (ticket 43), e.g.
        # @{ SHIYU_FAKE_BACKEND = '1'; SHIYU_PROBE_CLIPBOARD = '1' }. Set only
        # around the launch and restored afterwards, like the four above.
        [hashtable]$ExtraEnv = @{}
    )
    if (-not (Test-Path $Exe)) { throw "probe exe not found: $Exe" }
    if ($DataDir -notlike "$env:TEMP*") { throw "refusing to probe outside TEMP: $DataDir" }

    $old = @{ DATA = $env:SHIYU_DATA_DIR; CMD = $env:SHIYU_PROBE_CMD; ITEM = $env:SHIYU_PROBE_ITEM; TEXT = $env:SHIYU_PROBE_TEXT }
    $oldExtra = @{}
    foreach ($name in $ExtraEnv.Keys) {
        $oldExtra[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, [string]$ExtraEnv[$name], 'Process')
    }
    $env:SHIYU_DATA_DIR = $DataDir
    $env:SHIYU_PROBE_CMD = $Cmd
    $env:SHIYU_PROBE_ITEM = $Item
    $env:SHIYU_PROBE_TEXT = $Text
    try {
        $p = Start-Process -FilePath $Exe -PassThru
    } finally {
        $env:SHIYU_DATA_DIR = $old.DATA; $env:SHIYU_PROBE_CMD = $old.CMD
        $env:SHIYU_PROBE_ITEM = $old.ITEM; $env:SHIYU_PROBE_TEXT = $old.TEXT
        foreach ($name in $oldExtra.Keys) {
            [Environment]::SetEnvironmentVariable($name, $oldExtra[$name], 'Process')
        }
    }
    return $p
}

# Kill only a pid this session started. CloseMainWindow first so the app can
# save geometry; a WPF app with ShutdownMode=OnExplicitShutdown usually needs
# the kill, and that is fine - the probe owns the process.
function Stop-ProbeApp([System.Diagnostics.Process]$Process) {
    if (-not $Process -or $Process.HasExited) { return }
    try {
        [void]$Process.CloseMainWindow()
        if (-not $Process.WaitForExit(1200)) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
}

# Finds the probe instance's window by DIP width (see header note).
# The match is made against every common monitor scale rather than
# GetDpiForWindow, which reports an unusable value for another process's
# DPI-virtualized window (observed: 96 for a window rendering 384 DIP at
# 150%). The found window's real scale is then derived from its own width,
# which is exact because these windows are size-fixed.
function Wait-ProbeWindow([int]$ProcessId, [double]$DipWidth, [int]$TimeoutSec = 25) {
    $scales = @(1.0, 1.25, 1.5, 1.75, 2.0)
    $candidates = @($scales | ForEach-Object { [int][Math]::Round($DipWidth * $_) })
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($h in [Shiyu.Probe.Native]::ListWindows()) {
            if ([Shiyu.Probe.Native]::PidOf($h) -ne $ProcessId) { continue }
            $r = [Shiyu.Probe.Native]::RectOf($h)
            foreach ($w in $candidates) {
                if ([Math]::Abs($r[2] - $w) -le 8) { return $h }
            }
        }
        Start-Sleep -Milliseconds 400
    }
    return [IntPtr]::Zero
}

# Window rect plus the scale derived from its known DIP width.
function Get-WindowRectInfo([IntPtr]$Hwnd, [double]$DipWidth) {
    $r = [Shiyu.Probe.Native]::RectOf($Hwnd)
    $scale = if ($DipWidth -gt 0) { $r[2] / $DipWidth } else { 1.0 }
    return @{ L = $r[0]; T = $r[1]; W = $r[2]; H = $r[3]; Dpi = [int]($scale * 96); Scale = $scale }
}

# Moves a probe-owned window onto the primary monitor's work area. The bar
# and its siblings appear AT THE CURSOR, and on a multi-monitor machine that
# can be a far-away screen whose DPI virtualization renders the window
# inconsistently for PrintWindow; pinning every probe window to one known
# place makes pixel expectations deterministic. Only ever called with hwnds
# of the probe instance this session started.
function Move-WindowToProbeSpot([IntPtr]$Hwnd, [int]$Slot) {
    # SWP_NOSIZE(0x1) | SWP_NOZORDER(0x4) | SWP_NOACTIVATE(0x10)
    [void][Shiyu.Probe.Native]::SetWindowPos($Hwnd, [IntPtr]::Zero, 60 + $Slot * 40, 80 + $Slot * 30, 0, 0, 0x15)
}

function Send-ProbeKey([IntPtr]$Hwnd, [int]$KeyCode, [switch]$KeyUp, [switch]$Extended) {
    [Shiyu.Probe.Native]::Key($Hwnd, [uint32]$KeyCode, [bool]$KeyUp, [bool]$Extended)
}

# --- screenshots -------------------------------------------------------------

function Get-WindowShot([IntPtr]$Hwnd) {
    $bmp = [Shiyu.Probe.Native]::CaptureHbitmap($Hwnd)
    if ($bmp -eq [IntPtr]::Zero) { return $null }
    try {
        $raw = [System.Drawing.Bitmap]::FromHbitmap($bmp)
    } finally {
        [Shiyu.Probe.Native]::FreeHbitmap($bmp)
    }
    # FromHbitmap hands back a bitmap that still shares GDI memory: LockBits
    # on it can serve a stale buffer (a live quickbar shot counted 0 accent
    # pixels while its own saved PNG held 107). Redrawing onto a fresh bitmap
    # forces a realized, owned copy - every consumer then reads what was
    # actually captured.
    $clean = New-Object System.Drawing.Bitmap $raw.Width, $raw.Height, `
        ([System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($clean)
    try {
        $g.DrawImage($raw, 0, 0, $raw.Width, $raw.Height)
    } finally {
        $g.Dispose(); $raw.Dispose()
    }
    return $clean
}

function Save-Shot([System.Drawing.Bitmap]$Bitmap, [string]$Name) {
    $dir = $script:ProbeResultRoot
    $path = Join-Path $dir ($Name + '.png')
    $Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    return $path
}

# --- pixel access ------------------------------------------------------------

# LockBits 32bpp: returns @{B=byte[];W;H;S} where B is BGRA order, rows
# normalized to top-down (negative strides flipped row by row).
function Get-Pixels([System.Drawing.Bitmap]$Bitmap) {
    $rect = New-Object System.Drawing.Rectangle(0, 0, $Bitmap.Width, $Bitmap.Height)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
    try {
        $row = [Math]::Abs($data.Stride)
        $bytes = New-Object byte[] ($row * $data.Height)
        if ($data.Stride -gt 0) {
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        } else {
            for ($y = 0; $y -lt $data.Height; $y++) {
                $p = [IntPtr]([long]$data.Scan0 + [long]$data.Stride * $y)
                [System.Runtime.InteropServices.Marshal]::Copy($p, $bytes, ($data.Height - 1 - $y) * $row, $row)
            }
        }
        return @{ B = $bytes; W = $Bitmap.Width; H = $Bitmap.Height; S = $row }
    } finally {
        $Bitmap.UnlockBits($data)
    }
}

# R,G,B of one pixel (crop-relative coordinates).
function Get-Px($Pixels, [int]$X, [int]$Y) {
    if ($X -lt 0 -or $Y -lt 0 -or $X -ge $Pixels.W -or $Y -ge $Pixels.H) { return $null }
    $i = $Y * $Pixels.S + $X * 4
    return , @([int]$Pixels.B[$i + 2], [int]$Pixels.B[$i + 1], [int]$Pixels.B[$i])
}

# Crop a window shot to a physical-pixel rect (window coordinates).
function Get-CropPixels([System.Drawing.Bitmap]$Shot, [int]$L, [int]$T, [int]$W, [int]$H) {
    $L = [Math]::Max(0, $L); $T = [Math]::Max(0, $T)
    $W = [Math]::Min($W, $Shot.Width - $L); $H = [Math]::Min($H, $Shot.Height - $T)
    if ($W -le 0 -or $H -le 0) { return $null }
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.DrawImage($Shot, (New-Object System.Drawing.Rectangle(0, 0, $W, $H)),
            (New-Object System.Drawing.Rectangle($L, $T, $W, $H)), [System.Drawing.GraphicsUnit]::Pixel)
    } finally { $g.Dispose() }
    try { return (Get-Pixels $bmp) } finally { $bmp.Dispose() }
}

function Get-ColorDistance([int[]]$A, [int[]]$B) {
    return [Math]::Abs($A[0] - $B[0]) + [Math]::Abs($A[1] - $B[1]) + [Math]::Abs($A[2] - $B[2])
}

# WCAG 2.x relative luminance and contrast ratio.
function Get-RelativeLuminance([int[]]$Rgb) {
    $f = {
        param($c)
        $v = $c / 255.0
        if ($v -le 0.03928) { $v / 12.92 } else { [Math]::Pow(($v + 0.055) / 1.055, 2.4) }
    }
    return 0.2126 * (& $f $Rgb[0]) + 0.7152 * (& $f $Rgb[1]) + 0.0722 * (& $f $Rgb[2])
}

function Get-ContrastRatio([int[]]$A, [int[]]$B) {
    $la = Get-RelativeLuminance $A
    $lb = Get-RelativeLuminance $B
    $hi = [Math]::Max($la, $lb); $lo = [Math]::Min($la, $lb)
    return ($hi + 0.05) / ($lo + 0.05)
}

function Get-MedianColor($Colors) {
    if (-not $Colors -or $Colors.Count -eq 0) { return $null }
    $rs = @($Colors | ForEach-Object { $_[0] } | Sort-Object)
    $gs = @($Colors | ForEach-Object { $_[1] } | Sort-Object)
    $bs = @($Colors | ForEach-Object { $_[2] } | Sort-Object)
    $mid = [int][Math]::Floor($Colors.Count / 2)
    return , @([int]$rs[$mid], [int]$gs[$mid], [int]$bs[$mid])
}

# --- user presence -----------------------------------------------------------

# True when the cursor moved between two samples ~2s apart: someone is at the
# machine, and mouse-stealing probes must stand down.
function Test-UserPresent {
    $a = [Shiyu.Probe.Native]::Cursor()
    Start-Sleep -Seconds 2
    $b = [Shiyu.Probe.Native]::Cursor()
    return (($a[0] -ne $b[0]) -or ($a[1] -ne $b[1]))
}

# --- a11y name checks ----------------------------------------------------------

# A string built from Unicode code points at runtime: probe scripts stay
# ASCII-only (see header), yet a11y assertions must match Chinese UI names
# exactly. Full-width parens for the "action (key)" name format: 0xFF08/0xFF09.
function New-Zh([int[]]$Codes) {
    return -join ($Codes | ForEach-Object { [char]$_ })
}

# "action (key)" - the KeyMap.AutomationName format, composed without a
# non-ASCII literal: action codes + full-width ( + ASCII key + full-width ).
function New-ZhKeyName([int[]]$ActionCodes, [string]$Key) {
    return (New-Zh $ActionCodes) + [string][char]0xFF08 + $Key + [string][char]0xFF09
}

# --- check bookkeeping -------------------------------------------------------

if (-not $script:Checks) {
    $script:Checks = New-Object System.Collections.ArrayList
}

function Add-Check([string]$Name, [string]$Status, [string]$Detail) {
    $null = $script:Checks.Add([pscustomobject]@{
        Check = $Name; Status = $Status; Detail = $Detail })
    Write-Host ("  [{0}] {1} - {2}" -f $Status, $Name, $Detail)
}

function Reset-Checks {
    $script:Checks = New-Object System.Collections.ArrayList
}

# --- shared visual measurements ----------------------------------------------

# DesignTokens.Accent in both themes (light #1A66DB, dark #4C8DFF).
$script:AccentLight = @(0x1A, 0x66, 0xDB)
$script:AccentDark  = @(0x4C, 0x8D, 0xFF)

# Defect "double corner radius": the shell paints its own corner radius
# (Radius.Window = 12 DIP) inside the DWM-rounded window region (8 DIP), so
# the outer contour carries two arcs of different radii.
#
# Measured by fitting the circle: for a rounded corner with centre (r, r)
# relative to the shell's corner, a boundary point (x, y) on the arc gives
# r = x + y + sqrt(2*x*y). The boundary at several rows just below the top
# edge is the first run of "surface" pixels (sampled from the window's own
# top centre), and the median of the per-row fits is the shell's radius.
# Works with the transparent margin windows (library/settings) by measuring
# relative to the shell's edge, margin aside.
function Measure-ShellRadiusDip {
    param($Shot, $RectInfo, [double]$ShellMarginDip)
    $scale = $RectInfo.Scale
    $marginPx = [int][Math]::Round($ShellMarginDip * $scale)

    # Reference: the shell's own surface, a little below the top border
    # stroke, sampled at the horizontal centre (above any header content).
    $yRef = $marginPx + [int][Math]::Ceiling(3.0 * $scale)
    $refH = [int][Math]::Max(2, [int][Math]::Round(2 * $scale))
    $ref = [Shiyu.Probe.Pixels]::MedianColor($Shot, [int]($Shot.Width / 2) - 10, $yRef, 20, $refH)
    if ($ref[0] -lt 0) { return @{ RadiusDip = -1.0; Note = 'no reference pixels' } }

    $maxOff = [int][Math]::Ceiling(40 * $scale) + $marginPx
    $run = [int][Math]::Max(2, [int][Math]::Round(2 * $scale))
    $fits = @()
    $rowsSeen = @()
    $firstY = $marginPx + [int][Math]::Max(1, [int][Math]::Round(1.0 * $scale))
    for ($dy = 0; $dy -lt [int][Math]::Round(6.0 * $scale); $dy++) {
        $y = $firstY + $dy
        if ($y -ge $Shot.Height - 1) { break }
        $hit = [Shiyu.Probe.Pixels]::RunScan($Shot, $y, $maxOff, $ref, 24, $run)
        if ($hit[0] -lt 0) { continue }
        $xRel = $hit[0] - $marginPx     # boundary x, relative to the shell edge
        $yRel = $y - $marginPx          # boundary row, relative to the shell edge
        if ($xRel -lt 1 -or $yRel -lt 1) { continue }
        $rPx = $xRel + $yRel + [Math]::Sqrt(2.0 * $xRel * $yRel)
        if ($rPx -gt 4 -and $rPx -lt 60 * $scale) {
            $fits += [Math]::Round($rPx / $scale, 1)
            $rowsSeen += "y=$yRel,x=$xRel,r=$([Math]::Round($rPx / $scale, 1))"
        }
    }
    if ($fits.Count -lt 2) {
        return @{ RadiusDip = -1.0; Note = ('only {0} usable rows; ref={1}' -f $fits.Count, ($ref -join ',')) }
    }
    $sorted = $fits | Sort-Object
    $median = [double]$sorted[[int][Math]::Floor($sorted.Count / 2)]
    return @{
        RadiusDip = $median
        Fits      = ($rowsSeen -join '; ')
        RefColor  = ($ref -join ',')
    }
}

# Registers the standard corner check for one window. Fail = defect present
# (this is a "currently red" regression assertion).
function Add-CornerRadiusCheck([string]$WindowName, $Shot, $RectInfo, [double]$ShellMarginDip) {
    $m = Measure-ShellRadiusDip -Shot $Shot -RectInfo $RectInfo -ShellMarginDip $ShellMarginDip
    if ($m.RadiusDip -lt 0) {
        Add-Check "defect:$WindowName-double-radius" 'FAIL' ("measurement failed: " + $m.Note)
        return
    }
    $detail = ("measured shell radius {0:N1} DIP (single-outline target ~8; defect 12) [{1}]" -f `
        $m.RadiusDip, $m.Fits)
    # 13.5 DIP is empirically calibrated: the 12-DIP shell measures ~15.7
    # (the boundary scan reads just inside the 1-DIP stroke and the
    # anti-aliased fringe), an 8-DIP shell lands ~11.5. The threshold sits
    # between the two with roughly equal margin.
    if ($m.RadiusDip -gt 13.5) {
        Add-Check "defect:$WindowName-double-radius" 'FAIL' $detail
    } else {
        Add-Check "defect:$WindowName-double-radius" 'PASS' $detail
    }
}
