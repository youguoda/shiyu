using Shiyu.Windows;

namespace Shiyu.Windows.Tests;

/// <summary>
/// 完整性级别的取值（v3 票 30 / 评审 2026-10-16 F-03）。只读自己进程与自己建的窗口——不碰别的程序，
/// 可以和用户正在跑的实例并排跑。
/// </summary>
public sealed class ProcessIntegrityTests
{
    [Fact]
    public void This_process_has_a_readable_level()
    {
        // 开发机上是中（0x2000）；CI 以管理员身份跑，是高（0x3000）。
        Assert.InRange(ProcessIntegrity.Self, 0x1000u, 0x4000u);
    }

    [Fact]
    public void A_window_of_this_process_reads_as_this_process_and_takes_input()
    {
        using var window = new MessageWindow();

        Assert.Equal(ProcessIntegrity.Self, ProcessIntegrity.OfWindow(window.Handle));
        Assert.False(ProcessIntegrity.BlocksInput(window.Handle));
    }

    [Fact]
    public void No_window_has_no_level()
    {
        Assert.Null(ProcessIntegrity.OfWindow(IntPtr.Zero));
        Assert.False(new ForegroundWindow(IntPtr.Zero).RunsElevated);
    }
}
