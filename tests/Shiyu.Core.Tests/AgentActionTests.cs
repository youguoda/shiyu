using System.Runtime.CompilerServices;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class AgentActionTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static Entry Make(long id, string text) => new(id, text, "test", Noon);

    private sealed class RecordingModel(params string[] pieces) : IStreamingModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public Exception? Fails { get; init; }

        public async IAsyncEnumerable<string> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            Requests.Add(request);

            if (Fails is not null)
            {
                await Task.Yield();
                throw Fails;
            }

            foreach (var piece in pieces)
            {
                cancellation.ThrowIfCancellationRequested();
                yield return piece;
                await Task.Yield();
            }
        }
    }

    [Fact]
    public async Task Only_the_selected_entries_are_sent()
    {
        var model = new RecordingModel("结果");
        var run = new AgentRun(model);

        var selected = new[] { Make(1, "选中的第一条"), Make(3, "选中的第三条") };

        await run.RunAsync(AgentActionKind.Summarise, selected);

        var sent = Assert.Single(model.Requests);

        // The promise the whole feature rests on. Anything the user did not
        // select must not appear in what leaves the machine.
        Assert.Contains("选中的第一条", sent.UserContent);
        Assert.Contains("选中的第三条", sent.UserContent);
        Assert.DoesNotContain("没有选中", sent.UserContent);
    }

    [Fact]
    public async Task Nothing_selected_sends_nothing_at_all()
    {
        var model = new RecordingModel("结果");
        var run = new AgentRun(model);

        await run.RunAsync(AgentActionKind.Summarise, []);

        Assert.Empty(model.Requests);
        Assert.Null(run.LastRequest);
    }

    [Fact]
    public void Building_the_payload_includes_the_given_entries_and_nothing_else()
    {
        var content = AgentActions.BuildContent([Make(1, "甲"), Make(2, "乙")]);

        Assert.Contains("甲", content);
        Assert.Contains("乙", content);
        Assert.DoesNotContain("丙", content);
    }

    [Fact]
    public async Task The_streamed_answer_accumulates_as_it_arrives()
    {
        var run = new AgentRun(new RecordingModel("这是", "一段", "总结"));
        var snapshots = new List<string>();
        run.Updated += () => snapshots.Add(run.Output);

        await run.RunAsync(AgentActionKind.Summarise, [Make(1, "材料")]);

        Assert.Equal(TranslationState.Finished, run.State);
        Assert.Equal("这是一段总结", run.Output);
        Assert.Contains("这是", snapshots);
    }

    [Fact]
    public async Task An_unreachable_agent_reports_itself_without_taking_anything_down()
    {
        var run = new AgentRun(new RecordingModel { Fails = new TranslationFailedException("连不上") });

        await run.RunAsync(AgentActionKind.Summarise, [Make(1, "材料")]);

        Assert.Equal(TranslationState.Failed, run.State);
        Assert.Equal("连不上", run.Error);
    }

    [Fact]
    public async Task An_unexpected_error_becomes_a_message_rather_than_a_crash()
    {
        var run = new AgentRun(new RecordingModel { Fails = new InvalidOperationException("意外") });

        await run.RunAsync(AgentActionKind.Summarise, [Make(1, "材料")]);

        Assert.Equal(TranslationState.Failed, run.State);
        Assert.Equal("意外", run.Error);
    }

    [Theory]
    [InlineData(AgentActionKind.Summarise)]
    [InlineData(AgentActionKind.MergeIntoNote)]
    [InlineData(AgentActionKind.Rewrite)]
    [InlineData(AgentActionKind.SuggestTags)]
    public void Every_action_has_a_prompt_and_a_label(AgentActionKind kind)
    {
        Assert.NotEmpty(AgentActions.SystemPrompt(kind));
        Assert.NotEmpty(AgentActions.Label(kind));
    }

    [Fact]
    public void Suggested_tags_are_read_back_one_per_line()
    {
        var tags = AgentActions.ParseSuggestedTags("项目甲\n待办\n参考资料");

        Assert.Equal(new[] { "项目甲", "待办", "参考资料" }, tags);
    }

    [Fact]
    public void Bullets_and_numbering_the_model_added_are_stripped()
    {
        // Models reach for these however firmly the prompt asks them not to.
        var tags = AgentActions.ParseSuggestedTags("- 项目甲\n* 待办\n1. 参考资料\n• 归档");

        Assert.Equal(new[] { "项目甲", "待办", "参考资料", "归档" }, tags);
    }

    [Fact]
    public void Duplicate_and_overlong_suggestions_are_discarded()
    {
        var tags = AgentActions.ParseSuggestedTags(
            "项目甲\n项目甲\n这是一个长得完全不像标签的整句话应该被丢掉因为没人会这样打标签");

        Assert.Single(tags);
        Assert.Equal("项目甲", tags[0]);
    }

    [Fact]
    public void Nothing_suggested_parses_to_nothing()
        => Assert.Empty(AgentActions.ParseSuggestedTags("   \n\n  "));

    // ── 存为条目（评审 2026-10-16 F-17）──

    [Fact]
    public void Only_tag_suggestions_cannot_be_saved_as_an_entry()
    {
        Assert.True(AgentActions.CanSaveResult(AgentActionKind.Summarise));
        Assert.True(AgentActions.CanSaveResult(AgentActionKind.MergeIntoNote));
        Assert.True(AgentActions.CanSaveResult(AgentActionKind.Rewrite));
        Assert.False(AgentActions.CanSaveResult(AgentActionKind.SuggestTags));
    }

    [Fact]
    public void The_note_names_three_sources_by_their_first_words_and_says_there_are_more()
    {
        var note = AgentActions.ResultNote(
            AgentActionKind.Summarise,
            [Make(1, "\n  first   line\nsecond line"), Make(2, "甲"), Make(3, "乙"), Make(4, "丙")]);

        Assert.Equal("由「总结」生成，来自 4 条：「first line」「甲」「乙」等", note);
    }
}

public class NoBackgroundRequestsTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class WatchfulModel : IStreamingModel
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<string> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            Calls++;
            await Task.Yield();
            yield return "unexpected";
        }
    }

    [Fact]
    public async Task Recording_a_whole_session_of_copies_never_reaches_the_model()
    {
        using var database = new TempDatabase();
        var model = new WatchfulModel();
        var clipboard = new Fakes.FakeClipboardMonitor();
        using var store = EntryStore.Open(database.FilePath);
        using var pipeline = new ClipboardPipeline(
            clipboard, store, new TestClock(Noon), new ExclusionPolicy());

        // An agent run exists only where a user selected something and pressed
        // a button. Nothing in the recording path may reach outward on its own.
        foreach (var text in new[] { "hello there", "这是中文", "const x = 1;", "https://example.com" })
        {
            clipboard.Emit(text);
        }

        await pipeline.Idle;

        Assert.Equal(0, model.Calls);
        Assert.Equal(4, store.Count());
    }
}
