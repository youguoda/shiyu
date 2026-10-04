using System.Runtime.InteropServices;
using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>Reads where the cursor is and what screen it is on, in real pixels.</summary>
public static class ScreenGeometry
{
    public static ScreenPoint CursorPosition()
        => NativeMethods.GetCursorPos(out var point)
            ? new ScreenPoint(point.X, point.Y)
            : new ScreenPoint(0, 0);

    /// <summary>
    /// The text caret of whatever window is in the foreground（票 26 的锚点：
    /// 键盘呼出的快速粘贴要贴着插入符出现）. Asked through GetGUIThreadInfo,
    /// which answers for any thread — including other processes' — so the
    /// query works exactly where it is needed, on the app the user was typing
    /// in.
    ///
    /// 取不到就给 null，调用方退回鼠标：一个没有可见系统插入符的应用
    /// （浏览器内容区、多数现代编辑器都自绘光标）不该把浮层锚到客户区
    /// 原点去。锚点取插入符矩形的左下角——文字生长的地方。
    /// </summary>
    public static ScreenPoint? CaretPosition()
    {
        const int CaretVisible = 0x0002; // GUIF_CARETVISIBLE

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return null;
        }

        var thread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        if (thread == 0)
        {
            return null;
        }

        var info = new NativeMethods.GuiThreadInfo
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>(),
        };

        if (!NativeMethods.GetGUIThreadInfo(thread, ref info)
            || (info.flags & CaretVisible) == 0
            || info.hWndCaret == IntPtr.Zero)
        {
            return null;
        }

        var left = new NativeMethods.Point { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        var bottom = new NativeMethods.Point { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };

        if (!NativeMethods.ClientToScreen(info.hWndCaret, ref left)
            || !NativeMethods.ClientToScreen(info.hWndCaret, ref bottom))
        {
            return null;
        }

        return new ScreenPoint(left.X, bottom.Y);
    }

    /// <summary>
    /// The usable area of the screen the point is on — the work area, not the
    /// full monitor, so a badge never hides behind the taskbar.
    /// </summary>
    public static ScreenRect WorkAreaAt(ScreenPoint point)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point { X = point.X, Y = point.Y },
            NativeMethods.MonitorDefaultToNearest);

        var info = new NativeMethods.MonitorInfo
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            // A guess is better than placing the window at the origin of a
            // screen that may not be the one the user is looking at.
            return new ScreenRect(point.X - 400, point.Y - 300, point.X + 400, point.Y + 300);
        }

        return new ScreenRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
    }

    /// <summary>
    /// The monitor's DPI scale at the point, as (x, y) multipliers from
    /// device-independent units to physical pixels. Asked through
    /// GetDpiForMonitor rather than WPF's PresentationSource so it answers
    /// correctly before a window has a handle — the first summon happens
    /// exactly there, and a silently wrong scale put the bar's bottom off
    /// the work area (ticket 30's screenshot probe).
    /// </summary>
    public static (double ScaleX, double ScaleY) ScaleAt(ScreenPoint point)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point { X = point.X, Y = point.Y },
            NativeMethods.MonitorDefaultToNearest);

        if (GetDpiForMonitor(monitor, DpiType.Effective, out var dpiX, out var dpiY) != 0)
        {
            return (1.0, 1.0);
        }

        return (dpiX / 96.0, dpiY / 96.0);
    }

    private enum DpiType
    {
        Effective = 0,
        Angular = 1,
        Raw = 2,
    }

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, DpiType type, out uint dpiX, out uint dpiY);
}
