using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class PreviewPolicyTests
{
    private const int HoverDelay = 500;

    private sealed class Clock
    {
        public long Now;

        public long Get() => Now;

        public void Advance(long milliseconds) => Now += milliseconds;
    }

    private static (PreviewPolicy Policy, Clock Time) Make()
    {
        var time = new Clock();
        return (new PreviewPolicy(HoverDelay, time.Get), time);
    }

    // --- space ----------------------------------------------------------------

    [Fact]
    public void Holding_space_opens_immediately_and_releasing_closes()
    {
        var (policy, _) = Make();

        Assert.Equal(PreviewCommand.Open, policy.SpaceDown(7));
        Assert.True(policy.IsOpen);
        Assert.Equal(7, policy.Card);

        Assert.Equal(PreviewCommand.Close, policy.SpaceUp());
        Assert.False(policy.IsOpen);
    }

    [Fact]
    public void Releasing_space_leaves_a_hover_preview_standing()
    {
        var (policy, time) = Make();

        policy.SpaceDown(1);
        time.Advance(1000);
        policy.HoverEnter(1); // The pointer drifts onto the held card.
        Assert.Equal(PreviewCommand.None, policy.SpaceUp());

        // The hover buffer owns it now: leaving then re-entering retargets
        // rather than having closed it.
        policy.HoverLeave();
        time.Advance(100);
        Assert.Equal(PreviewCommand.Retarget, policy.HoverEnter(2));
    }

    [Fact]
    public void A_held_space_repeating_never_reopens_the_card_already_showing()
    {
        // 按住空格，键盘每 30 毫秒自动重复一次 KeyDown；每一次都"重新打开"，图片就在缩略图与
        // 原图之间来回闪（用户实录 2026-10-09：预览图片会抖动）。
        var (policy, _) = Make();

        Assert.Equal(PreviewCommand.Open, policy.SpaceDown(7));
        Assert.Equal(PreviewCommand.None, policy.SpaceDown(7));
        Assert.Equal(PreviewCommand.None, policy.SpaceDown(7));
        Assert.True(policy.IsOpen);
        Assert.Equal(7, policy.Card);

        Assert.Equal(PreviewCommand.Close, policy.SpaceUp());
    }

    [Fact]
    public void Space_over_a_hover_preview_of_that_card_takes_it_over_without_reopening()
    {
        var (policy, time) = Make();
        policy.HoverEnter(3);
        time.Advance(HoverDelay);
        Assert.Equal(PreviewCommand.Open, policy.Tick());

        Assert.Equal(PreviewCommand.None, policy.SpaceDown(3));

        // The key owns it now: releasing it closes what it holds.
        Assert.Equal(PreviewCommand.Close, policy.SpaceUp());
    }

    [Fact]
    public void Space_on_another_card_still_opens_that_card()
    {
        var (policy, _) = Make();
        policy.SpaceDown(1);

        Assert.Equal(PreviewCommand.Open, policy.SpaceDown(2));
        Assert.Equal(2, policy.Card);
    }

    // --- hover dwell ------------------------------------------------------------

    [Fact]
    public void Hover_opens_only_after_the_configured_dwell()
    {
        var (policy, time) = Make();

        Assert.Equal(PreviewCommand.None, policy.HoverEnter(1));
        time.Advance(HoverDelay - 1);
        Assert.Equal(PreviewCommand.None, policy.Tick());

        time.Advance(1);
        Assert.Equal(PreviewCommand.Open, policy.Tick());
        Assert.Equal(1, policy.Card);
    }    [Fact]
    public void A_pointer_that_passes_through_opens_nothing()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        time.Advance(50);
        Assert.Equal(PreviewCommand.None, policy.HoverLeave());

        time.Advance(HoverDelay);
        Assert.Equal(PreviewCommand.None, policy.Tick());
        Assert.False(policy.IsOpen);
    }

    [Fact]
    public void Zero_delay_disables_hover_entirely()
    {
        var time = new Clock();
        var policy = new PreviewPolicy(0, time.Get);

        Assert.Equal(PreviewCommand.None, policy.HoverEnter(1));
        time.Advance(10_000);

        Assert.Equal(PreviewCommand.None, policy.Tick());
    }

    // --- the grace buffer ---------------------------------------------------------

    [Fact]
    public void Crossing_to_a_neighbouring_card_retargets_instead_of_flickering()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        time.Advance(HoverDelay);
        policy.Tick();

        policy.HoverLeave();
        time.Advance(PreviewPolicy.HoverBufferMs - 10);
        Assert.Equal(PreviewCommand.Retarget, policy.HoverEnter(2));
        Assert.True(policy.IsOpen);
        Assert.Equal(2, policy.Card);
    }

    [Fact]
    public void Leaving_everything_behind_closes_after_the_buffer()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        time.Advance(HoverDelay);
        policy.Tick();

        policy.HoverLeave();
        time.Advance(PreviewPolicy.HoverBufferMs - 1);
        Assert.Equal(PreviewCommand.None, policy.Tick());

        time.Advance(1);
        Assert.Equal(PreviewCommand.Close, policy.Tick());
        Assert.False(policy.IsOpen);
    }

    [Fact]
    public void Coming_back_to_the_same_card_mid_buffer_just_stays_open()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        time.Advance(HoverDelay);
        policy.Tick();

        policy.HoverLeave();
        time.Advance(100);
        Assert.Equal(PreviewCommand.None, policy.HoverEnter(1));
        Assert.True(policy.IsOpen);
    }

    [Fact]
    public void The_pointer_resting_on_the_preview_keeps_it_alive()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        time.Advance(HoverDelay);
        policy.Tick();

        policy.HoverLeave();
        policy.PreviewEntered();

        time.Advance(10_000);
        Assert.Equal(PreviewCommand.None, policy.Tick());
        Assert.True(policy.IsOpen);

        // Off the panel and away: the buffer restarts, then closes.
        policy.PreviewLeft();
        time.Advance(PreviewPolicy.HoverBufferMs);
        Assert.Equal(PreviewCommand.Close, policy.Tick());
    }

    [Fact]
    public void Coming_back_from_the_panel_to_its_own_card_reloads_nothing()
    {
        var (policy, time) = Make();
        policy.HoverEnter(1);
        time.Advance(HoverDelay);
        policy.Tick();

        policy.HoverLeave();
        policy.PreviewEntered();
        Assert.Equal(PreviewCommand.None, policy.HoverEnter(1));
        Assert.True(policy.IsOpen);

        // A neighbour reached the same way still glides across.
        policy.HoverLeave();
        policy.PreviewEntered();
        Assert.Equal(PreviewCommand.Retarget, policy.HoverEnter(2));
        Assert.Equal(2, policy.Card);
    }

    // --- keyboard following --------------------------------------------------------

    [Fact]
    public void The_keyboard_preview_waits_for_the_selection_to_settle()
    {
        var (policy, time) = Make();

        policy.SpaceDown(1);
        time.Advance(1000);
        policy.SelectionMoved(2);
        time.Advance(PreviewPolicy.KeyboardSettleMs - 1);
        Assert.Equal(PreviewCommand.None, policy.Tick());

        time.Advance(1);
        Assert.Equal(PreviewCommand.Retarget, policy.Tick());
        Assert.Equal(2, policy.Card);
    }

    [Fact]
    public void A_still_moving_selection_keeps_resetting_the_settle()
    {
        var (policy, time) = Make();

        policy.SpaceDown(1);
        time.Advance(1000);
        policy.SelectionMoved(2);
        time.Advance(100);
        policy.SelectionMoved(3);
        time.Advance(PreviewPolicy.KeyboardSettleMs - 1);
        Assert.Equal(PreviewCommand.None, policy.Tick());

        time.Advance(1);
        Assert.Equal(PreviewCommand.Retarget, policy.Tick());
        Assert.Equal(3, policy.Card);
    }

    [Fact]
    public void A_hover_preview_does_not_follow_the_keyboard_selection()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        time.Advance(HoverDelay);
        policy.Tick();

        policy.SelectionMoved(5);
        time.Advance(2000);

        Assert.Equal(PreviewCommand.None, policy.Tick());
        Assert.Equal(1, policy.Card);
    }

    // --- closing rules --------------------------------------------------------------

    [Fact]
    public void Scrolling_closes_a_hover_preview_but_not_a_held_one()
    {
        var (policy, time) = Make();
        policy.HoverEnter(1);
        time.Advance(HoverDelay);
        policy.Tick();
        Assert.Equal(PreviewCommand.Close, policy.Scrolled());

        var (held, heldTime) = Make();
        held.SpaceDown(1);
        heldTime.Advance(1000);
        Assert.Equal(PreviewCommand.None, held.Scrolled());
        Assert.True(held.IsOpen);
    }

    [Fact]
    public void Scrolling_cancels_a_pending_hover_before_it_ever_opens()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        time.Advance(50);
        Assert.Equal(PreviewCommand.None, policy.Scrolled());

        time.Advance(HoverDelay);
        Assert.Equal(PreviewCommand.None, policy.Tick());
        Assert.False(policy.IsOpen);
    }

    [Fact]
    public void Escape_and_a_hidden_bar_close_whatever_was_up()
    {
        var (policy, _) = Make();
        policy.SpaceDown(1);
        Assert.Equal(PreviewCommand.Close, policy.Escape());
        Assert.False(policy.IsOpen);

        policy.SpaceDown(2);
        Assert.Equal(PreviewCommand.Close, policy.BarHidden());
        Assert.False(policy.IsOpen);
    }

    // --- the on-demand deadline (O-37) ------------------------------------------

    // One single-shot timer replaces the old always-on 50 ms tick; these pin
    // the policy's answer to "how long until something actually happens?" to
    // the same instants Tick() would have acted on.

    [Fact]
    public void An_idle_bar_has_no_deadline_and_arms_no_timer()
    {
        var (policy, _) = Make();

        Assert.Null(policy.TimeUntilDecision());
    }

    [Fact]
    public void A_dwelling_hover_names_the_exact_moment_it_opens()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        time.Advance(100);

        var wait = policy.TimeUntilDecision();
        Assert.NotNull(wait);
        Assert.Equal(HoverDelay - 100, wait.Value.TotalMilliseconds);
    }

    [Fact]
    public void A_deadline_that_has_already_passed_reports_zero_not_negative()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        time.Advance(HoverDelay + 500);

        Assert.Equal(0, policy.TimeUntilDecision()?.TotalMilliseconds);
    }

    [Fact]
    public void A_crossed_card_buffers_and_names_the_buffers_expiry()
    {
        var (policy, time) = Make();

        policy.SpaceDown(1);
        policy.HoverEnter(1);
        time.Advance(200);
        policy.SpaceUp();

        // The panel is standing open under the pointer — no deadline of its
        // own — until the pointer leaves the card.
        Assert.Null(policy.TimeUntilDecision());

        policy.HoverLeave();
        time.Advance(100);

        var wait = policy.TimeUntilDecision();
        Assert.NotNull(wait);
        Assert.Equal(PreviewPolicy.HoverBufferMs - 100, wait.Value.TotalMilliseconds);
    }

    [Fact]
    public void A_moving_keyboard_selection_names_the_settle_deadline()
    {
        var (policy, time) = Make();

        policy.SpaceDown(1);
        policy.SelectionMoved(2);
        time.Advance(50);

        var wait = policy.TimeUntilDecision();
        Assert.NotNull(wait);
        Assert.Equal(PreviewPolicy.KeyboardSettleMs - 50, wait.Value.TotalMilliseconds);

        // The settle elapses, the panel follows, and nothing is pending
        // again — selection and panel agree.
        time.Advance(PreviewPolicy.KeyboardSettleMs - 50);
        Assert.Equal(PreviewCommand.Retarget, policy.Tick());
        Assert.Null(policy.TimeUntilDecision());
    }

    [Fact]
    public void A_closed_panel_arms_nothing()
    {
        var (policy, time) = Make();

        policy.HoverEnter(1);
        Assert.NotNull(policy.TimeUntilDecision());

        policy.Scrolled();
        Assert.Null(policy.TimeUntilDecision());
    }

    // --- the hover switch (悬停自动预览，用户需求 2026-10-05) ---------------------

    [Fact]
    public void With_hover_previews_off_resting_opens_nothing_but_space_still_does()
    {
        var time = new Clock();
        var policy = PreviewPolicy.For(new AppSettings { PreviewOnHover = false }, time.Get);

        Assert.False(policy.HoverEnabled);
        Assert.Equal(PreviewCommand.None, policy.HoverEnter(1));
        time.Advance(5000);
        Assert.Equal(PreviewCommand.None, policy.Tick());
        Assert.False(policy.IsOpen);

        // Nothing pending either: a bar with hover off asks for no timer.
        Assert.Null(policy.TimeUntilDecision());

        Assert.Equal(PreviewCommand.Open, policy.SpaceDown(1));
    }

    [Fact]
    public void With_hover_previews_on_the_dwell_is_the_settings_delay()
    {
        var time = new Clock();
        var policy = PreviewPolicy.For(
            new AppSettings { PreviewOnHover = true, PreviewHoverDelayMs = 300 }, time.Get);

        policy.HoverEnter(1);
        time.Advance(299);
        Assert.Equal(PreviewCommand.None, policy.Tick());
        time.Advance(1);
        Assert.Equal(PreviewCommand.Open, policy.Tick());
    }

    [Fact]
    public void A_switch_that_says_on_is_never_turned_off_by_a_zero_delay()
    {
        var policy = PreviewPolicy.For(
            new AppSettings { PreviewOnHover = true, PreviewHoverDelayMs = 0 }, () => 0);

        Assert.True(policy.HoverEnabled);
    }
}
