using Shiyu.Core;
using Shiyu.Core.Tests.Fakes;

namespace Shiyu.Core.Tests;

public class SelectionDebtTests
{
    private static DeferredCapture Debt(string text = "选中文字", string borrowed = "用户原文")
        => new(text, CaptureOutcome.Captured, TextBackup(borrowed), SequenceBefore: 0, SequenceSealed: 1);

    /// <summary>只有文字的一份快照（用户剪贴板里是一段普通文字时的样子）。</summary>
    private static ClipboardBackup TextBackup(string text)
        => new(text, null, null, [], Excluded: false, LostFormats: [], Empty: false);

    // --- the fade path -----------------------------------------------------------

    [Fact]
    public void A_badge_that_fades_without_a_click_pays_back_once()
    {
        var ledger = new SelectionDebt();

        Assert.Empty(ledger.Offer(Debt()).Restore); // borrowed, held — nothing due yet
        var faded = ledger.Dismissed();
        Assert.Equal(["用户原文"], faded.Restore.Select(d => d.Borrowed));

        // Second fade event (or any later settle) is a no-op: paid exactly once.
        Assert.Empty(ledger.Dismissed().Restore);
        Assert.Empty(ledger.Exit().Restore);
    }

    [Fact]
    public void A_copy_badge_settles_an_older_selection_debts_badge()
    {
        // The copy path offers a badge with no debt of its own; taking over
        // still clears whatever the selection badge was holding.
        var ledger = new SelectionDebt();
        ledger.Offer(Debt());

        var settled = ledger.Dismissed(); // the copy path's takeover settle
        Assert.Single(settled.Restore);
    }

    // --- the offer-supersedes path -------------------------------------------------

    [Fact]
    public void A_new_offer_settles_the_old_debt_first()
    {
        var ledger = new SelectionDebt();
        ledger.Offer(Debt(text: "第一段", borrowed: "原文一"));

        // A second quick drag settles the first borrow before taking its own.
        var superseded = ledger.Offer(Debt(text: "第二段", borrowed: "第一段的影子"));
        Assert.Equal(["原文一"], superseded.Restore.Select(d => d.Borrowed));

        // And the second debt is still owed.
        Assert.Equal(["第一段的影子"], ledger.Dismissed().Restore.Select(d => d.Borrowed));
    }

    // --- the click-and-panel path ----------------------------------------------------

    [Fact]
    public void Clicking_the_badge_defers_the_restore_until_the_panel_is_up()
    {
        var ledger = new SelectionDebt();
        ledger.Offer(Debt());

        // The click hands the debt to the panel; nothing is restored yet.
        Assert.Empty(ledger.Accepted().Restore);

        // Panel up (or known-dead — the App calls this on both paths): pay now.
        var displayed = ledger.PanelDisplayed();
        Assert.Equal(["用户原文"], displayed.Restore.Select(d => d.Borrowed));

        // A late fade event after the click settles nothing.
        Assert.Empty(ledger.Dismissed().Restore);
    }

    [Fact]
    public void A_panel_that_never_comes_up_still_gets_its_debt_settled()
    {
        // The App invokes the displayed callback on the failure path too —
        // a restore left hanging is a borrow never paid back.
        var ledger = new SelectionDebt();
        ledger.Offer(Debt());
        ledger.Accepted();

        Assert.Single(ledger.PanelDisplayed().Restore);
    }

    [Fact]
    public void Two_accepted_debts_settle_in_panel_order()
    {
        var ledger = new SelectionDebt();
        ledger.Offer(Debt(text: "一", borrowed: "原文一"));
        ledger.Accepted();
        ledger.Offer(Debt(text: "二", borrowed: "原文二"));
        ledger.Accepted();

        Assert.Equal(["原文一"], ledger.PanelDisplayed().Restore.Select(d => d.Borrowed));
        Assert.Equal(["原文二"], ledger.PanelDisplayed().Restore.Select(d => d.Borrowed));
        Assert.Empty(ledger.PanelDisplayed().Restore);
    }

    // --- exit ---------------------------------------------------------------------

    [Fact]
    public void Exit_with_nothing_held_does_nothing()
    {
        var ledger = new SelectionDebt();
        Assert.Empty(ledger.Exit().Restore);
    }

    [Fact]
    public void Exit_after_everything_was_already_settled_does_nothing()
    {
        var ledger = new SelectionDebt();
        ledger.Offer(Debt());
        ledger.Dismissed();

        Assert.Empty(ledger.Exit().Restore);
    }

    [Fact]
    public void Exit_pays_the_newest_debt_first_so_the_oldest_original_lands_last()
    {
        var ledger = new SelectionDebt();
        ledger.Offer(Debt(text: "一", borrowed: "用户原文")); // oldest — the user's own
        ledger.Accepted(); // now with the opening panel
        ledger.Offer(Debt(text: "二", borrowed: "一选中")); // newer, held by the badge

        var settled = ledger.Exit().Restore.Select(d => d.Borrowed).ToArray();
        Assert.Equal(["一选中", "用户原文"], settled);

        // Everything is closed out: a later exit has nothing left to pay.
        Assert.Empty(ledger.Exit().Restore);
    }

    // --- end to end, through the real capture and a fake clipboard ------------------

    private static (FakeCapturePlatform Platform, SelectionCapture Capture) Platform(string existing)
    {
        var platform = new FakeCapturePlatform
        {
            AnswersAfterPolls = 1,
            Answer = "选中文字",
        };
        platform.PutOnClipboard(existing);
        return (platform, new SelectionCapture(platform, CaptureTiming.Default));
    }

    [Fact]
    public void Fade_path_end_to_end_puts_the_users_clipboard_back()
    {
        var (platform, capture) = Platform("用户原文");
        var ledger = new SelectionDebt();

        var debt = capture.CaptureDeferRestore();
        Assert.NotNull(debt);
        ledger.Offer(debt!);
        Assert.Equal("选中文字", platform.CurrentClipboard); // still held

        foreach (var due in ledger.Dismissed().Restore)
        {
            capture.Restore(due);
        }

        Assert.Equal("用户原文", platform.CurrentClipboard);
    }

    [Fact]
    public void Panel_path_end_to_end_restores_only_after_the_panel_is_up()
    {
        var (platform, capture) = Platform("用户原文");
        var ledger = new SelectionDebt();

        ledger.Offer(capture.CaptureDeferRestore()!);
        ledger.Accepted();
        Assert.Equal("选中文字", platform.CurrentClipboard); // deferred past the click

        foreach (var due in ledger.PanelDisplayed().Restore)
        {
            capture.Restore(due);
        }

        Assert.Equal("用户原文", platform.CurrentClipboard);
    }

    [Fact]
    public void Exit_end_to_end_leaves_the_users_original_on_the_clipboard()
    {
        var (platform, capture) = Platform("用户原文");
        var ledger = new SelectionDebt();

        // First borrow for real; the second (arrived before the panel came
        // up) is modeled by hand: it borrowed the artifact the first capture
        // left and put its own selection there in Shiyu's name.
        var oldest = capture.CaptureDeferRestore()!;
        ledger.Offer(oldest);
        ledger.Accepted();
        platform.WriteClipboardText("第二段选中");
        var sealedAt = platform.ClipboardSequenceNumber();
        ledger.Offer(new DeferredCapture(
            "第二段选中", CaptureOutcome.Captured, TextBackup("选中文字"), sealedAt - 1, sealedAt));

        foreach (var due in ledger.Exit().Restore)
        {
            capture.Restore(due);
        }

        // The newest restore pays back the artifact it borrowed; the oldest —
        // the user's own — lands last and stays.
        Assert.Equal("用户原文", platform.CurrentClipboard);
    }
}
