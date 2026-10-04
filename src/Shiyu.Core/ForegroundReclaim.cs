namespace Shiyu.Core;

/// <summary>
/// 前台窗口的 Win32 操作（票 43）。时序逻辑放在 Core（<see cref="ForegroundReclaim"/>）里用假件测，
/// Windows 层只做一一对应的薄壳。
/// </summary>
public interface IForegroundPlatform
{
    /// <summary>现在的前台窗口；没有给 <see cref="IntPtr.Zero"/>。</summary>
    IntPtr Foreground();

    /// <summary>SetForegroundWindow：被前台锁挡下给 false。</summary>
    bool SetForeground(IntPtr window);

    /// <summary>AllowSetForegroundWindow(ASFW_ANY)：允许失败。</summary>
    void AllowAnyProcess();

    /// <summary>窗口所属线程；窗口没了给 0。</summary>
    uint ThreadOf(IntPtr window);

    uint CurrentThread();

    /// <summary>AttachThreadInput：把 <paramref name="thread"/> 挂到 <paramref name="toThread"/> 上，或解挂。</summary>
    bool Attach(uint thread, uint toThread, bool attach);

    /// <summary>
    /// 发一次 F24（按下、抬起）。这是平台接口里<b>唯一</b>的发键口子：F24 几乎没有软件响应，
    /// 目的是让本进程成为"刚产生输入的进程"以满足 Windows 对 SetForegroundWindow 的许可，
    /// 且不像 Alt 那样有副作用。
    /// </summary>
    void TapF24();

    void Pause(TimeSpan duration);
}

/// <summary>
/// 把前台抢回目标窗口（票 43，取自 Xtranslate 的 forceForeground，最难的那 50 行）。
///
/// <list type="number">
/// <item>前台已经是目标：什么都不发。浮窗隐藏后 Windows 通常已把焦点还回去，此时任何模拟按键
/// 都会落进目标程序——<b>尤其单按 Alt 会激活记事本、Office 的菜单栏，吞掉随后的 Ctrl+V</b>。</item>
/// <item>先试一次普通的 SetForegroundWindow（也就是 <c>ForegroundWindow.Restore</c> 一直在做的）。
/// 成功了就不再有别的动作。</item>
/// <item>前台仍不是目标：AllowSetForegroundWindow → AttachThreadInput（前台线程、目标线程各挂到
/// 本线程，每一对挂不上都容错）→ 发一次 F24 → SetForegroundWindow → 按相反顺序解挂。
/// 解挂在 finally 里：不管成败，不能把别的线程的输入队列永远绑在我们身上。</item>
/// </list>
/// </summary>
public static class ForegroundReclaim
{
    /// <summary>SetForegroundWindow 返回之后，前台变过去可能要一两拍：每拍多久、最多几拍。</summary>
    public static readonly TimeSpan SettleStep = TimeSpan.FromMilliseconds(10);

    public const int SettleSteps = 3;

    /// <returns>此刻目标窗口是否在前台。</returns>
    public static bool Run(IForegroundPlatform platform, IntPtr target)
    {
        if (target == IntPtr.Zero)
        {
            return false;
        }

        if (platform.Foreground() == target)
        {
            return true;
        }

        if (platform.SetForeground(target) && Settled(platform, target))
        {
            return true;
        }

        platform.AllowAnyProcess();

        var me = platform.CurrentThread();
        var attached = new List<uint>();

        // 前台线程、目标线程各挂到本线程：不认识的线程（0）与本线程自己不挂，同一个线程只挂一次。
        foreach (var thread in new[] { platform.ThreadOf(platform.Foreground()), platform.ThreadOf(target) }.Distinct())
        {
            if (thread != 0 && thread != me && platform.Attach(thread, me, attach: true))
            {
                attached.Add(thread);
            }
        }

        try
        {
            platform.TapF24();
            platform.SetForeground(target);
        }
        finally
        {
            for (var index = attached.Count - 1; index >= 0; index--)
            {
                platform.Attach(attached[index], me, attach: false);
            }
        }

        return Settled(platform, target);
    }

    private static bool Settled(IForegroundPlatform platform, IntPtr target)
    {
        for (var step = 0; ; step++)
        {
            if (platform.Foreground() == target)
            {
                return true;
            }

            if (step >= SettleSteps)
            {
                return false;
            }

            platform.Pause(SettleStep);
        }
    }
}
