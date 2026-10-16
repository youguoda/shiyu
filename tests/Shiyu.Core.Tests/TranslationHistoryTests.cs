using Shiyu.Core;
using Shiyu.Core.Tests.Fakes;

namespace Shiyu.Core.Tests;

/// <summary>
/// Translations become first-class entries that keep their relationship and
/// their honesty: linked to the original, marked as ours, gated by the same
/// exclusion rules, and — the standing rule — only what the user selected
/// ever leaves the machine.
/// </summary>
public class TranslationHistoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeModel : IStreamingModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public Func<string, string> Reply { get; set; } = text => "【译】" + text;

        public async IAsyncEnumerable<string> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return Reply(request.UserContent);
        }
    }

    private sealed class FailingModel : IStreamingModel
    {
        public async IAsyncEnumerable<string> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation = default)
        {
            await Task.Yield();
            throw new TranslationFailedException("后端无法连接");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class Bench : IDisposable
    {
        public TempDatabase Database { get; } = new();
        public EntryStore Store { get; }
        public FakeClipboardMonitor Monitor { get; } = new();
        public ClipboardPipeline Pipeline { get; }

        public Bench(ExclusionPolicy? policy = null)
        {
            Store = EntryStore.Open(Database.FilePath);
            Pipeline = new ClipboardPipeline(Monitor, Store, new TestClock(Noon), policy ?? new ExclusionPolicy());
        }

        public void Dispose()
        {
            Store.Dispose();
            Database.Dispose();
        }
    }

    [Fact]
    public void A_kept_translation_is_a_linked_entry()
    {
        using var bench = new Bench();
        var original = bench.Store.Append("hello world", "ZCode", Noon);

        bench.Pipeline.RecordTranslation("你好，世界", original.Id);

        var saved = bench.Store.Recent(limit: 5).Single(entry => entry.Text == "你好，世界");
        Assert.Equal(original.Id, saved.TranslatedFrom);
        Assert.Equal("Shiyu", saved.SourceApp);
    }

    [Fact]
    public void Deleting_the_original_keeps_the_translation_but_unlinks_it()
    {
        using var bench = new Bench();
        var original = bench.Store.Append("hello", "ZCode", Noon);
        bench.Pipeline.RecordTranslation("你好", original.Id);
        var translationId = bench.Store.Recent(limit: 5).Single(entry => entry.Text == "你好").Id;

        bench.Store.Delete(original.Id);

        var survivor = bench.Store.Get(translationId);
        Assert.NotNull(survivor);
        Assert.Equal("你好", survivor!.Text);
        Assert.Null(survivor.TranslatedFrom);
    }

    [Fact]
    public void A_translation_blocked_by_an_exclusion_rule_never_lands()
    {
        using var bench = new Bench(new ExclusionPolicy(
            [new ExclusionRule(ExclusionRuleKind.ContentPattern, "sekrit")]));
        var original = bench.Store.Append("safe text", "ZCode", Noon);

        var kept = bench.Pipeline.RecordTranslation("the sekrit answer", original.Id);

        Assert.False(kept);
        Assert.DoesNotContain(bench.Store.Recent(limit: 10), entry => entry.Text.Contains("sekrit"));
    }

    [Fact]
    public async Task The_batch_sends_exactly_the_selected_entries_and_nothing_else()
    {
        using var bench = new Bench();
        var alpha = bench.Store.Append("alpha text", "ZCode", Noon);
        var beta = bench.Store.Append("beta text", "ZCode", Noon.AddMinutes(1));
        bench.Store.Append("not selected", "ZCode", Noon.AddMinutes(2));

        var model = new FakeModel();
        var batch = new TranslationBatch(bench.Store, bench.Pipeline, new ExclusionPolicy(), model, "中文");
        var result = await batch.RunAsync([alpha.Id, beta.Id]);

        Assert.Equal(2, result.Translated);
        Assert.Equal(2, batch.SentRequests.Count);
        Assert.Equal("alpha text", batch.SentRequests[0].UserContent);
        Assert.Equal("beta text", batch.SentRequests[1].UserContent);
        Assert.All(batch.SentRequests, request => Assert.DoesNotContain("not selected", request.UserContent));

        // 全部成功就没有"原因"可报。
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Excluded_and_already_translated_entries_are_skipped_unsent()
    {
        using var bench = new Bench(new ExclusionPolicy(
            [new ExclusionRule(ExclusionRuleKind.ContentPattern, "^secret")]));
        var clean = bench.Store.Append("clean text", "ZCode", Noon);
        var secret = bench.Store.Append("secret stuff", "ZCode", Noon.AddMinutes(1));
        var original = bench.Store.Append("to be translated", "ZCode", Noon.AddMinutes(2));
        bench.Pipeline.RecordTranslation("会被跳过的译文", original.Id);
        var alreadyTranslation = bench.Store.Recent(limit: 5).Single(entry => entry.Text == "会被跳过的译文");

        var model = new FakeModel();
        var batch = new TranslationBatch(bench.Store, bench.Pipeline,
            new ExclusionPolicy([new ExclusionRule(ExclusionRuleKind.ContentPattern, "^secret")]), model, "中文");
        var result = await batch.RunAsync([clean.Id, secret.Id, alreadyTranslation.Id]);

        Assert.Equal(1, result.Translated);
        Assert.Equal(2, result.Skipped);

        // Only the clean entry was sent: exclusion decided before sending,
        // and a translation of a translation was never asked for.
        Assert.Single(batch.SentRequests);
        Assert.Equal("clean text", batch.SentRequests[0].UserContent);
    }

    [Fact]
    public async Task A_dead_backend_fails_the_item_without_killing_the_batch()
    {
        using var bench = new Bench();
        var first = bench.Store.Append("one", "ZCode", Noon);
        var second = bench.Store.Append("two", "ZCode", Noon.AddMinutes(1));

        var batch = new TranslationBatch(bench.Store, bench.Pipeline, new ExclusionPolicy(),
            new FailingModel(), "中文");
        var result = await batch.RunAsync([first.Id, second.Id]);

        Assert.Equal(0, result.Translated);
        Assert.Equal(2, result.Failed);
        Assert.Equal(2, bench.Store.Count());

        // 报告带上第一条失败的人话原因（票 08）：光说"失败 2 条"是死胡同。
        Assert.Equal("后端无法连接", result.FailureReason);
    }

    [Fact]
    public async Task Cancelling_midway_keeps_what_was_already_filed()
    {
        using var bench = new Bench();
        for (var i = 0; i < 5; i++)
        {
            bench.Store.Append("line " + i, "ZCode", Noon.AddMinutes(i));
        }

        var model = new FakeModel
        {
            Reply = text =>
            {
                if (text.EndsWith("2"))
                {
                    throw new OperationCanceledException();
                }

                return "【译】" + text;
            },
        };

        var ids = bench.Store.Recent(limit: 5).Select(entry => entry.Id).ToList();
        var batch = new TranslationBatch(bench.Store, bench.Pipeline, new ExclusionPolicy(), model, "中文");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => batch.RunAsync(ids));

        // "line 4", "line 3" landed before "line 2" cancelled the run.
        var translations = bench.Store.Recent(limit: 10).Where(entry => entry.TranslatedFrom is not null).ToList();
        Assert.Equal(2, translations.Count);
    }

    [Fact]
    public void Batch_results_carry_the_link_to_their_originals()
    {
        using var bench = new Bench();
        var original = bench.Store.Append("link me", "ZCode", Noon);
        bench.Pipeline.RecordTranslation("链接我", original.Id);

        var saved = bench.Store.Recent(limit: 5).Single(entry => entry.Text == "链接我");
        Assert.Equal(original.Id, saved.TranslatedFrom);
        Assert.Equal("Shiyu", saved.SourceApp);
    }
    // ── AI 动作的结果存为条目（评审 2026-10-16 F-17）──

    [Fact]
    public void A_saved_action_result_is_a_new_entry_whose_note_names_its_sources()
    {
        using var bench = new Bench();
        var first = bench.Store.Append("周一的会议纪要：预算还差两万", "ZCode", Noon);
        var second = bench.Store.Append("客户电话：下周三前给报价", "ZCode", Noon);

        var saved = bench.Pipeline.RecordActionResult("  合并后的笔记正文  ", AgentActionKind.MergeIntoNote, [first, second]);

        Assert.NotNull(saved);
        var stored = bench.Store.Get(saved!.Id)!;
        Assert.Equal("合并后的笔记正文", stored.Text);
        Assert.Equal("Shiyu", stored.SourceApp);
        Assert.Equal("由「合并成笔记」生成，来自 2 条：「周一的会议纪要：预算还差…」「客户电话：下周三前给报价」", stored.Note);
        Assert.Equal(3, bench.Store.Count());
    }

    [Fact]
    public void An_action_result_blocked_by_an_exclusion_rule_is_not_saved()
    {
        using var bench = new Bench(new ExclusionPolicy(
            [new ExclusionRule(ExclusionRuleKind.ContentPattern, "sekrit")]));
        var source = bench.Store.Append("safe text", "ZCode", Noon);

        var saved = bench.Pipeline.RecordActionResult("the sekrit summary", AgentActionKind.Summarise, [source]);

        Assert.Null(saved);
        Assert.Equal(1, bench.Store.Count());
    }

    [Fact]
    public void An_empty_action_result_is_not_saved()
    {
        using var bench = new Bench();
        var source = bench.Store.Append("text", "ZCode", Noon);

        Assert.Null(bench.Pipeline.RecordActionResult("   ", AgentActionKind.Rewrite, [source]));
        Assert.Equal(1, bench.Store.Count());
    }
}
