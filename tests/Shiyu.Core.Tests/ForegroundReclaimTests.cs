using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 把前台抢回来的兜底（票 43，取自 Xtranslate 的 forceForeground）：只有 Restore 之后前台仍不是
/// 目标窗口时，才走强制序列——AllowSetForegroundWindow → AttachThreadInput（把前台线程、目标线程
/// 挂到本线程，每一对挂不上都容错）→ 发一次 F24 → SetForegroundWindow → 按相反顺序解挂。
/// 前台已经是目标窗口时什么都不发；<b>绝不发 Alt</b>：单按 Alt 会激活记事本、Office 的菜单栏，
/// 吞掉随后的 Ctrl+V。
/// </summary>
public class ForegroundReclaimTests
{
    private static readonly IntPtr Target = new(0x1001);
    private static readonly IntPtr Other = new(0x2002);
    private const uint Me = 100;
    private const uint TargetThread = 200;
    private const uint OtherThread = 300;

    private sealed class FakeForeground : IForegroundPlatform
    {
        public IntPtr Front { get; set; } = Other;

        public List<string> Calls { get; } = [];

        /// <summary>第几次 SetForegroundWindow（从 1 数）是否成功；默认总是成功。</summary>
        public Func<int, bool> SetForegroundWorks { get; set; } = _ => true;

        /// <summary>这些线程挂不上。</summary>
        public HashSet<uint> AttachFails { get; } = [];

        /// <summary>成功的 SetForeground 之后，前台再过几次轮询才真的变过去。</summary>
        public int SettleDelayPolls { get; set; }

        private int _setAttempts;
        private int _pendingPolls = -1;

        public IntPtr Foreground()
        {
            Calls.Add("poll");
            if (_pendingPolls > 0)
            {
                _pendingPolls--;
            }
            else if (_pendingPolls == 0)
            {
                _pendingPolls = -1;
                Front = Target;
            }

            return Front;
        }

        public bool SetForeground(IntPtr window)
        {
            Calls.Add($"set:{window.ToInt32():X}");
            var works = SetForegroundWorks(++_setAttempts);
            if (works)
            {
                if (SettleDelayPolls > 0)
                {
                    _pendingPolls = SettleDelayPolls;
                }
                else
                {
                    Front = window;
                }
            }

            return works;
        }

        public void AllowAnyProcess() => Calls.Add("allow");

        public uint ThreadOf(IntPtr window)
            => window == Target ? TargetThread : window == Other ? OtherThread : 0;

        public uint CurrentThread() => Me;

        public bool Attach(uint thread, uint toThread, bool attach)
        {
            Calls.Add($"{(attach ? "attach" : "detach")}:{thread}->{toThread}");
            return attach && AttachFails.Contains(thread) ? false : true;
        }

        public void TapF24() => Calls.Add("F24");

        public void Pause(TimeSpan duration) => Calls.Add("pause");
    }

    private static IEnumerable<string> Meaningful(FakeForeground platform)
        => platform.Calls.Where(call => call is not "poll" and not "pause");

    [Fact]
    public void When_the_target_is_already_in_front_nothing_is_sent_at_all()
    {
        var platform = new FakeForeground { Front = Target };

        Assert.True(ForegroundReclaim.Run(platform, Target));

        Assert.Empty(Meaningful(platform));
    }

    [Fact]
    public void A_plain_SetForegroundWindow_that_works_sends_no_keystroke_and_attaches_nothing()
    {
        var platform = new FakeForeground();

        Assert.True(ForegroundReclaim.Run(platform, Target));

        Assert.Equal(["set:1001"], Meaningful(platform));
    }

    [Fact]
    public void A_foreground_that_arrives_a_moment_late_is_waited_for_rather_than_forced()
    {
        var platform = new FakeForeground { SettleDelayPolls = 2 };

        Assert.True(ForegroundReclaim.Run(platform, Target));

        Assert.DoesNotContain("F24", platform.Calls);
        Assert.DoesNotContain("allow", platform.Calls);
    }

    [Fact]
    public void When_the_plain_attempt_is_refused_the_forced_sequence_runs_in_this_exact_order()
    {
        var platform = new FakeForeground { SetForegroundWorks = attempt => attempt >= 2 };

        Assert.True(ForegroundReclaim.Run(platform, Target));

        Assert.Equal(
            [
                "set:1001",                       // 先试 Restore
                "allow",                          // AllowSetForegroundWindow(ASFW_ANY)
                $"attach:{OtherThread}->{Me}",    // 前台线程挂到本线程
                $"attach:{TargetThread}->{Me}",   // 目标线程挂到本线程
                "F24",                            // 一次 F24：让本进程成为"刚产生输入的进程"
                "set:1001",
                $"detach:{TargetThread}->{Me}",   // 按相反顺序解挂
                $"detach:{OtherThread}->{Me}",
            ],
            Meaningful(platform));
    }

    [Fact]
    public void The_forced_sequence_taps_F24_exactly_once_and_never_anything_else()
    {
        // F24 几乎没有软件响应，所以不会像 Alt 那样激活菜单栏。平台接口里只有 TapF24 一个发键的口子，
        // 这里再钉住：强制序列里它恰好一次，别的路径里一次都没有。
        var forced = new FakeForeground { SetForegroundWorks = attempt => attempt >= 2 };
        ForegroundReclaim.Run(forced, Target);
        Assert.Single(forced.Calls, "F24");

        var plain = new FakeForeground();
        ForegroundReclaim.Run(plain, Target);
        Assert.DoesNotContain("F24", plain.Calls);
    }

    [Fact]
    public void Detaching_happens_even_when_the_forced_attempt_fails_too()
    {
        var platform = new FakeForeground { SetForegroundWorks = _ => false };

        Assert.False(ForegroundReclaim.Run(platform, Target));

        Assert.Contains($"detach:{TargetThread}->{Me}", platform.Calls);
        Assert.Contains($"detach:{OtherThread}->{Me}", platform.Calls);
    }

    [Fact]
    public void A_thread_that_cannot_be_attached_is_tolerated_and_is_not_detached()
    {
        var platform = new FakeForeground { SetForegroundWorks = attempt => attempt >= 2 };
        platform.AttachFails.Add(OtherThread);

        Assert.True(ForegroundReclaim.Run(platform, Target));

        // 前台线程挂不上：继续往下走，且只解挂挂上了的那个。
        Assert.Contains("F24", platform.Calls);
        Assert.Contains($"attach:{TargetThread}->{Me}", platform.Calls);
        Assert.DoesNotContain($"detach:{OtherThread}->{Me}", platform.Calls);
        Assert.Contains($"detach:{TargetThread}->{Me}", platform.Calls);
    }

    [Fact]
    public void The_same_thread_is_attached_once_and_our_own_thread_never_to_itself()
    {
        var platform = new FakeForeground { Front = new IntPtr(0x3003), SetForegroundWorks = attempt => attempt >= 2 };

        // 前台窗口认不出线程（0）：只挂目标线程。
        ForegroundReclaim.Run(platform, Target);

        Assert.Single(platform.Calls, call => call.StartsWith("attach:", StringComparison.Ordinal));
        Assert.DoesNotContain($"attach:{Me}->{Me}", platform.Calls);
    }

    [Fact]
    public void Returns_false_when_even_the_forced_sequence_does_not_bring_the_window_to_the_front()
    {
        var platform = new FakeForeground { SetForegroundWorks = _ => false };

        Assert.False(ForegroundReclaim.Run(platform, Target));
    }

    [Fact]
    public void No_window_means_nothing_to_restore_and_nothing_is_touched()
    {
        var platform = new FakeForeground();

        Assert.False(ForegroundReclaim.Run(platform, IntPtr.Zero));

        Assert.Empty(platform.Calls);
    }
}
