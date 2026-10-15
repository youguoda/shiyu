using System.Runtime.InteropServices;
using Shiyu.Windows;

namespace Shiyu.Windows.Tests;

/// <summary>
/// 截图工具框选区域的蒙版长什么样：没有标题栏、置顶、盖满整块显示器（用户日志 2026-10-14：开着
/// 拖选徽标时，一次还原失败后 78 毫秒记下的是一张截图）。拖选取词在这样的前台一律不发 Ctrl+C。
///
/// 用真的窗口量，但从不显示——不显示就不打扰任何人，可以和用户正在跑的实例并排安全地跑。
/// </summary>
public sealed class FullScreenOverlayTests
{
    private const uint WsPopup = 0x80000000;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsExToolWindow = 0x00000080;

    /// <summary>主显示器（含任务栏）的整块矩形，物理像素。</summary>
    private static NativeMethods.Rect PrimaryScreen()
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point(), NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { cbSize = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        Assert.True(NativeMethods.GetMonitorInfoW(monitor, ref info));
        return info.rcMonitor;
    }

    private static bool Covers(uint style, uint exStyle, Func<NativeMethods.Rect, NativeMethods.Rect> bounds)
    {
        var rect = bounds(PrimaryScreen());
        var window = NativeMethods.CreateWindowExW(
            exStyle, "STATIC", null, style,
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);

        try
        {
            return FullScreenOverlay.Covers(window);
        }
        finally
        {
            NativeMethods.DestroyWindow(window);
        }
    }

    private static NativeMethods.Rect Whole(NativeMethods.Rect screen) => screen;

    [Fact]
    public void A_borderless_topmost_window_over_the_whole_screen_is_an_overlay()
        => Assert.True(Covers(WsPopup, NativeMethods.WsExTopmost | WsExToolWindow, Whole));

    [Fact]
    public void One_that_spills_past_the_screen_still_is()
        => Assert.True(Covers(WsPopup, NativeMethods.WsExTopmost, screen => new NativeMethods.Rect
        {
            Left = screen.Left - 8,
            Top = screen.Top - 8,
            Right = screen.Right + 8,
            Bottom = screen.Bottom + 8,
        }));

    [Fact]
    public void A_full_screen_window_that_is_not_topmost_is_not()
        => Assert.False(Covers(WsPopup, 0, Whole));

    [Fact]
    public void A_topmost_window_with_a_title_bar_is_not()
        => Assert.False(Covers(WsOverlappedWindow, NativeMethods.WsExTopmost, Whole));

    [Fact]
    public void A_topmost_window_short_of_the_whole_screen_is_not()
        => Assert.False(Covers(WsPopup, NativeMethods.WsExTopmost, screen => screen with
        {
            Bottom = screen.Bottom - 48,
        }));

    [Fact]
    public void No_window_is_not()
        => Assert.False(FullScreenOverlay.Covers(IntPtr.Zero));
}
