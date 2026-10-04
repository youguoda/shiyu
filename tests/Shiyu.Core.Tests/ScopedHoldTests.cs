using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 面板在场时写设置会弄丢 Esc（票 41）。每次写设置都会让热键注册表整体重建，
/// 面板持有的作用域 Esc 跟旧注册表一起被注销，而"已经持有"的标记不会让它重挂。
/// 修法是在新注册表上重挂，重挂本身有两条顺序要求：排在 HotkeyModule 重建之后
/// （由 AppShell.HotkeysRebuilt 事件保证）、先放旧作用域再取新的（这里钉住）。
/// 新旧注册表共用同一个消息窗口、id 都从 1 编号，旧作用域放得晚了，会把新注册
/// 的同号热键注销掉——注册表一侧的守卫见 Shiyu.Windows.Tests 的 HotkeyScopeTests。
/// </summary>
public class ScopedHoldTests
{
    private sealed class RecordingScope(List<string> log, string name) : IDisposable
    {
        public void Dispose() => log.Add("release " + name);
    }

    [Fact]
    public void Hold_takes_the_scope_only_once()
    {
        var taken = 0;
        var hold = new ScopedHold(() =>
        {
            taken++;
            return new RecordingScope([], "scope");
        });

        hold.Hold();
        hold.Hold();

        Assert.True(hold.IsHeld);
        Assert.Equal(1, taken);
    }

    [Fact]
    public void Release_lets_go_and_is_safe_to_repeat()
    {
        var log = new List<string>();
        var hold = new ScopedHold(() => new RecordingScope(log, "scope"));
        hold.Hold();

        hold.Release();
        hold.Release();

        Assert.False(hold.IsHeld);
        Assert.Equal(["release scope"], log);
    }

    [Fact]
    public void A_scope_nobody_would_give_leaves_the_hold_empty_and_is_tried_again_next_time()
    {
        // Esc 被别的软件占着：没取到不是错误（面板照常显示），下次再试。
        var attempts = 0;
        var hold = new ScopedHold(() =>
        {
            attempts++;
            return attempts < 2 ? null : new RecordingScope([], "late");
        });

        hold.Hold();
        Assert.False(hold.IsHeld);

        hold.Reacquire();
        Assert.True(hold.IsHeld);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Reacquire_lets_go_of_the_old_scope_before_taking_the_new_one()
    {
        var log = new List<string>();
        var generation = 0;
        var hold = new ScopedHold(() =>
        {
            var name = "scope#" + ++generation;
            log.Add("take " + name);
            return new RecordingScope(log, name);
        });
        hold.Hold();

        hold.Reacquire();

        Assert.Equal(["take scope#1", "release scope#1", "take scope#2"], log);
        Assert.True(hold.IsHeld);
    }

    [Fact]
    public void Reacquire_on_an_empty_hold_just_takes_one()
    {
        var log = new List<string>();
        var hold = new ScopedHold(() =>
        {
            log.Add("take");
            return new RecordingScope(log, "scope");
        });

        hold.Reacquire();

        Assert.Equal(["take"], log);
        Assert.True(hold.IsHeld);
    }
}
