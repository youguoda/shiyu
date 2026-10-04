using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 免费引擎后端（票 41）：必应为主、腾讯兜底、分块渐进输出。引擎换成假件，
/// 时间换成 <see cref="TestClock"/>——兜底、冷却、取消、超时都不碰网络也不真等。
/// </summary>
public class FreeEngineBackendTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public Rig(
            ScriptedTranslator? bing = null,
            ScriptedTranslator? tencent = null,
            TimeSpan? chunkTimeout = null,
            int firstChunkChars = FreeEngineChunker.FirstChunkChars,
            int chunkChars = FreeEngineChunker.ChunkChars)
        {
            Clock = new TestClock(Start);
            Health = new FreeEngineHealth(Clock);
            Bing = bing ?? ScriptedTranslator.Prefixing("B:");
            Tencent = tencent ?? ScriptedTranslator.Prefixing("T:");
            Backend = new FreeEngineBackend(
                Bing, Tencent, Health, chunkTimeout, pieceDelay: () => TimeSpan.Zero,
                firstChunkChars: firstChunkChars, chunkChars: chunkChars);
        }

        public TestClock Clock { get; }
        public FreeEngineHealth Health { get; }
        public ScriptedTranslator Bing { get; }
        public ScriptedTranslator Tencent { get; }
        public FreeEngineBackend Backend { get; }

        public async Task<string> Run(string text, string target = "Chinese", string? source = null)
            => string.Concat(await Pieces(text, target, source));

        public async Task<List<string>> Pieces(string text, string target = "Chinese", string? source = null)
        {
            var pieces = new List<string>();
            await foreach (var piece in Backend.TranslateAsync(
                new TranslationRequest(text, target) { SourceLanguage = source }, CancellationToken.None))
            {
                pieces.Add(piece);
            }

            return pieces;
        }
    }

    // --- 主路径 ---------------------------------------------------------------------

    [Fact]
    public async Task A_short_text_goes_to_bing_once_and_never_touches_tencent()
    {
        var rig = new Rig();

        var translated = await rig.Run("hello");

        Assert.Equal("B:hello", translated);
        Assert.Equal(1, rig.Bing.Calls);
        Assert.Equal(0, rig.Tencent.Calls);
        Assert.True(rig.Health.BingHealthy);
    }

    [Fact]
    public async Task The_languages_reach_the_engine_as_resolved_codes()
    {
        var rig = new Rig();

        await rig.Run("你好", target: "English", source: "Chinese");
        await rig.Run("hello", target: "Chinese");

        Assert.Equal("zh-Hans", rig.Bing.Languages[0].From!.Bing);
        Assert.Equal("en", rig.Bing.Languages[0].To.Bing);

        // 没声明源语言就是 null——自动检测由引擎自己做。
        Assert.Null(rig.Bing.Languages[1].From);
        Assert.Equal("zh", rig.Bing.Languages[1].To.Tencent);
    }

    [Fact]
    public async Task Whitespace_only_text_yields_nothing_and_asks_nobody()
    {
        var rig = new Rig();

        Assert.Equal(string.Empty, await rig.Run("  \n "));
        Assert.Equal(0, rig.Bing.Calls);
        Assert.Equal(0, rig.Tencent.Calls);
    }

    // --- 分块渐进输出 ---------------------------------------------------------------

    [Fact]
    public async Task Chunks_are_translated_in_order_and_the_text_rebuilds_exactly()
    {
        var rig = new Rig(bing: ScriptedTranslator.Identity(), firstChunkChars: 30, chunkChars: 60);
        var text = string.Join("\n\n", Enumerable.Range(1, 8).Select(i => $"Paragraph {i}: some plain words here."));

        var translated = await rig.Run(text);

        Assert.True(rig.Bing.Calls > 2, "text this long must not be sent as one request");
        Assert.True(rig.Bing.Texts[0].Length <= 30);
        Assert.Equal(text, translated);
        Assert.Equal(0, rig.Tencent.Calls);
    }

    [Fact]
    public async Task Each_chunk_arrives_as_soon_as_it_is_translated()
    {
        // 第二块还没译完时，第一块已经吐出去了——渐进输出的全部意义。
        var secondChunkStarted = new TaskCompletionSource();
        var releaseSecond = new TaskCompletionSource();
        var bing = new ScriptedTranslator(async (text, call, _) =>
        {
            if (call == 2)
            {
                secondChunkStarted.SetResult();
                await releaseSecond.Task;
            }

            return text.ToUpperInvariant();
        });
        var rig = new Rig(bing: bing, firstChunkChars: 20, chunkChars: 20);

        var received = new List<string>();
        var run = Task.Run(async () =>
        {
            await foreach (var piece in rig.Backend.TranslateAsync(
                new TranslationRequest("first line here\n\nsecond line here", "Chinese"), CancellationToken.None))
            {
                received.Add(piece);
            }
        });

        await secondChunkStarted.Task;
        Assert.Contains("FIRST LINE HERE", string.Concat(received));
        Assert.DoesNotContain("SECOND", string.Concat(received));

        releaseSecond.SetResult();
        await run;
        Assert.Equal("FIRST LINE HERE\n\nSECOND LINE HERE", string.Concat(received));
    }

    [Fact]
    public async Task A_chunks_translation_is_sliced_by_the_shared_slicer()
    {
        // 译文一到就经切片器吐出：与公共通道是同一个函数的同一种切法。
        const string translation = "第一句。第二句！第三句？第四句。";
        var rig = new Rig(bing: new ScriptedTranslator((_, _, _) => Task.FromResult(translation)));

        var pieces = await rig.Pieces("one chunk only");

        Assert.True(pieces.Count >= 4);
        Assert.Equal(StreamingSlicer.Split(translation), pieces);
    }

    [Fact]
    public async Task Newlines_between_chunks_are_kept_exactly()
    {
        var rig = new Rig(bing: ScriptedTranslator.Identity(), firstChunkChars: 12, chunkChars: 12);
        const string text = "alpha beta.\n\n\ngamma delta.\r\n  epsilon zeta.\n";

        var translated = await rig.Run(text);

        Assert.Equal(text, translated);
    }

    [Fact]
    public async Task Sentences_cut_inside_a_paragraph_get_a_space_between_them_in_a_spaced_script()
    {
        // 中文原文句与句之间没有空白；译成英文时拼回去要补一个空格，
        // 否则成了 "Hello.How are you?"。
        var bing = new ScriptedTranslator((text, _, _) => Task.FromResult(text switch
        {
            "你好。" => "Hello.",
            "你好吗？" => "How are you?",
            _ => text,
        }));
        var rig = new Rig(bing: bing, firstChunkChars: 5, chunkChars: 5);

        var translated = await rig.Run("你好。你好吗？", target: "English");

        Assert.Equal("Hello. How are you?", translated);
    }

    [Fact]
    public async Task Sentences_cut_inside_a_paragraph_lose_the_space_in_an_unspaced_script()
    {
        // 英文原文句间有空格；译成中文时句与句之间不该留一个空格。
        var bing = new ScriptedTranslator((text, _, _) => Task.FromResult(text switch
        {
            "Hello there." => "你好。",
            "How are you?" => "你好吗？",
            _ => text,
        }));
        var rig = new Rig(bing: bing, firstChunkChars: 14, chunkChars: 14);

        var translated = await rig.Run("Hello there. How are you?");

        Assert.Equal("你好。你好吗？", translated);
    }

    // --- 兜底与冷却 -----------------------------------------------------------------

    [Fact]
    public async Task A_failure_in_chunk_k_moves_chunk_k_and_everything_after_it_to_tencent()
    {
        // 必应第 2 块失败：第 1 块已是必应的译文；第 2 块起全部改走腾讯，中途不再切回。
        var bing = new ScriptedTranslator((text, call, _) => call == 2
            ? throw new FreeEngineException("bing broke on chunk 2")
            : Task.FromResult("B:" + text));
        var rig = new Rig(bing: bing, firstChunkChars: 12, chunkChars: 12);

        var translated = await rig.Run("one one one.\n\ntwo two two.\n\nthree three.");

        Assert.Equal("B:one one one.\n\nT:two two two.\n\nT:three three.", translated);
        Assert.Equal(2, rig.Bing.Calls);
        Assert.Equal(2, rig.Tencent.Calls);
        Assert.Equal("two two two.", rig.Tencent.Texts[0]);
    }

    [Fact]
    public async Task A_bing_failure_marks_it_unhealthy_and_later_translations_skip_it()
    {
        var rig = new Rig(bing: ScriptedTranslator.Failing());

        await rig.Run("first");
        Assert.False(rig.Health.BingHealthy);
        Assert.Equal(1, rig.Bing.Calls);

        // 冷却期内的新翻译直接走腾讯，不让每次翻译都先白等一遍。
        var second = await rig.Run("second");

        Assert.Equal("T:second", second);
        Assert.Equal(1, rig.Bing.Calls);
    }

    [Fact]
    public async Task Bing_is_asked_again_once_the_five_minute_cooldown_is_over()
    {
        var failFirstOnly = new ScriptedTranslator((text, call, _) => call == 1
            ? throw new FreeEngineException("blip")
            : Task.FromResult("B:" + text));
        var rig = new Rig(bing: failFirstOnly);

        await rig.Run("first");

        rig.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal("T:second", await rig.Run("second"));
        Assert.Equal(1, rig.Bing.Calls);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(rig.Health.BingHealthy);
        Assert.Equal("B:third", await rig.Run("third"));
        Assert.Equal(2, rig.Bing.Calls);
    }

    [Fact]
    public async Task Cancelling_is_not_a_failure_and_does_not_trigger_the_fallback()
    {
        var rig = new Rig(bing: ScriptedTranslator.Hanging());
        using var cancel = new CancellationTokenSource();

        var run = rig.Backend.TranslateAsync(new TranslationRequest("hello", "Chinese"), cancel.Token)
            .GetAsyncEnumerator(cancel.Token);
        var pending = run.MoveNextAsync().AsTask();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await run.DisposeAsync();

        Assert.Equal(0, rig.Tencent.Calls);
        Assert.True(rig.Health.BingHealthy);
    }

    [Fact]
    public async Task A_chunk_that_takes_too_long_counts_as_a_failure()
    {
        // 单块超时：必应卡住 → 到点算失败 → 同一块改走腾讯。
        var rig = new Rig(bing: ScriptedTranslator.Hanging(), chunkTimeout: TimeSpan.FromMilliseconds(60));

        var translated = await rig.Run("hello");

        Assert.Equal("T:hello", translated);
        Assert.False(rig.Health.BingHealthy);
    }

    [Fact]
    public async Task An_empty_answer_for_real_text_counts_as_a_failure()
    {
        var rig = new Rig(bing: new ScriptedTranslator((_, _, _) => Task.FromResult("  ")));

        Assert.Equal("T:hello", await rig.Run("hello"));
        Assert.False(rig.Health.BingHealthy);
    }

    [Fact]
    public async Task Both_engines_failing_gives_the_human_sentence_with_the_causes_attached()
    {
        var rig = new Rig(
            bing: ScriptedTranslator.Failing("bing down"),
            tencent: ScriptedTranslator.Failing("tencent down"));

        var failure = await Assert.ThrowsAsync<TranslationFailedException>(() => rig.Run("hello"));

        Assert.Equal(
            "免费引擎暂时不可用：微软和腾讯都没有响应，稍后再试，或在 设置 → 翻译 换一种翻译方式。",
            failure.Message);

        // 内部异常照常挂上，供「复制错误详情」使用：两家各自的原因都在。
        Assert.NotNull(failure.InnerException);
        Assert.Contains("bing down", failure.InnerException!.ToString());
        Assert.Contains("tencent down", failure.InnerException.ToString());
    }

    [Fact]
    public async Task Text_translated_before_a_total_failure_is_still_delivered()
    {
        // 第 1 块译好了、第 2 块两家都挂：第 1 块不能因此被收回。
        var bing = new ScriptedTranslator((text, call, _) => call == 1
            ? Task.FromResult("B:" + text)
            : throw new FreeEngineException("bing down"));
        var rig = new Rig(bing: bing, tencent: ScriptedTranslator.Failing(), firstChunkChars: 12, chunkChars: 12);

        var received = new List<string>();
        await Assert.ThrowsAsync<TranslationFailedException>(async () =>
        {
            await foreach (var piece in rig.Backend.TranslateAsync(
                new TranslationRequest("one one one.\n\ntwo two two.", "Chinese"), CancellationToken.None))
            {
                received.Add(piece);
            }
        });

        Assert.Equal("B:one one one.", string.Concat(received).TrimEnd());
    }

    [Fact]
    public async Task A_session_that_hits_a_total_failure_ends_failed_with_the_human_sentence()
    {
        var rig = new Rig(bing: ScriptedTranslator.Failing(), tencent: ScriptedTranslator.Failing());
        var session = new TranslationSession(rig.Backend);

        await session.RunAsync(new TranslationRequest("hello", "Chinese"));

        Assert.Equal(TranslationState.Failed, session.State);
        Assert.StartsWith("免费引擎暂时不可用", session.Error);
        Assert.NotNull(session.Failure!.InnerException);
    }

    // --- 前置拒绝 -------------------------------------------------------------------

    [Fact]
    public async Task Text_over_five_thousand_chars_is_refused_before_any_request()
    {
        var rig = new Rig();

        var failure = await Assert.ThrowsAsync<TranslationFailedException>(
            () => rig.Run(new string('a', FreeEngineBackend.MaxTextChars + 1)));

        Assert.Equal(
            "免费引擎单次最多翻译 5000 字，长文请改用自备密钥。",
            failure.Message);
        Assert.Equal(0, rig.Bing.Calls);
        Assert.Equal(0, rig.Tencent.Calls);
    }

    [Fact]
    public async Task Exactly_five_thousand_chars_is_accepted()
    {
        var rig = new Rig(bing: ScriptedTranslator.Identity());

        var translated = await rig.Run(new string('a', FreeEngineBackend.MaxTextChars));

        // 一整段没有任何断点的长串被硬切成多块；块间是否补空格不是这条要管的事。
        Assert.True(rig.Bing.Calls > 1);
        Assert.Equal(FreeEngineBackend.MaxTextChars, translated.Replace(" ", string.Empty).Length);
    }

    [Theory]
    [InlineData("Klingon", null)]
    [InlineData("Chinese", "Elvish")]
    public async Task A_language_outside_the_table_is_refused_in_plain_words(string target, string? source)
    {
        var rig = new Rig();

        var failure = await Assert.ThrowsAsync<TranslationFailedException>(
            () => rig.Run("hello", target, source));

        Assert.StartsWith("免费引擎暂不支持", failure.Message);
        Assert.Equal(0, rig.Bing.Calls);
    }

    [Fact]
    public void The_cooldown_is_five_minutes_and_the_chunk_timeout_eight_seconds()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), FreeEngineHealth.BingCooldown);
        Assert.Equal(TimeSpan.FromSeconds(8), FreeEngineBackend.DefaultChunkTimeout);
    }

    [Fact]
    public void The_free_engine_is_not_a_general_model()
    {
        // 它没有 prompt，不是通用模型：不实现 IStreamingModel，Agent 动作、批量翻译
        // 与 LLM 词典因此永远拿不到它（见 FreeEngineBoundaryTests）。
        Assert.False(typeof(IStreamingModel).IsAssignableFrom(typeof(FreeEngineBackend)));
        Assert.True(typeof(ITranslationBackend).IsAssignableFrom(typeof(FreeEngineBackend)));
    }
}
