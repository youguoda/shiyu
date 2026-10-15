using Shiyu.Core;
using Shiyu.Core.Tests.Fakes;

namespace Shiyu.Core.Tests;

/// <summary>
/// 取词之后把用户的剪贴板还回去（用户实录 2026-10-15：开着「翻译徽标」与「拖选文字后也弹出」时常弹
/// 「取词后未能还原你原本的剪贴板内容」，两个开关一关就好了）。原来的做法只借走文字；Ctrl+C 什么都没
/// 取到也要写回一遍；判断要不要还时，还得先打开剪贴板读一遍——别的程序正占着剪贴板时，后两样都会失败，
/// 前一样则把图片清空、把密码去掉「勿记录」标记写回。这里把每一种处境钉住。
/// </summary>
public class CaptureRestoreTests
{
    private static readonly CaptureTiming Timing =
        new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));

    private static (FakeCapturePlatform Platform, SelectionCapture Capture) Build(
        Action<FakeCapturePlatform> clipboard, int? answersAfterPolls = 1, string? answer = "the selected words")
    {
        var platform = new FakeCapturePlatform { AnswersAfterPolls = answersAfterPolls, Answer = answer };
        clipboard(platform);
        return (platform, new SelectionCapture(platform, Timing));
    }

    // --- 不写，就不会写失败 -------------------------------------------------------------------

    [Fact]
    public void A_drag_over_something_that_copies_nothing_never_writes_the_clipboard()
    {
        // 拖窗口、拖滚动条、在不认 Ctrl+C 的地方拖：剪贴板根本没变。
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"), answersAfterPolls: null);

        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        Assert.Equal(CaptureOutcome.NothingCaptured, deferred.Outcome);
        Assert.Equal(CaptureRestore.Untouched, capture.Restore(deferred));
        Assert.Empty(platform.Restores);
        Assert.Empty(platform.Writes);
        Assert.Equal("the user's own", platform.CurrentClipboard);
    }

    [Fact]
    public void Deciding_to_restore_does_not_open_the_clipboard()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        // 别的程序（Windows 剪贴板历史、还在补写格式的浏览器）此刻占着剪贴板，读不了。
        platform.ReadFails = true;
        var readsBefore = platform.Events.Count(e => e == "read");

        Assert.Equal(CaptureRestore.Restored, capture.Restore(deferred));

        // 凭序列号判断：一次都没读。原来要先读一遍文字作比较，这一读失败就报"未能还原"。
        Assert.Equal(readsBefore, platform.Events.Count(e => e == "read"));
        Assert.Equal("the user's own", platform.CurrentClipboard);
    }

    // --- 剪贴板被占着：是"稍后再试"，不是"丢了" ---------------------------------------------------

    [Fact]
    public void A_busy_clipboard_is_busy_and_the_next_try_puts_it_back()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;
        platform.RestoreBusyTimes = 1;

        Assert.Equal(CaptureRestore.Busy, capture.Restore(deferred));
        Assert.Equal(CaptureRestore.Restored, capture.Restore(deferred));
        Assert.Equal("the user's own", platform.CurrentClipboard);
    }

    [Fact]
    public void A_user_copy_between_two_tries_is_kept()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;
        platform.RestoreBusyTimes = 1;

        Assert.Equal(CaptureRestore.Busy, capture.Restore(deferred));
        platform.PutOnClipboard("copied while we waited");

        Assert.Equal(CaptureRestore.Superseded, capture.Restore(deferred));
        Assert.Equal("copied while we waited", platform.CurrentClipboard);
    }

    [Fact]
    public void A_clipboard_too_busy_to_read_after_a_write_of_our_own_is_busy_not_superseded()
    {
        // 封存之后拾语自己又写过（后一笔取词还回来的、从历史里重新复制的），得读一读才知道是不是
        // 我们留下的那段字；读不了，先当"稍后再试"。
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;
        platform.WriteClipboardText("the selected words");
        platform.ReadFails = true;

        Assert.Equal(CaptureRestore.Busy, capture.Restore(deferred));
    }

    // --- 用户自己的复制，一笔都不撤 -----------------------------------------------------------------

    [Fact]
    public void Copying_the_same_selection_by_hand_is_kept()
    {
        // 拖选完顺手按 Ctrl+C——再常见不过。剪贴板里还是那段字，原来比文字，一样就还原，把用户这一下
        // 复制给撤了：徽标一淡出，粘贴出来的又是旧内容。
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        platform.PutOnClipboard("the selected words");

        Assert.Equal(CaptureRestore.Superseded, capture.Restore(deferred));
        Assert.Equal("the selected words", platform.CurrentClipboard);
        Assert.Empty(platform.Restores);
    }

    [Fact]
    public void Copying_the_same_selection_by_hand_is_kept_even_when_our_rewrite_failed()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        platform.WriteFails = true;
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;
        platform.WriteFails = false;

        platform.PutOnClipboard("the selected words");

        Assert.Equal(CaptureRestore.Superseded, capture.Restore(deferred));
        Assert.Equal("the selected words", platform.CurrentClipboard);
    }

    [Fact]
    public void An_entry_copied_back_from_the_history_is_kept()
    {
        // 拾语自己的写入（窄条里点一条重新复制）：owner 是拾语，读一读——不是我们留下的那段字，销账。
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        platform.WriteClipboardText("an older entry");

        Assert.Equal(CaptureRestore.Superseded, capture.Restore(deferred));
        Assert.Equal("an older entry", platform.CurrentClipboard);
    }

    [Fact]
    public void Our_own_rewrite_still_counts_as_ours_when_nothing_else_happened()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        Assert.True(platform.OwnsClipboard());
        Assert.Equal(CaptureRestore.Restored, capture.Restore(deferred));
        Assert.Equal("the user's own", platform.CurrentClipboard);
    }

    // --- 原样还：不只是文字 -----------------------------------------------------------------------

    [Fact]
    public void Formatted_text_comes_back_with_its_html_and_rtf()
    {
        var (platform, capture) = Build(p => p.PutRich("plain words", "<b>plain words</b>", @"{\rtf1 plain words}"));
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        Assert.Equal(CaptureRestore.Restored, capture.Restore(deferred));
        Assert.Equal("plain words", platform.CurrentClipboard);
        Assert.Equal("<b>plain words</b>", platform.CurrentHtml);
        Assert.Equal(@"{\rtf1 plain words}", platform.CurrentRtf);
    }

    [Fact]
    public void Copied_files_come_back_as_files()
    {
        var (platform, capture) = Build(p => p.PutFiles(@"C:\work\report.pdf", @"C:\work\data.csv"));
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        Assert.Equal(CaptureRestore.Restored, capture.Restore(deferred));
        Assert.Equal([@"C:\work\report.pdf", @"C:\work\data.csv"], platform.CurrentFiles);
    }

    [Fact]
    public void A_copy_that_answers_with_no_text_is_put_back_at_once()
    {
        // 在资源管理器里拖选了文件：Ctrl+C 复制出的是文件，不是文字——取不到，但剪贴板被换掉了。
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"), answer: null);

        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        Assert.Equal(CaptureOutcome.NothingCaptured, deferred.Outcome);
        Assert.Null(deferred.SequenceSealed);
        Assert.Equal(CaptureRestore.Restored, capture.Restore(deferred));
        Assert.Equal("the user's own", platform.CurrentClipboard);
    }

    [Fact]
    public void A_failed_rewrite_still_tells_a_later_user_copy_apart()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        platform.WriteFails = true;
        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;
        platform.WriteFails = false;

        platform.PutOnClipboard("copied during the badge");

        Assert.Equal(CaptureRestore.Superseded, capture.Restore(deferred));
        Assert.Equal("copied during the badge", platform.CurrentClipboard);
    }

    // --- 写不回去的，拖选不借 ---------------------------------------------------------------------

    [Fact]
    public void A_drag_never_borrows_an_image_it_could_not_give_back()
    {
        var (platform, capture) = Build(p => p.PutImage());

        Assert.Null(capture.CaptureDeferRestore(requireRestorable: true));

        // 没有按键发出去，截图原封不动（原来会被清空）。
        Assert.Equal(0, platform.CopyKeystrokes);
        Assert.True(platform.CurrentlyHoldsImage);
    }

    [Fact]
    public void A_drag_never_borrows_a_password_managers_copy()
    {
        var (platform, capture) = Build(p => p.PutExcluded("correct horse battery staple"));

        Assert.Null(capture.CaptureDeferRestore(requireRestorable: true));
        Assert.Equal(0, platform.CopyKeystrokes);
        Assert.Equal("correct horse battery staple", platform.CurrentClipboard);
    }

    [Fact]
    public void A_drag_never_borrows_files_waiting_to_be_moved()
    {
        var (platform, capture) = Build(p => p.PutCutFiles(@"C:\work\report.pdf"));

        Assert.Null(capture.CaptureDeferRestore(requireRestorable: true));
        Assert.Equal(0, platform.CopyKeystrokes);
        Assert.Equal([@"C:\work\report.pdf"], platform.CurrentFiles);
    }

    [Fact]
    public void A_drag_after_copying_from_word_still_works_and_gives_back_the_formatted_text()
    {
        // Word、Excel 复制的文字附着图元文件、位图。它们写不回去，但文字和格式写得回去——按"写不回去
        // 就不借"一刀切，开着 Office 干活的人拖选徽标就全哑了。
        var (platform, capture) = Build(p => p.PutOffice("quarterly", "<b>quarterly</b>", @"{\rtf1 quarterly}"));

        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        Assert.Equal(CaptureOutcome.Captured, deferred.Outcome);
        Assert.Equal(CaptureRestore.Restored, capture.Restore(deferred));
        Assert.Equal("quarterly", platform.CurrentClipboard);
        Assert.Equal("<b>quarterly</b>", platform.CurrentHtml);
        Assert.Equal(@"{\rtf1 quarterly}", platform.CurrentRtf);
    }

    // --- 热键：点名要取的，照取，结局如实 -------------------------------------------------------------

    [Fact]
    public void The_hotkey_still_captures_over_an_image_and_says_it_could_not_put_it_back()
    {
        var (platform, capture) = Build(p => p.PutImage());

        var result = capture.Capture();

        // 原来写回 null（清空）还报"还原了"——截图悄悄没了。
        Assert.True(result.Succeeded);
        Assert.Equal(CaptureRestore.Unrestorable, result.Restore);
        Assert.False(result.ClipboardRestored);
        Assert.Empty(platform.Restores);
        Assert.Equal("the selected words", platform.CurrentClipboard);
    }

    [Fact]
    public void The_hotkey_gives_back_an_office_copy_with_its_formatting()
    {
        var (platform, capture) = Build(p => p.PutOffice("quarterly", "<b>quarterly</b>", @"{\rtf1 quarterly}"));

        var result = capture.Capture();

        Assert.Equal(CaptureRestore.Restored, result.Restore);
        Assert.True(result.ClipboardRestored);
        Assert.Equal("<b>quarterly</b>", platform.CurrentHtml);
    }

    [Fact]
    public void A_hotkey_that_copies_nothing_leaves_the_clipboard_untouched()
    {
        var (platform, capture) = Build(p => p.PutImage(), answersAfterPolls: null);

        var result = capture.Capture();

        // 剪贴板没动过：截图原封不动，不算失败，也不写一笔。
        Assert.Equal(CaptureOutcome.NothingCaptured, result.Outcome);
        Assert.Equal(CaptureRestore.Untouched, result.Restore);
        Assert.True(result.ClipboardRestored);
        Assert.True(platform.CurrentlyHoldsImage);
        Assert.Empty(platform.Writes);
    }

    [Fact]
    public void The_hotkey_never_writes_a_password_back_without_its_marker()
    {
        var (platform, capture) = Build(p => p.PutExcluded("correct horse battery staple"));

        var result = capture.Capture();

        // 故意不写回：写回时「勿记录」标记会丢，Windows 剪贴板历史就会把密码记下来。不算要转告的失败。
        Assert.True(result.Succeeded);
        Assert.True(result.ClipboardRestored);
        Assert.Empty(platform.Restores);
        Assert.DoesNotContain("correct horse battery staple", platform.Writes);
    }

    [Fact]
    public void The_hotkey_waits_out_a_busy_clipboard_before_giving_up()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        platform.RestoreBusyTimes = 2;

        var result = capture.Capture();

        Assert.True(result.ClipboardRestored);
        Assert.Equal("the user's own", platform.CurrentClipboard);
        Assert.Equal(2, platform.Events.Count(e => e == "restore-busy"));
    }

    [Fact]
    public void The_hotkey_reports_a_clipboard_that_stays_busy_after_every_retry()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        platform.RestoreBusyTimes = 99;

        var result = capture.Capture();

        Assert.False(result.ClipboardRestored);
        Assert.Equal(1 + Timing.RestoreRetryDelays.Count, platform.Events.Count(e => e == "restore-busy"));
    }

    [Fact]
    public void The_retry_schedule_grows_and_stays_under_two_seconds()
    {
        var delays = CaptureTiming.Default.RestoreRetryDelays;

        Assert.True(delays.Count >= 3);
        Assert.Equal(delays.OrderBy(d => d), delays);
        Assert.True(delays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d) < TimeSpan.FromSeconds(2));
    }

    // --- 答案到了、剪贴板却正被别的程序读着 ------------------------------------------------------
    //
    // 实机探针 2026-10-15：两个模拟的读者每次变化后都占一阵剪贴板时，一半的取词因为读不到答案而作罢
    // ——热键弹「剪贴板正被其他程序占用」，拖选干脆不出徽标。

    [Fact]
    public void An_answer_that_is_busy_for_a_moment_is_still_read()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        platform.ReadBusyTimes = 2;

        var deferred = capture.CaptureDeferRestore(requireRestorable: true)!;

        Assert.Equal(CaptureOutcome.Captured, deferred.Outcome);
        Assert.Equal("the selected words", deferred.Text);
    }

    [Fact]
    public void The_hotkey_reads_a_busy_answer_too()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        platform.ReadBusyTimes = Timing.BusyReadDelays.Count;

        var result = capture.Capture();

        Assert.True(result.Succeeded);
        Assert.Equal(CaptureRestore.Restored, result.Restore);
        Assert.Equal("the user's own", platform.CurrentClipboard);
    }

    [Fact]
    public void An_answer_that_stays_unreadable_is_unavailable_and_the_clipboard_still_comes_back()
    {
        var (platform, capture) = Build(p => p.PutOnClipboard("the user's own"));
        platform.ReadBusyTimes = Timing.BusyReadDelays.Count + 1;

        var result = capture.Capture();

        Assert.Equal(CaptureOutcome.ClipboardUnavailable, result.Outcome);
        Assert.Equal(CaptureRestore.Restored, result.Restore);
        Assert.Equal("the user's own", platform.CurrentClipboard);
        Assert.Equal(1 + Timing.BusyReadDelays.Count, platform.Events.Count(e => e == "read"));
    }

    [Fact]
    public void The_busy_read_schedule_is_short()
    {
        var delays = CaptureTiming.Default.BusyReadDelays;

        Assert.NotEmpty(delays);
        Assert.True(delays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d) <= TimeSpan.FromMilliseconds(500));
    }
}
