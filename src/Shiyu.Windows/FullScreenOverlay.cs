using System.Runtime.InteropServices;

namespace Shiyu.Windows;

/// <summary>
/// 前台是不是一扇盖住整块屏幕的置顶窗（用户日志 2026-10-14：开着拖选徽标时，一次还原失败后
/// 78 毫秒记下的是一张截图）。截图工具框选区域的那层蒙版——微信、QQ、Snipaste 之类——就是这个
/// 样子：没有标题栏、置顶、连任务栏一起盖住。在那里拖出一个框再收到 Ctrl+C，截图工具会把选区
/// 复制走、当场收工，紧接着取词的还原又把截图从剪贴板上换掉。全屏的视频、演示也长这样，那里
/// 没有字可选，一并放过。
///
/// 三条都得占：普通窗口最大化也不盖任务栏、有标题栏；置顶的小工具盖不满屏幕。
/// </summary>
public static class FullScreenOverlay
{
    public static bool IsForeground() => Covers(NativeMethods.GetForegroundWindow());

    /// <summary>这扇窗没有标题栏、置顶，并盖住了它所在的整块显示器（含任务栏）。</summary>
    internal static bool Covers(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var style = (long)NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle);
        var exStyle = (long)NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle);
        if ((exStyle & NativeMethods.WsExTopmost) == 0
            || (style & NativeMethods.WsCaption) == NativeMethods.WsCaption)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(window, out var rect))
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { cbSize = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        var screen = info.rcMonitor;
        return rect.Left <= screen.Left
            && rect.Top <= screen.Top
            && rect.Right >= screen.Right
            && rect.Bottom >= screen.Bottom;
    }
}
