using System.Runtime.CompilerServices;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 把会话开出的一次请求跑成 <see cref="TranslationSession"/>，再把结局按序号交回会话（票 43）。
/// 单测不碰真实网络：用脚本后端。
/// </summary>
public class ReverseInputRunnerTests
{
    /// <summary>吐一串片段（可在中途失败），并记下收到的请求。</summary>
    private sealed class ScriptedBackend(params string[] pieces) : ITranslationBackend
    {
        public Exception? ThrowAfterPieces { get; init; }

        public List<TranslationRequest> Requests { get; } = [];

        public async IAsyncEnumerable<string> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            Requests.Add(request);
            foreach (var piece in pieces)
            {
                cancellation.ThrowIfCancellationRequested();
                yield return piece;
                await Task.Yield();
            }

            if (ThrowAfterPieces is not null)
            {
                throw ThrowAfterPieces;
            }
        }
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static ReverseRun Run(int seq = 7, PromptTemplate? template = null)
        => new(seq, new TranslationRequest("你好", "English")
        {
            SourceLanguage = "Chinese",
            Template = template ?? PromptTemplates.Standard,
        });

    [Fact]
    public async Task The_request_reaches_the_backend_exactly_as_the_session_built_it()
    {
        var backend = new ScriptedBackend("Hello");
        var run = Run();

        await ReverseInputRunner.RunAsync(run, backend, onPartial: null, CancellationToken.None);

        Assert.Same(run.Request, Assert.Single(backend.Requests));
    }

    [Fact]
    public async Task Streamed_text_arrives_cleaned_and_tagged_with_the_sequence_number()
    {
        var backend = new ScriptedBackend("<text>\n", "Hel", "lo", "\n</text>");
        var partials = new List<(int Seq, string Text)>();

        var outcome = await ReverseInputRunner.RunAsync(
            Run(seq: 7, PromptTemplates.Colloquial),
            backend,
            (seq, text) => partials.Add((seq, text)),
            CancellationToken.None);

        Assert.All(partials, partial => Assert.Equal(7, partial.Seq));
        Assert.Contains(partials, partial => partial.Text == "Hel");
        Assert.DoesNotContain(partials, partial => partial.Text.Contains("<text>", StringComparison.Ordinal));
        Assert.Equal(TranslationState.Finished, outcome.State);
        Assert.Equal("Hello", outcome.Text);
        Assert.Equal(7, outcome.Seq);
    }

    [Fact]
    public async Task Nothing_is_reported_before_the_first_piece_has_text()
    {
        var partials = new List<string>();

        await ReverseInputRunner.RunAsync(
            Run(), new ScriptedBackend("Hello"), (_, text) => partials.Add(text), CancellationToken.None);

        Assert.All(partials, text => Assert.NotEqual(string.Empty, text));
    }

    [Fact]
    public async Task A_failure_comes_back_with_its_exception_and_message()
    {
        var boom = new TranslationFailedException("服务响应超时。");

        var outcome = await ReverseInputRunner.RunAsync(
            Run(), new ScriptedBackend("Hel") { ThrowAfterPieces = boom }, onPartial: null, CancellationToken.None);

        Assert.Equal(TranslationState.Failed, outcome.State);
        Assert.Same(boom, outcome.Failure);
        Assert.Equal("服务响应超时。", outcome.Error);
    }

    [Fact]
    public async Task A_cancelled_run_comes_back_cancelled_and_not_as_a_failure()
    {
        using var cancellation = new CancellationTokenSource();

        var outcome = await ReverseInputRunner.RunAsync(
            Run(),
            new ScriptedBackend("一", "二", "三"),
            (_, _) => cancellation.Cancel(),
            cancellation.Token);

        Assert.Equal(TranslationState.Cancelled, outcome.State);
        Assert.Null(outcome.Failure);
    }

    // --- 把结局交给会话 -----------------------------------------------------------------

    private static (ReverseInputSession Session, ReverseRun Run) Running(TestClock? clock = null)
    {
        clock ??= new TestClock(Start);
        var session = new ReverseInputSession(clock);
        session.Open(PromptTemplates.Standard);
        session.TextChanged("你好");
        clock.Advance(ReverseInputSession.Debounce);
        return (session, session.Tick().Run!);
    }

    [Fact]
    public async Task A_finished_run_is_delivered_as_completed_and_pastes_when_Enter_was_pressed()
    {
        var (session, run) = Running();
        session.Enter();

        var outcome = await ReverseInputRunner.RunAsync(
            run, new ScriptedBackend("Hel", "lo"), onPartial: null, CancellationToken.None);
        var step = ReverseInputRunner.Deliver(session, outcome);

        Assert.Equal("Hello", step.Paste);
    }

    [Fact]
    public async Task A_failed_run_is_delivered_as_failed()
    {
        var (session, run) = Running();

        var outcome = await ReverseInputRunner.RunAsync(
            run,
            new ScriptedBackend { ThrowAfterPieces = new TranslationFailedException("连接翻译服务失败：x") },
            onPartial: null,
            CancellationToken.None);
        ReverseInputRunner.Deliver(session, outcome);

        Assert.Equal(ReverseInputStatus.Failed, session.Status);
        Assert.IsType<TranslationFailedException>(session.Failure);
    }

    [Fact]
    public async Task A_cancelled_run_delivers_nothing()
    {
        var (session, run) = Running();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var outcome = await ReverseInputRunner.RunAsync(
            run, new ScriptedBackend("Hello"), onPartial: null, cancellation.Token);
        var step = ReverseInputRunner.Deliver(session, outcome);

        Assert.Equal(default, step);
        Assert.Equal(ReverseInputStatus.Running, session.Status);
    }

    [Fact]
    public async Task A_late_outcome_from_a_superseded_run_changes_nothing()
    {
        var clock = new TestClock(Start);
        var (session, stale) = Running(clock);

        session.TextChanged("你好吗");
        clock.Advance(ReverseInputSession.Debounce);
        var current = session.Tick().Run!;

        var outcome = await ReverseInputRunner.RunAsync(
            stale, new ScriptedBackend("Hello"), onPartial: null, CancellationToken.None);
        ReverseInputRunner.Deliver(session, outcome);

        Assert.Equal(ReverseInputStatus.Running, session.Status);
        Assert.NotEqual(stale.Seq, current.Seq);
        Assert.NotEqual("Hello", session.Output);
    }

    [Fact]
    public async Task A_rewrite_is_delivered_and_stops_for_review()
    {
        var session = new ReverseInputSession(new TestClock(Start));
        session.Open(PromptTemplates.PromptOptimize);
        session.TextChanged("帮我写一个脚本");
        var run = session.Enter().Run!;

        var outcome = await ReverseInputRunner.RunAsync(
            run,
            new ScriptedBackend("Write a script.\n\nNotes:\n- stream it"),
            onPartial: null,
            CancellationToken.None);
        var step = ReverseInputRunner.Deliver(session, outcome);

        // 改写类不剥"Notes:"（它可能就是正文），也不自动贴：停下等第二次 Enter。
        Assert.Null(step.Paste);
        Assert.Equal("Write a script.\n\nNotes:\n- stream it", session.Output);
        Assert.Equal("Write a script.\n\nNotes:\n- stream it", session.Enter().Paste);
    }
}
