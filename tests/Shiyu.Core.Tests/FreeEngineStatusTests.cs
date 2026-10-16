using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 免费引擎的近况（评审 2026-10-16 F-14）：设置的翻译方式卡上写出上次成功、上次失败各是几点、哪一家，
/// 以及现在在用哪一家。只记时间与引擎名，不记内容；整段译完才算成功，取消不算。
/// </summary>
public class FreeEngineStatusTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public Rig(ScriptedTranslator? bing = null, ScriptedTranslator? tencent = null)
        {
            Clock = new TestClock(Start);
            var health = new FreeEngineHealth(Clock);
            Status = new FreeEngineStatus(health, Clock);
            Backend = new FreeEngineBackend(
                bing ?? ScriptedTranslator.Prefixing("B:"),
                tencent ?? ScriptedTranslator.Prefixing("T:"),
                health,
                pieceDelay: () => TimeSpan.Zero,
                status: Status);
        }

        public TestClock Clock { get; }
        public FreeEngineStatus Status { get; }
        public FreeEngineBackend Backend { get; }

        public IReadOnlyList<string> Lines => Status.Describe(TimeZoneInfo.Utc);

        public async Task Run(string text)
        {
            await foreach (var _ in Backend.TranslateAsync(new TranslationRequest(text, "Chinese"), CancellationToken.None))
            {
            }
        }
    }

    [Fact]
    public void Before_any_translation_it_says_so_and_that_bing_comes_first()
    {
        var rig = new Rig();

        Assert.Equal(
            ["这次启动以来还没用免费引擎翻译过。", "现在用：微软必应，不可用时自动改用腾讯"],
            rig.Lines);
    }

    [Fact]
    public async Task A_translation_shows_when_it_succeeded_and_which_engine_did_it()
    {
        var rig = new Rig();
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        await rig.Run("hello");

        Assert.Equal(["上次成功：12:05 · 微软必应", "现在用：微软必应，不可用时自动改用腾讯"], rig.Lines);
    }

    [Fact]
    public async Task A_bing_failure_shows_the_fallback_and_when_bing_is_tried_again()
    {
        var rig = new Rig(bing: ScriptedTranslator.Failing());

        await rig.Run("hello");

        Assert.Equal(
            [
                "上次成功：12:00 · 腾讯交互翻译",
                "上次失败：12:00 · 微软必应没有响应，改用了腾讯",
                "现在用：腾讯交互翻译（12:05 起再试微软）",
            ],
            rig.Lines);

        // 冷却一过，「现在用」回到必应；失败那一行照旧留着，直到下一次失败盖掉它。
        rig.Clock.Advance(FreeEngineHealth.BingCooldown);
        Assert.Equal("现在用：微软必应，不可用时自动改用腾讯", rig.Lines[^1]);
        Assert.Contains("上次失败：12:00 · 微软必应没有响应，改用了腾讯", rig.Lines);
    }

    [Fact]
    public async Task Both_engines_failing_is_told_as_such()
    {
        var rig = new Rig(bing: ScriptedTranslator.Failing(), tencent: ScriptedTranslator.Failing());

        await Assert.ThrowsAsync<TranslationFailedException>(() => rig.Run("hello"));

        Assert.Equal(
            ["上次失败：12:00 · 微软和腾讯都没有响应", "现在用：腾讯交互翻译（12:05 起再试微软）"],
            rig.Lines);
    }

    [Fact]
    public async Task A_cancelled_translation_is_neither_a_success_nor_a_failure()
    {
        var rig = new Rig(bing: ScriptedTranslator.Hanging());
        using var cancel = new CancellationTokenSource();

        var run = rig.Backend.TranslateAsync(new TranslationRequest("hello", "Chinese"), cancel.Token)
            .GetAsyncEnumerator(cancel.Token);
        var pending = run.MoveNextAsync().AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await run.DisposeAsync();

        Assert.Equal("这次启动以来还没用免费引擎翻译过。", rig.Lines[0]);
    }

    [Fact]
    public async Task An_older_event_carries_its_date()
    {
        var rig = new Rig();
        await rig.Run("hello");

        rig.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal("上次成功：10月4日 12:00 · 微软必应", rig.Lines[0]);
    }

    [Fact]
    public async Task Nothing_of_the_text_is_kept()
    {
        var rig = new Rig(bing: ScriptedTranslator.Failing());

        await rig.Run("a private sentence");

        Assert.DoesNotContain(rig.Lines, line => line.Contains("private") || line.Contains("T:"));
    }

    [Fact]
    public async Task Each_record_raises_changed_so_an_open_settings_window_can_refresh()
    {
        var rig = new Rig(bing: ScriptedTranslator.Failing());
        var raised = 0;
        rig.Status.Changed += () => raised++;

        await rig.Run("hello");

        // 必应失败一次、腾讯译完一次。
        Assert.Equal(2, raised);
    }
}
