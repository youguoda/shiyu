using Shiyu.Core;
using Shiyu.Core.Tests.Fakes;

namespace Shiyu.Core.Tests;

/// <summary>
/// Every test here asserts the same invariant alongside whatever else it is
/// checking: the clipboard the user had is the clipboard the user gets back.
/// That is the one failure a capture tool cannot be forgiven for.
/// </summary>
public class SelectionCaptureTests
{
    private static readonly CaptureTiming Timing =
        new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));

    private static (FakeCapturePlatform Platform, SelectionCapture Capture) Build(string? existingClipboard)
    {
        var platform = new FakeCapturePlatform();
        platform.PutOnClipboard(existingClipboard);
        return (platform, new SelectionCapture(platform, Timing));
    }

    [Fact]
    public void A_prompt_application_has_its_selection_captured()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = 1;
        platform.Answer = "the selected text";

        var result = capture.Capture();

        Assert.True(result.Succeeded);
        Assert.Equal("the selected text", result.Text);
        Assert.True(result.ClipboardRestored);
        Assert.Equal("the user's own clipboard", platform.CurrentClipboard);
    }

    [Fact]
    public void A_slow_application_still_gets_its_answer_in_before_the_deadline()
    {
        var (platform, capture) = Build("the user's own clipboard");

        // Nine polls out of a possible ten: only just in time.
        platform.AnswersAfterPolls = 9;

        var result = capture.Capture();

        Assert.True(result.Succeeded);
        Assert.Equal("the selected text", result.Text);
        Assert.Equal("the user's own clipboard", platform.CurrentClipboard);
    }

    [Fact]
    public void An_application_that_never_answers_times_out_and_gives_the_clipboard_back()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = null;

        var result = capture.Capture();

        Assert.False(result.Succeeded);
        Assert.Equal(CaptureOutcome.NothingCaptured, result.Outcome);
        Assert.Null(result.Text);
        Assert.True(result.ClipboardRestored);
        Assert.Equal("the user's own clipboard", platform.CurrentClipboard);
    }

    [Fact]
    public void Capture_gives_up_rather_than_polling_forever()
    {
        var (platform, capture) = Build("anything");
        platform.AnswersAfterPolls = null;

        capture.Capture();

        // 100 ms of deadline at 10 ms a poll. A loop that ran away would show
        // up here long before it hung a real machine.
        Assert.True(platform.Polls <= 12, $"polled {platform.Polls} times");
    }

    [Fact]
    public void With_nothing_selected_it_reports_nothing_rather_than_the_previous_clipboard()
    {
        var (platform, capture) = Build("something copied earlier");

        // Pressing copy with no selection changes nothing at all.
        platform.AnswersAfterPolls = null;

        var result = capture.Capture();

        Assert.Equal(CaptureOutcome.NothingCaptured, result.Outcome);
        Assert.Null(result.Text);
        Assert.NotEqual("something copied earlier", result.Text);
    }

    [Fact]
    public void A_clipboard_that_changes_but_holds_nothing_usable_is_not_treated_as_a_capture()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = 2;
        platform.Answer = "";

        var result = capture.Capture();

        Assert.Equal(CaptureOutcome.NothingCaptured, result.Outcome);
        Assert.Null(result.Text);
        Assert.Equal("the user's own clipboard", platform.CurrentClipboard);
    }

    [Fact]
    public void An_empty_clipboard_before_capture_is_restored_as_empty()
    {
        var (platform, capture) = Build(null);
        platform.AnswersAfterPolls = 1;

        var result = capture.Capture();

        Assert.True(result.Succeeded);

        // Restoring must put back "nothing", not leave the captured text
        // sitting there as though the user had copied it.
        Assert.Null(platform.CurrentClipboard);
        Assert.Contains(null, platform.Writes);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_read_fails_without_pressing_anything()
    {
        var platform = new FakeCapturePlatform { ReadFails = true };
        var capture = new SelectionCapture(platform, Timing);

        var result = capture.Capture();

        Assert.Equal(CaptureOutcome.ClipboardUnavailable, result.Outcome);
        Assert.True(result.ClipboardRestored);

        // Nothing was borrowed, so no keystroke should have been sent into the
        // user's application on a false premise.
        Assert.Equal(0, platform.CopyKeystrokes);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_written_back_reports_the_loss_rather_than_hiding_it()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = 1;
        platform.WriteFails = true;

        var result = capture.Capture();

        Assert.True(result.Succeeded);
        Assert.False(result.ClipboardRestored);
    }

    [Fact]
    public void A_clipboard_that_throws_while_restoring_still_reports_the_loss()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = 1;

        var result = capture.Capture();
        Assert.True(result.ClipboardRestored);

        var (throwing, throwingCapture) = Build("the user's own clipboard");
        throwing.AnswersAfterPolls = 1;
        throwing.WriteThrows = true;

        var second = throwingCapture.Capture();
        Assert.False(second.ClipboardRestored);
    }

    [Fact]
    public void The_copy_keystroke_is_sent_exactly_once()
    {
        var (platform, capture) = Build("anything");
        platform.AnswersAfterPolls = 3;

        capture.Capture();

        Assert.Equal(1, platform.CopyKeystrokes);
    }

    [Fact]
    public void Pasting_puts_the_text_on_the_clipboard_and_then_presses_paste()
    {
        var (platform, capture) = Build("anything");

        Assert.True(capture.Paste("text to paste"));

        Assert.Equal("text to paste", platform.CurrentClipboard);
        Assert.Equal(1, platform.PasteKeystrokes);
    }

    [Fact]
    public void Pasting_does_not_press_anything_when_the_clipboard_refused_the_text()
    {
        var (platform, capture) = Build("anything");
        platform.WriteFails = true;

        Assert.False(capture.Paste("text to paste"));

        // Pressing paste after a failed write would paste whatever happened to
        // be on the clipboard instead — the wrong text, silently.
        Assert.Equal(0, platform.PasteKeystrokes);
    }

    // --- 划词路径（票 37）：借出 → Ctrl+C → 等答案，但还原推迟 ---
    //
    // 还原时序采纳 Glossy 的次序细节：剪贴板还原放在翻译面板显示之后。
    // 还原可以等——取到的文字已经拿在手里，而点击到出面板这一段不该
    // 再添一次剪贴板写。

    [Fact]
    public void A_deferred_capture_leaves_the_selection_on_the_clipboard()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = 1;
        platform.Answer = "the selected text";

        var deferred = capture.CaptureDeferRestore();

        Assert.NotNull(deferred);
        Assert.True(deferred.Succeeded);
        Assert.Equal("the selected text", deferred.Text);
        Assert.Equal("the user's own clipboard", deferred.Borrowed);

        // 剪贴板此刻攥着的是选中文字，不是用户原有的内容——还原被推迟了。
        Assert.Equal("the selected text", platform.CurrentClipboard);
    }

    [Fact]
    public void The_deferred_capture_rewrites_the_selection_in_shiyus_own_name()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = 1;
        platform.Answer = "the selected text";

        capture.CaptureDeferRestore();

        // 取到的文字被立即以拾语名义写回：剪贴板的 owner 从此是拾语，
        // "目标应用完成复制"的迟到通知在还原之前抵达也会被自我抑制挡下
        // （监控按处理时刻的 owner 判定）。这一次写是唯一的一次。
        Assert.Equal(["the selected text"], platform.Writes);
    }

    [Fact]
    public void Restoring_a_deferred_capture_puts_the_users_clipboard_back()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = 1;

        var deferred = capture.CaptureDeferRestore();
        Assert.NotNull(deferred);
        Assert.True(capture.Restore(deferred));

        Assert.Equal("the user's own clipboard", platform.CurrentClipboard);
    }

    [Fact]
    public void A_deferred_capture_that_captures_nothing_still_gives_the_clipboard_back()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = null;

        var deferred = capture.CaptureDeferRestore();
        Assert.NotNull(deferred);
        Assert.Equal(CaptureOutcome.NothingCaptured, deferred.Outcome);

        // 没取到文字就不会有徽标，也没有"以拾语名义写回"——剪贴板原样未动。
        Assert.Empty(platform.Writes);
        Assert.Equal("the user's own clipboard", platform.CurrentClipboard);

        Assert.True(capture.Restore(deferred));
        Assert.Equal("the user's own clipboard", platform.CurrentClipboard);
    }

    [Fact]
    public void A_deferred_capture_on_an_unreadable_clipboard_borrows_nothing()
    {
        var platform = new FakeCapturePlatform { ReadFails = true };
        var capture = new SelectionCapture(platform, Timing);

        Assert.Null(capture.CaptureDeferRestore());

        // 借都没借到：没有按键发进别人的窗口，也没有需要还原的东西。
        Assert.Equal(0, platform.CopyKeystrokes);
        Assert.Empty(platform.Writes);
    }

    [Fact]
    public void A_failing_rewrite_does_not_fail_the_deferred_capture()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = 1;
        platform.WriteThrows = true;

        var deferred = capture.CaptureDeferRestore();

        // 名义写回失败只是少了自我抑制的保险，取词本身成立。
        Assert.NotNull(deferred);
        Assert.True(deferred.Succeeded);

        // 还原同样撞上坏写——如实报告失败，这正是调用方要转告用户的。
        Assert.False(capture.Restore(deferred));
    }

    [Fact]
    public void An_empty_clipboard_is_restored_as_empty_by_the_deferred_path()
    {
        var (platform, capture) = Build(null);
        platform.AnswersAfterPolls = 1;

        var deferred = capture.CaptureDeferRestore();
        Assert.NotNull(deferred);
        Assert.Null(deferred.Borrowed);

        capture.Restore(deferred);

        // 还的是"空"，不是留下选中文字冒充用户复制过它。
        Assert.Null(platform.CurrentClipboard);
    }

    [Fact]
    public void A_user_copy_made_while_the_debt_was_out_voids_the_restore()
    {
        var (platform, capture) = Build("the user's own clipboard");
        platform.AnswersAfterPolls = 1;

        var deferred = capture.CaptureDeferRestore();
        Assert.NotNull(deferred);

        // 徽标还挂着的时候用户自己复制了新东西——剪贴板已经往前走了。
        platform.PutOnClipboard("something newer");

        Assert.True(capture.Restore(deferred));

        // 还原被跳过：踩掉用户更新的复制比不还旧债更糟。
        Assert.Equal("something newer", platform.CurrentClipboard);
        Assert.Equal(["the selected text"], platform.Writes);
    }
}

/// <summary>
/// 手指离开修饰键再发键（用户实录 2026-10-10：用过划词翻译以后，Ctrl 快捷键有时不灵）。热键在按下
/// 的那一刻触发，手指还在 Ctrl+Shift 上——这时模拟的 Ctrl+C 要么和它们缠在一起，要么得替用户
/// 松开，而替他松开的 Ctrl 在 Windows 眼里就"没了"。等他松开再发，是一次干净的 Ctrl+C。
/// </summary>
public class CaptureWaitsForModifierReleaseTests
{
    private static readonly CaptureTiming Timing =
        new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100))
        {
            ModifierRelease = TimeSpan.FromMilliseconds(50),
        };

    private static (FakeCapturePlatform Platform, SelectionCapture Capture) Build(int heldForChecks)
    {
        var platform = new FakeCapturePlatform { ModifiersHeldForChecks = heldForChecks };
        platform.PutOnClipboard("the user's own clipboard");
        return (platform, new SelectionCapture(platform, Timing));
    }

    [Fact]
    public void The_copy_waits_until_the_hotkeys_modifiers_are_let_go()
    {
        var (platform, capture) = Build(heldForChecks: 3);
        platform.AnswersAfterPolls = 4;

        var result = capture.Capture();

        // 三回还按着、第四回松开了：等三回，再借剪贴板、再发键。
        Assert.Equal(["wait", "wait", "wait", "read", "copy"], platform.Events.Take(5));
        Assert.True(result.Succeeded);
        Assert.Equal("the user's own clipboard", platform.CurrentClipboard);
    }

    [Fact]
    public void Nothing_held_means_no_wait_at_all()
    {
        var (platform, capture) = Build(heldForChecks: 0);
        platform.AnswersAfterPolls = 1;

        capture.Capture();

        Assert.Equal(0, platform.WaitsBeforeFirstKeystroke);
    }

    [Fact]
    public void Fingers_that_stay_down_hold_the_copy_back_only_until_the_deadline()
    {
        var (platform, capture) = Build(heldForChecks: int.MaxValue);

        capture.Capture();

        // 50 毫秒的上限、10 毫秒一问：等五回，照发。
        Assert.Equal(5, platform.WaitsBeforeFirstKeystroke);
        Assert.Equal(1, platform.CopyKeystrokes);
    }

    [Fact]
    public void The_drag_badge_path_waits_the_same_way()
    {
        var (platform, capture) = Build(heldForChecks: 2);
        platform.AnswersAfterPolls = 3;

        var deferred = capture.CaptureDeferRestore();

        Assert.Equal(2, platform.WaitsBeforeFirstKeystroke);
        Assert.True(deferred!.Succeeded);
    }

    [Fact]
    public void A_paste_writes_and_sends_only_after_the_modifiers_are_let_go()
    {
        // 窄条里 Ctrl+Enter 粘贴为纯文本：手指还在 Ctrl 上。
        var (platform, capture) = Build(heldForChecks: 2);

        Assert.True(capture.Paste("译文"));

        Assert.Equal(["wait", "wait", "write", "paste"], platform.Events);
    }

    [Fact]
    public void Pasting_what_is_already_on_the_clipboard_waits_too()
    {
        var (platform, capture) = Build(heldForChecks: 2);

        Assert.True(capture.PasteCurrentClipboard());

        Assert.Equal(["wait", "wait", "paste"], platform.Events);
    }
}

/// <summary>模拟一次 Ctrl+键该发哪些按键：只看此刻哪些修饰键被按着（平台照着发）。</summary>
public class ControlKeystrokeTests
{
    private const ushort C = 0x43;

    private static string Played(params ushort[] held)
        => string.Join(" ", ControlKeystroke.For(C, key => held.Contains(key))
            .Select(stroke => Name(stroke.Key) + (stroke.Up ? "↑" : "↓")));

    private static string Name(ushort key) => key switch
    {
        ControlKeystroke.Control => "Ctrl",
        ControlKeystroke.Shift => "Shift",
        ControlKeystroke.Alt => "Alt",
        ControlKeystroke.LeftWin => "LWin",
        ControlKeystroke.RightWin => "RWin",
        C => "C",
        _ => key.ToString(),
    };

    [Fact]
    public void With_nothing_held_it_is_a_clean_ctrl_press_and_release()
        => Assert.Equal("Ctrl↓ C↓ C↑ Ctrl↑", Played());

    [Fact]
    public void A_ctrl_the_user_still_holds_is_borrowed_and_never_released()
    {
        // 替他松开，他还按着的 Ctrl 在 Windows 眼里就没了——之后的 Ctrl+S 只打出一个 s。
        Assert.Equal("C↓ C↑", Played(ControlKeystroke.Control));
    }

    [Fact]
    public void A_held_shift_is_let_go_so_the_app_sees_ctrl_c_not_ctrl_shift_c()
        => Assert.Equal("Shift↑ C↓ C↑", Played(ControlKeystroke.Control, ControlKeystroke.Shift));

    [Fact]
    public void Alt_and_win_are_let_go_too_and_ctrl_is_pressed_for_them()
        => Assert.Equal(
            "Alt↑ LWin↑ Ctrl↓ C↓ C↑ Ctrl↑",
            Played(ControlKeystroke.Alt, ControlKeystroke.LeftWin));

    [Fact]
    public void Any_modifier_down_counts_as_held()
    {
        Assert.False(ControlKeystroke.AnyModifierDown(_ => false));
        Assert.True(ControlKeystroke.AnyModifierDown(key => key == ControlKeystroke.RightWin));
        Assert.True(ControlKeystroke.AnyModifierDown(key => key == ControlKeystroke.Control));
    }
}
