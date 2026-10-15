using Shiyu.Core;
using Shiyu.Core.Tests.Fakes;

namespace Shiyu.Core.Tests;

/// <summary>
/// 划词取词欠下的还原排队还（用户实录 2026-10-15：开着拖选徽标时常弹「取词后未能还原你原本的剪贴板
/// 内容」）。剪贴板被别的程序占着不算失败，隔一会儿再试；后到的债排在没还上的旧债后面。
/// </summary>
public class RestoreQueueTests
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
    ];

    private static DeferredCapture Debt(string text)
        => new(text, CaptureOutcome.Captured, TextBackup("原文"), SequenceBefore: 0, SequenceSealed: 1);

    private static ClipboardBackup TextBackup(string text)
        => new(text, null, null, [], Excluded: false, LostFormats: [], Empty: false);

    /// <summary>照剧本回答的还原：每笔债一串结局，用到最后一个就一直是它。记下尝试的先后。</summary>
    private sealed class Script
    {
        private readonly Dictionary<string, Queue<CaptureRestore>> _answers = [];

        public List<string> Attempts { get; } = [];

        public Script Answer(string text, params CaptureRestore[] results)
        {
            _answers[text] = new Queue<CaptureRestore>(results);
            return this;
        }

        public CaptureRestore Restore(DeferredCapture debt)
        {
            Attempts.Add(debt.Text!);
            var answers = _answers[debt.Text!];
            return answers.Count > 1 ? answers.Dequeue() : answers.Peek();
        }
    }

    // --- 一笔债 -------------------------------------------------------------------------------

    [Fact]
    public void A_restore_that_goes_through_is_settled_at_once()
    {
        var script = new Script().Answer("一", CaptureRestore.Restored);
        var queue = new RestoreQueue(script.Restore, Delays);

        var progress = queue.Owe(Debt("一"));

        Assert.Equal([CaptureRestore.Restored], progress.Settled.Select(s => s.Result));
        Assert.Null(progress.RetryAfter);
        Assert.False(queue.Owing);
    }

    [Fact]
    public void A_busy_clipboard_is_tried_again_later_and_later()
    {
        var script = new Script().Answer("一", CaptureRestore.Busy, CaptureRestore.Busy, CaptureRestore.Restored);
        var queue = new RestoreQueue(script.Restore, Delays);

        var first = queue.Owe(Debt("一"));
        Assert.Empty(first.Settled);
        Assert.Equal(Delays[0], first.RetryAfter);
        Assert.True(queue.Owing);

        var second = queue.Retry();
        Assert.Empty(second.Settled);
        Assert.Equal(Delays[1], second.RetryAfter);

        var third = queue.Retry();
        Assert.Equal([CaptureRestore.Restored], third.Settled.Select(s => s.Result));
        Assert.Null(third.RetryAfter);
        Assert.False(queue.Owing);
    }

    [Fact]
    public void A_clipboard_busy_through_every_retry_is_finally_reported_busy()
    {
        var script = new Script().Answer("一", CaptureRestore.Busy);
        var queue = new RestoreQueue(script.Restore, Delays);

        var progress = queue.Owe(Debt("一"));
        var retries = 0;
        while (progress.RetryAfter is not null)
        {
            progress = queue.Retry();
            retries++;
        }

        Assert.Equal(Delays.Length, retries);
        Assert.Equal([CaptureRestore.Busy], progress.Settled.Select(s => s.Result));
        Assert.Equal(1 + Delays.Length, script.Attempts.Count);
        Assert.False(queue.Owing);
    }

    [Theory]
    [InlineData(CaptureRestore.Untouched)]
    [InlineData(CaptureRestore.Superseded)]
    [InlineData(CaptureRestore.Withheld)]
    [InlineData(CaptureRestore.Unrestorable)]
    public void Only_a_busy_clipboard_is_worth_another_try(CaptureRestore result)
    {
        var script = new Script().Answer("一", result);
        var queue = new RestoreQueue(script.Restore, Delays);

        var progress = queue.Owe(Debt("一"));

        Assert.Equal([result], progress.Settled.Select(s => s.Result));
        Assert.Null(progress.RetryAfter);
        Assert.Single(script.Attempts);
    }

    // --- 次序 ---------------------------------------------------------------------------------

    [Fact]
    public void A_debt_that_falls_due_while_another_waits_queues_behind_it()
    {
        var script = new Script()
            .Answer("旧", CaptureRestore.Busy, CaptureRestore.Restored)
            .Answer("新", CaptureRestore.Restored);
        var queue = new RestoreQueue(script.Restore, Delays);

        queue.Owe(Debt("旧"));
        var behind = queue.Owe(Debt("新"));

        // 没有越过排头去还，也不另约时间——排头约好的那一回还在。
        Assert.Empty(behind.Settled);
        Assert.Null(behind.RetryAfter);
        Assert.Equal(["旧"], script.Attempts);

        var retry = queue.Retry();
        Assert.Equal(["旧", "新"], retry.Settled.Select(s => s.Debt.Text));
        Assert.Equal(["旧", "旧", "新"], script.Attempts);
        Assert.False(queue.Owing);
    }

    [Fact]
    public void Each_debt_gets_the_whole_schedule()
    {
        var script = new Script()
            .Answer("一", CaptureRestore.Busy, CaptureRestore.Busy, CaptureRestore.Restored)
            .Answer("二", CaptureRestore.Busy, CaptureRestore.Restored);
        var queue = new RestoreQueue(script.Restore, Delays);

        queue.Owe(Debt("一"));
        queue.Owe(Debt("二"));
        queue.Retry();
        var progress = queue.Retry();

        // 一 结清；二 头一回撞上被占着，从头约起。
        Assert.Equal(["一"], progress.Settled.Select(s => s.Debt.Text));
        Assert.Equal(Delays[0], progress.RetryAfter);
    }

    // --- 退出 ---------------------------------------------------------------------------------

    [Fact]
    public void Exit_waits_out_a_busy_clipboard_and_pays_everything_in_order()
    {
        var script = new Script()
            .Answer("一", CaptureRestore.Busy, CaptureRestore.Busy, CaptureRestore.Restored)
            .Answer("二", CaptureRestore.Restored);
        var queue = new RestoreQueue(script.Restore, Delays);
        var waits = new List<TimeSpan>();

        queue.Owe(Debt("一"));   // 撞上被占着，约了 Delays[0]——还没到，就退出了
        queue.Owe(Debt("二"));
        var settled = queue.Drain(waits.Add);

        Assert.Equal(["一", "二"], settled.Select(s => s.Debt.Text));
        Assert.All(settled, s => Assert.Equal(CaptureRestore.Restored, s.Result));
        Assert.Equal([Delays[1]], waits);
        Assert.False(queue.Owing);
    }

    [Fact]
    public void Exit_gives_up_once_the_schedule_runs_out()
    {
        var script = new Script().Answer("一", CaptureRestore.Busy);
        var queue = new RestoreQueue(script.Restore, Delays);
        var waits = new List<TimeSpan>();

        queue.Owe(Debt("一"));
        var settled = queue.Drain(waits.Add);

        Assert.Equal([CaptureRestore.Busy], settled.Select(s => s.Result));
        Assert.Equal(Delays.Skip(1), waits);
    }

    [Fact]
    public void Draining_with_nothing_owed_does_nothing()
    {
        var queue = new RestoreQueue(_ => throw new InvalidOperationException("nothing to restore"), Delays);

        Assert.Empty(queue.Drain(_ => throw new InvalidOperationException("nothing to wait for")));
    }

    // --- 端到端：真的取词、假的剪贴板 ------------------------------------------------------------

    private static readonly CaptureTiming Timing =
        new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// 第一笔真借（用户原文借走，"第一段"以拾语名义留在剪贴板上，点了徽标、债交给面板）；第二笔
    /// （面板还没上屏又一次拖选）照 SelectionDebtTests 的做法手工记：借走的是"第一段"。
    /// </summary>
    private static (FakeCapturePlatform Platform, SelectionCapture Capture, SelectionDebt Ledger) TwoDebts()
    {
        var platform = new FakeCapturePlatform { AnswersAfterPolls = 1, Answer = "第一段" };
        platform.PutOnClipboard("用户原文");
        var capture = new SelectionCapture(platform, Timing);
        var ledger = new SelectionDebt();

        ledger.Offer(capture.CaptureDeferRestore(requireRestorable: true)!);
        ledger.Accepted();

        platform.WriteClipboardText("第二段"); // 第二笔以拾语名义封存
        var sealedAt = platform.ClipboardSequenceNumber();
        ledger.Offer(new DeferredCapture(
            "第二段", CaptureOutcome.Captured, TextBackup("第一段"), sealedAt - 1, sealedAt));

        return (platform, capture, ledger);
    }

    [Fact]
    public void Chained_debts_end_on_the_users_original_even_when_the_clipboard_is_busy()
    {
        var (platform, capture, ledger) = TwoDebts();
        var queue = new RestoreQueue(capture.Restore, Delays);
        platform.RestoreBusyTimes = 1;

        foreach (var due in ledger.Exit().Restore)
        {
            queue.Owe(due);
        }

        var settled = queue.Drain(_ => { });

        Assert.Equal("用户原文", platform.CurrentClipboard);
        Assert.All(settled, s => Assert.Equal(CaptureRestore.Restored, s.Result));
    }

    [Fact]
    public void Without_the_queue_a_busy_newer_debt_would_cost_the_user_the_original()
    {
        // 为什么要排队：新债撞上被占着、旧债接着就还，旧债认不出剪贴板（还是"第二段"），当用户复制过
        // 而销账——用户原文就没了。
        var (platform, capture, ledger) = TwoDebts();
        platform.RestoreBusyTimes = 1;

        var results = ledger.Exit().Restore.Select(capture.Restore).ToList();

        Assert.Equal([CaptureRestore.Busy, CaptureRestore.Superseded], results);
        Assert.Equal("第二段", platform.CurrentClipboard);
    }

    [Fact]
    public void A_copy_the_user_makes_while_a_restore_waits_is_kept()
    {
        var platform = new FakeCapturePlatform { AnswersAfterPolls = 1, Answer = "选中的" };
        platform.PutOnClipboard("用户原文");
        var capture = new SelectionCapture(platform, Timing);
        var queue = new RestoreQueue(capture.Restore, Delays);
        platform.RestoreBusyTimes = 1;

        Assert.NotNull(queue.Owe(capture.CaptureDeferRestore(requireRestorable: true)!).RetryAfter);
        platform.PutOnClipboard("用户新复制的");

        var progress = queue.Retry();

        Assert.Equal([CaptureRestore.Superseded], progress.Settled.Select(s => s.Result));
        Assert.Equal("用户新复制的", platform.CurrentClipboard);
    }

    [Fact]
    public void A_restore_that_waited_its_turn_puts_the_original_back()
    {
        var platform = new FakeCapturePlatform { AnswersAfterPolls = 1, Answer = "选中的" };
        platform.PutRich("用户原文", "<i>用户原文</i>", @"{\rtf1 用户原文}");
        var capture = new SelectionCapture(platform, Timing);
        var queue = new RestoreQueue(capture.Restore, Delays);
        platform.RestoreBusyTimes = 2;

        var progress = queue.Owe(capture.CaptureDeferRestore(requireRestorable: true)!);
        while (progress.RetryAfter is not null)
        {
            progress = queue.Retry();
        }

        Assert.Equal([CaptureRestore.Restored], progress.Settled.Select(s => s.Result));
        Assert.Equal("用户原文", platform.CurrentClipboard);
        Assert.Equal("<i>用户原文</i>", platform.CurrentHtml);
    }
}
