using System.Runtime.CompilerServices;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 会话在后台线程上读流、界面在 UI 线程上读字（用户实录 2026-10-15：原来整条流都在 UI 线程上读，
/// 每到一片当场整篇重排，LLM 出字时界面卡到"未响应"）。片在锁里追加，<see cref="TranslationSession.Text"/>
/// 随时可从别的线程读，读到的是某一刻完整的一份；没有新片时不再重拼、重洗。
/// </summary>
public class TranslationSessionThreadingTests
{
    /// <summary>一个字一个字地出，每片之间让出一次，读的一方有机会插进来。</summary>
    private sealed class TrickleBackend(string text) : ITranslationBackend
    {
        public async IAsyncEnumerable<string> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            foreach (var character in text)
            {
                await Task.Yield();
                yield return character.ToString();
            }
        }
    }

    private static readonly string Answer = string.Concat(Enumerable.Range(0, 400).Select(i => (char)('a' + i % 26)));

    [Fact]
    public async Task Text_can_be_read_from_another_thread_while_the_stream_runs()
    {
        var session = new TranslationSession(new TrickleBackend(Answer));
        var reads = new List<string>();
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                reads.Add(session.Text);
            }
        });

        await Task.Run(() => session.RunAsync(new TranslationRequest("原文", "en")));
        stop.Cancel();
        await reader;

        Assert.Equal(TranslationState.Finished, session.State);
        Assert.Equal(Answer, session.Text);

        // 每一次读到的都是某一刻完整的一份：全文的一个前缀，不会有撕裂的半截。
        Assert.NotEmpty(reads);
        Assert.All(reads, read => Assert.StartsWith(read, Answer, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reading_twice_without_a_new_piece_gives_the_same_text_without_rebuilding_it()
    {
        var session = new TranslationSession(new TrickleBackend("hello"));
        await session.RunAsync(new TranslationRequest("原文", "en"));

        Assert.Same(session.Text, session.Text);
    }

    [Fact]
    public async Task Every_update_sees_all_the_text_that_arrived_before_it()
    {
        var session = new TranslationSession(new TrickleBackend("streamed"));
        var seen = new List<string>();
        session.Updated += () => seen.Add(session.Text);

        await session.RunAsync(new TranslationRequest("原文", "en"));

        Assert.Equal(["", "s", "st", "str", "stre", "strea", "stream", "streame", "streamed", "streamed"], seen);
    }
}
