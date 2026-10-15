#if DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 按真实模型的节奏出字的假后端（用户实录 2026-10-15：LLM 出字时输出一卡一卡、拖滚动条发涩、
/// 偶尔"未响应"）：一篇六千字上下的 Markdown（标题、段落、列表、引用、代码、表格都有），切成
/// 3–9 个字符的小片，每秒 100 片，偶尔两片挨着到（网络上常见的成串到达）。按墙钟排期而不是
/// 每片睡一下——Task.Delay 的粒度随系统计时器精度在 1 到 15.6 毫秒之间漂。
/// 由 <c>SHIYU_FAKE_BACKEND=stream</c> 打开（<see cref="DebugOverrides.FakeStream"/>）。
/// </summary>
internal sealed class DebugStreamingBackend : ITranslationBackend
{
    public const int PiecesPerSecond = 100;

    /// <summary>出字的全文（探针核对用）。</summary>
    public static string Answer { get; } = BuildAnswer();

    public async IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var random = new Random(15);
        var clock = Stopwatch.StartNew();
        var piece = 0;
        for (var at = 0; at < Answer.Length; piece++)
        {
            var due = TimeSpan.FromSeconds((double)piece / PiecesPerSecond);
            if (due > clock.Elapsed)
            {
                await Task.Delay(due - clock.Elapsed, cancellation);
            }

            var length = Math.Min(random.Next(3, 10), Answer.Length - at);
            yield return Answer.Substring(at, length);
            at += length;
        }
    }

    private static string BuildAnswer()
    {
        var text = new StringBuilder();
        for (var section = 1; section <= 9; section++)
        {
            text.Append("## Section ").Append(section).Append(": what the probe streams\n\n");
            text.Append("This paragraph stands in for a long answer from a language model. It keeps going ")
                .Append("for a few sentences, with **bold words**, *a little emphasis* and `inline code`, ")
                .Append("so that every kind of inline the renderer knows turns up while the text grows. ")
                .Append("Section ").Append(section).Append(" ends after one more sentence like this one.\n\n");
            text.Append("- first point of section ").Append(section).Append(", short\n")
                .Append("- second point, a little longer so that it wraps on a narrow panel\n")
                .Append("- third point with a [link](https://example.com/").Append(section).Append(")\n\n");

            if (section % 3 == 0)
            {
                text.Append("> A quoted remark in section ").Append(section).Append(", the way answers often cite.\n\n");
                text.Append("```csharp\nvar total = 0;\nfor (var i = 0; i < ").Append(section)
                    .Append("; i++)\n{\n    total += i;\n}\n```\n\n");
                text.Append("| Column | Value |\n| --- | --- |\n| section | ").Append(section)
                    .Append(" |\n| kind | table |\n\n");
            }
        }

        text.Append("That is the end of the streamed answer.");
        return text.ToString();
    }
}

/// <summary>
/// 探针用的确定性假后端（票 43，只在 Debug 构建存在）：返回 <c>[EN] </c> + 原文，分两段流出，
/// 既不碰网络、也不依赖任何服务的状态，端到端流程（打字 → 出译文 → Enter → 贴回）才有可断言的输出。
/// 由 <c>SHIYU_FAKE_BACKEND=1</c> 打开（<see cref="DebugOverrides.FakeBackend"/>）。
/// </summary>
internal sealed class DebugFakeBackend : ITranslationBackend
{
    public const string Prefix = "[EN] ";

    public async IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return Prefix;

        // 让出一拍：流式的形状（先出前缀、再出正文）与真实后端一致。
        await Task.Delay(30, cancellation);
        yield return request.Text;
    }
}

/// <summary>
/// 与假后端同一个开关的假词典（用户需求 2026-10-13：单词态也能换模板）：给一张只有一条义项的
/// 卡、不碰网络，并记下查了几次——改写类模板下不查、切回翻译类时补查、查过的不重查，探针都要
/// 数得出来。计数是进程级的：每个探针都是新进程。
/// </summary>
internal sealed class DebugFakeDictionary : IDictionaryApi
{
    private static int _lookups;

    public static int Lookups => Volatile.Read(ref _lookups);

    public Task<DictionaryCard?> LookupAsync(string word, CancellationToken cancellation = default)
    {
        Interlocked.Increment(ref _lookups);
        return Task.FromResult<DictionaryCard?>(
            new DictionaryCard(word, "/probe/", [new DictionarySense("n.", ["probe sense"], [], [])]));
    }
}
#endif
