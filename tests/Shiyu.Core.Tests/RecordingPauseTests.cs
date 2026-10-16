using Shiyu.Core;
using Shiyu.Core.Tests.Fakes;

namespace Shiyu.Core.Tests;

/// <summary>
/// 暂停记录（评审 2026-10-16 F-06）：共享屏幕、演示、处理不在排除名单里的敏感内容时，不必退出拾语。暂停期间
/// 复制的内容不落库、不弹徽标；到点即恢复——按复制那一刻的时间判定，不等任何定时器。
/// </summary>
public class RecordingPauseTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Recording_runs_until_someone_pauses_it()
    {
        var pause = new RecordingPause();

        Assert.False(pause.IsPaused(Noon));
        Assert.Null(pause.Until);
        Assert.Null(pause.Describe(Noon));
    }

    [Fact]
    public void A_timed_pause_ends_at_its_moment_without_anyone_resuming_it()
    {
        var pause = new RecordingPause();
        pause.PauseFor(TimeSpan.FromMinutes(15), Noon);

        Assert.True(pause.IsPaused(Noon.AddMinutes(14)));
        Assert.False(pause.IsPaused(Noon.AddMinutes(15)));
        Assert.Equal(Noon.AddMinutes(15), pause.Until);
        Assert.Equal("已暂停记录剪贴板，12:15 自动恢复", pause.Describe(Noon));
        Assert.Null(pause.Describe(Noon.AddMinutes(16)));
    }

    [Fact]
    public void An_open_pause_lasts_until_resumed()
    {
        var pause = new RecordingPause();
        pause.PauseUntilResumed();

        Assert.True(pause.IsPaused(Noon.AddDays(3)));
        Assert.Null(pause.Until);
        Assert.Equal("已暂停记录剪贴板，直到你恢复", pause.Describe(Noon));

        pause.Resume();
        Assert.False(pause.IsPaused(Noon));
    }

    [Fact]
    public void Every_change_is_announced()
    {
        var pause = new RecordingPause();
        var changes = 0;
        pause.Changed += () => changes++;

        pause.PauseFor(TimeSpan.FromHours(1), Noon);
        pause.PauseUntilResumed();
        pause.Resume();

        Assert.Equal(3, changes);
    }

    [Fact]
    public void Copies_made_while_paused_leave_no_entry_and_no_badge_and_recording_resumes_on_time()
    {
        using var database = new TempDatabase();
        var clipboard = new FakeClipboardMonitor();
        var clock = new TestClock(Noon);
        using var store = EntryStore.Open(database.FilePath);
        var pause = new RecordingPause();
        using var pipeline = new ClipboardPipeline(clipboard, store, clock, new ExclusionPolicy()) { Pause = pause };
        var badges = new List<string>();
        pipeline.BadgeDeserved += badges.Add;

        pause.PauseFor(TimeSpan.FromMinutes(15), clock.GetUtcNow());
        clipboard.Emit("first secret");
        clipboard.Emit("second secret");
        clipboard.Emit("third secret");

        Assert.Empty(store.Recent(limit: 10));
        Assert.Empty(badges);

        clock.Advance(TimeSpan.FromMinutes(15));
        clipboard.Emit("after the pause");

        Assert.Equal("after the pause", Assert.Single(store.Recent(limit: 10)).Text);
    }
}
