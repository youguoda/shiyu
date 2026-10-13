using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// A system key is being borrowed: the filter must swallow exactly Win+V and
/// its trailing V release, and let every other keystroke through untouched —
/// including the Win release itself, whose free passage is what keeps the
/// key from sticking system-wide (the Start menu is suppressed by an
/// injected mask keystroke instead, one layer up).
/// </summary>
public class WinVFilterTests
{
    private static KeyFlow Feed(WinVFilter filter, SpecialKey key, KeyDirection direction)
        => filter.Feed(key, direction);

    [Fact]
    public void Win_then_V_triggers_and_swallows_but_Win_release_passes()
    {
        var filter = new WinVFilter();
        var fired = 0;
        filter.Triggered += () => fired++;

        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(1, fired);

        // The V release is eaten (its down was eaten); the Win release PASSES
        // so the key never sticks — the Start menu is prevented by the mask
        // the hook injects, not by withholding the release.
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Up));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Up));
    }

    [Fact]
    public void Either_Win_works()
    {
        var filter = new WinVFilter();
        var fired = 0;
        filter.Triggered += () => fired++;

        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.RightWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Up));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.RightWin, KeyDirection.Up));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Plain_typing_passes_completely_even_after_a_takeover()
    {
        var filter = new WinVFilter();

        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.V, KeyDirection.Up));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.Other, KeyDirection.Down));

        // After one takeover, a later plain V press must not lose its release
        // to a stale takeover flag.
        Feed(filter, SpecialKey.LeftWin, KeyDirection.Down);
        Feed(filter, SpecialKey.V, KeyDirection.Down);
        Feed(filter, SpecialKey.V, KeyDirection.Up);
        Feed(filter, SpecialKey.LeftWin, KeyDirection.Up);

        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.V, KeyDirection.Up));
    }

    [Fact]
    public void Win_alone_and_other_win_combos_pass()
    {
        var filter = new WinVFilter();

        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.Other, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.Other, KeyDirection.Up));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Up));

        // Win+R, Win+E: identical shape to Win+V but a different key.
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.Other, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Up));
    }

    [Fact]
    public void Win_released_before_V_means_no_takeover()
    {
        var filter = new WinVFilter();
        var fired = 0;
        filter.Triggered += () => fired++;

        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Up));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.V, KeyDirection.Up));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Held_repeats_swallow_without_retrigger_but_a_real_second_press_triggers()
    {
        var filter = new WinVFilter();
        var fired = 0;
        filter.Triggered += () => fired++;

        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Down));

        // Auto-repeat: down after down, no release between — swallowed, no
        // re-trigger (auto-repeat must not toggle the bar).
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(1, fired);
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Up));

        // A genuine second press — full down/up pair — triggers again: the
        // bar toggles, and nothing reaches the system.
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(2, fired);
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Up));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Up));
    }

    [Fact]
    public void V_released_after_Win_still_gets_its_up_eaten_once()
    {
        var filter = new WinVFilter();

        Feed(filter, SpecialKey.LeftWin, KeyDirection.Down);
        Feed(filter, SpecialKey.V, KeyDirection.Down);
        // Fast fingers: Win released before V.
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Up));
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Up));
    }

    [Fact]
    public void After_a_full_takeover_the_next_win_combo_behaves_normally()
    {
        var filter = new WinVFilter();

        Feed(filter, SpecialKey.LeftWin, KeyDirection.Down);
        Feed(filter, SpecialKey.V, KeyDirection.Down);
        Feed(filter, SpecialKey.V, KeyDirection.Up);
        Feed(filter, SpecialKey.LeftWin, KeyDirection.Up);

        // A later Win+V triggers again — the takeover did not jam the filter.
        var fired = 0;
        filter.Triggered += () => fired++;
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.RightWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(1, fired);
    }

    // --- 钩子漏看了 Win 的一半（用户实录 2026-10-13）-----------------------------------

    [Fact]
    public void A_Win_release_the_hook_never_saw_does_not_turn_plain_V_into_Win_V()
    {
        // Win+L 锁屏：Win 的按下钩子看见了，松开发生在锁屏界面上、钩子收不到。解锁后打字，
        // V、Shift+V、Ctrl+V 全被当成 Win+V 吞掉，窄条一下一下地弹，直到退出拾语。系统自己的
        // 按键状态从不漏：判 V 时问它。
        var winDown = true;
        var filter = new WinVFilter(() => winDown);
        var fired = 0;
        var stale = 0;
        filter.Triggered += () => fired++;
        filter.StaleWinCorrected += () => stale++;

        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.Other, KeyDirection.Down));
        winDown = false;

        for (var press = 0; press < 3; press++)
        {
            Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.V, KeyDirection.Down));
            Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.V, KeyDirection.Up));
        }

        Assert.Equal(0, fired);
        Assert.Equal(1, stale);

        // Healed for good: the next real Win+V is taken over as usual.
        winDown = true;
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void A_Win_press_the_hook_never_saw_still_makes_Win_V()
    {
        // 反过来：Win 是在钩子看不见的地方按下的（锁屏界面、提权窗口里），回来时手还按着——
        // 系统说按着，就是 Win+V。
        var filter = new WinVFilter(() => true);
        var fired = 0;
        filter.Triggered += () => fired++;

        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Down));
        Assert.Equal(KeyFlow.Swallow, Feed(filter, SpecialKey.V, KeyDirection.Up));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Plain_typing_never_asks_the_platform_about_Win()
    {
        // The live check costs a system call; only a V press while the filter
        // believes Win is held — or a V at all when it has a probe — pays it,
        // and every other key goes straight through.
        var asked = 0;
        var filter = new WinVFilter(() => { asked++; return false; });

        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.Other, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.Other, KeyDirection.Up));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Down));
        Assert.Equal(KeyFlow.Pass, Feed(filter, SpecialKey.LeftWin, KeyDirection.Up));
        Assert.Equal(0, asked);
    }
}
