using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class BarRefreshPolicyTests
{
    // --- showing and hiding -------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Showing_reads_the_world_as_it_is_now_from_the_newest_entry(bool lightweightWhenHidden)
    {
        // 用户实录 2026-10-04：打开窄条有时停在最底部。轻量模式关着时卡片
        // 不清、偏移也不清，所以回到最新得是唤出命令自己的一部分。
        var policy = new BarRefreshPolicy(lightweightWhenHidden);

        Assert.Equal(BarRefreshCommand.Reload | BarRefreshCommand.ScrollToNewest, policy.Shown());
        Assert.True(policy.IsVisible);
    }

    [Fact]
    public void Showing_again_starts_at_the_newest_however_the_last_session_ended()
    {
        var policy = new BarRefreshPolicy(lightweightWhenHidden: false);
        policy.Shown();
        policy.Activated();
        policy.Hidden(BarHideReason.Toggled);

        Assert.Equal(BarRefreshCommand.Reload | BarRefreshCommand.ScrollToNewest, policy.Shown());
    }

    [Theory]
    [InlineData(BarHideReason.Toggled)]
    [InlineData(BarHideReason.Pasted)]
    public void Hiding_runs_the_full_teardown_whatever_the_reason(BarHideReason reason)
    {
        // 票 14 的教训变成受测不变式：粘贴收起与热键收起同待遇，没有捷径。
        var policy = new BarRefreshPolicy(lightweightWhenHidden: true);
        policy.Shown();

        Assert.Equal(BarRefreshCommand.EnterLightweight, policy.Hidden(reason));
        Assert.False(policy.IsVisible);
    }

    [Fact]
    public void Hiding_without_the_lightweight_setting_hides_only()
    {
        var policy = new BarRefreshPolicy(lightweightWhenHidden: false);
        policy.Shown();

        Assert.Equal(BarRefreshCommand.None, policy.Hidden(BarHideReason.Toggled));
    }

    [Fact]
    public void Hiding_an_already_hidden_bar_does_nothing()
    {
        var policy = new BarRefreshPolicy(lightweightWhenHidden: true);
        policy.Shown();
        policy.Hidden(BarHideReason.Toggled);

        Assert.Equal(BarRefreshCommand.None, policy.Hidden(BarHideReason.Pasted));
    }

    [Fact]
    public void The_lightweight_setting_applies_from_the_next_hide()
    {
        var policy = new BarRefreshPolicy(lightweightWhenHidden: true);
        policy.Shown();
        policy.Hidden(BarHideReason.Toggled);

        policy.ApplySettings(lightweightWhenHidden: false);
        policy.Shown();
        Assert.Equal(BarRefreshCommand.None, policy.Hidden(BarHideReason.Toggled));

        policy.ApplySettings(lightweightWhenHidden: true);
        policy.Shown();
        Assert.Equal(BarRefreshCommand.EnterLightweight, policy.Hidden(BarHideReason.Toggled));
    }

    // --- store changes ---------------------------------------------------------------

    [Fact]
    public void A_change_while_the_user_is_in_the_bar_reloads_in_place()
    {
        var policy = new BarRefreshPolicy(lightweightWhenHidden: true);
        policy.Shown();
        policy.Activated();

        // Browsing deep in the list: a background write must not yank the
        // user away from what they scrolled to.
        Assert.Equal(BarRefreshCommand.Reload, policy.StoreChanged(selfWrite: false));
    }

    [Fact]
    public void A_change_while_the_bar_sits_open_unused_brings_the_newest_into_view()
    {
        var policy = new BarRefreshPolicy(lightweightWhenHidden: true);
        policy.Shown();
        policy.Activated();
        policy.Deactivated();

        // 用户实录 2026-10-04 的成因：条开着、人在别的窗口复制，重读后列表
        // 仍停在翻到过的底部——下一眼看到的是最旧的记录，不是刚复制的那条。
        Assert.Equal(
            BarRefreshCommand.Reload | BarRefreshCommand.ScrollToNewest,
            policy.StoreChanged(selfWrite: false));
    }

    [Fact]
    public void A_bar_that_was_never_brought_to_the_front_counts_as_unused()
    {
        // Activation can be refused (foreground lock): a bar that never got
        // the keyboard is not one the user is browsing.
        var policy = new BarRefreshPolicy(lightweightWhenHidden: true);
        policy.Shown();

        Assert.Equal(
            BarRefreshCommand.Reload | BarRefreshCommand.ScrollToNewest,
            policy.StoreChanged(selfWrite: false));
    }

    [Fact]
    public void Being_in_use_follows_the_windows_focus_notifications_alone()
    {
        // WPF notifies only when IsActive really changes: a bar still holding
        // the focus through a hide comes back without a fresh Activated. A
        // hide that cleared the state would leave the policy believing the
        // user is elsewhere while they type in the bar (probe bar-newest).
        var policy = new BarRefreshPolicy(lightweightWhenHidden: false);
        policy.Shown();
        policy.Activated();
        policy.Hidden(BarHideReason.Pasted);
        policy.Shown();

        Assert.Equal(BarRefreshCommand.Reload, policy.StoreChanged(selfWrite: false));

        policy.Deactivated();
        Assert.Equal(
            BarRefreshCommand.Reload | BarRefreshCommand.ScrollToNewest,
            policy.StoreChanged(selfWrite: false));
    }

    [Fact]
    public void A_change_while_hidden_is_ignored()
    {
        var policy = new BarRefreshPolicy(lightweightWhenHidden: true);
        policy.Shown();
        policy.Hidden(BarHideReason.Toggled);

        // Whatever changed while hidden is read in one go by the next Shown.
        Assert.Equal(BarRefreshCommand.None, policy.StoreChanged(selfWrite: false));
    }

    [Fact]
    public void The_windows_own_change_is_ignored()
    {
        var policy = new BarRefreshPolicy(lightweightWhenHidden: true);
        policy.Shown();

        // Favourite, note and pin already produced their visual in place; a
        // reload here would only rebuild the list under the user (O-37).
        Assert.Equal(BarRefreshCommand.None, policy.StoreChanged(selfWrite: true));
    }

    [Fact]
    public void Every_external_change_means_exactly_one_reload_no_more()
    {
        var policy = new BarRefreshPolicy(lightweightWhenHidden: true);
        policy.Shown();
        policy.Activated();

        // A burst of Changed events (a copy plus its Touch) reloads once per
        // event — never twice for one event, never deferred and replayed in a
        // batch after hiding.
        Assert.Equal(BarRefreshCommand.Reload, policy.StoreChanged(selfWrite: false));
        Assert.Equal(BarRefreshCommand.Reload, policy.StoreChanged(selfWrite: false));

        policy.Hidden(BarHideReason.Toggled);
        Assert.Equal(BarRefreshCommand.None, policy.StoreChanged(selfWrite: false));

        // The changes ignored while hidden surface as the Shown reload, not
        // as replayed reloads.
        Assert.Equal(BarRefreshCommand.Reload | BarRefreshCommand.ScrollToNewest, policy.Shown());
    }
}
