namespace Shiyu.Core;

/// <summary>一块要发给引擎的文本，与它后面紧跟的空白（块与块之间的分隔，原样保留）。</summary>
internal sealed record TextChunk(string Text, string Separator);

/// <summary>
/// 切块的结果：开头的空白、若干块，块间的分隔记在块上。<see cref="Rebuild"/> 必须
/// 与原文一字不差——只切不删。
/// </summary>
internal sealed record ChunkPlan(string Prefix, IReadOnlyList<TextChunk> Chunks)
{
    internal string Rebuild() => Prefix + string.Concat(Chunks.Select(chunk => chunk.Text + chunk.Separator));
}

/// <summary>
/// 免费引擎的分块（票 41）。必应翻 1392 字实测要 9 秒，整段等完才上屏面板就会一直
/// 空等，所以按段落与句末切块、逐块渐进输出。
///
/// 首块不超过 <see cref="FirstChunkChars"/> 字（让首字出得快），其后每块不超过
/// <see cref="ChunkChars"/> 字——两个都是常量，按探针数据调。
///
/// 在预算之内挑最靠后的断点，优先级：段落/换行 &gt; 句末 &gt; 空白 &gt; 硬切。所以一串
/// 短行会被攒进同一块（60 条短行不会变成 60 次请求），长段才会在句末断开。
/// 句末的判定刻意保守：<c>.</c> <c>!</c> <c>?</c> 后面不是空白的不算（3.14159、example.com
/// 绝不从中间切开）；全角的 。！？… 无条件算。
///
/// 空白（换行、缩进、句间空格）一律归"分隔"，不进发给引擎的块——引擎不会为一个
/// 换行白费一次请求，译文拼回时分隔再原样放回去。
/// </summary>
internal static class FreeEngineChunker
{
    /// <summary>首块上限：小，让面板的第一个字早点出来。</summary>
    internal const int FirstChunkChars = 150;

    /// <summary>其后每块的上限。</summary>
    internal const int ChunkChars = 400;

    internal static ChunkPlan Split(string text, int firstChars = FirstChunkChars, int chunkChars = ChunkChars)
    {
        var start = SkipWhitespace(text, 0);
        var prefix = text[..start];
        var chunks = new List<TextChunk>();

        while (start < text.Length)
        {
            var budget = chunks.Count == 0 ? firstChars : chunkChars;
            var end = ChooseEnd(text, start, budget);
            var next = SkipWhitespace(text, end);

            chunks.Add(new TextChunk(text[start..end], text[end..next]));
            start = next;
        }

        return new ChunkPlan(prefix, chunks);
    }

    /// <summary>
    /// 译文块之间该放什么：段落与换行的分隔原样保留；句子之间的空白按<b>译文</b>
    /// 的文字系统来——中文原文句间没有空格，译成英文要补一个，否则成了
    /// "Hello.How are you?"；英文原文句间的空格，译成中文就不该留。
    /// </summary>
    internal static string Between(string separator, string previousTranslation, string nextTranslation)
    {
        if (separator.Contains('\n'))
        {
            return separator;
        }

        if (previousTranslation.Length == 0
            || nextTranslation.Length == 0
            || char.IsWhiteSpace(previousTranslation[^1])
            || char.IsWhiteSpace(nextTranslation[0])
            || IsUnspaced(previousTranslation[^1])
            || IsUnspaced(nextTranslation[0]))
        {
            return string.Empty;
        }

        return separator.Length > 0 ? separator : " ";
    }

    /// <summary>词与词之间不空格的文字：汉字、假名，以及中日文的标点与全角形式。</summary>
    private static bool IsUnspaced(char c)
        => (int)c is (>= 0x3000 and <= 0x30FF)
            or (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFF00 and <= 0xFFEF);

    /// <summary>
    /// 从 <paramref name="start"/> 起的一块，文本部分的结束位置（不含）。剩余的部分（去掉
    /// 末尾空白）放得下预算就整块拿走；否则在预算内挑最靠后的断点。
    /// </summary>
    private static int ChooseEnd(string text, int start, int budget)
    {
        var contentEnd = TrimEnd(text, start);
        if (contentEnd - start <= budget)
        {
            return contentEnd;
        }

        var limit = start + budget;
        int paragraphCut = -1, sentenceCut = -1, spaceCut = -1;

        // 空白游程的起点：块文本在这里结束，游程整个成为分隔。
        for (var i = start + 1; i <= limit && i < contentEnd; i++)
        {
            if (char.IsWhiteSpace(text[i]) && !char.IsWhiteSpace(text[i - 1]))
            {
                spaceCut = i;
                if (RunHasNewline(text, i))
                {
                    paragraphCut = i;
                }
            }
        }

        // 句末：在预算之内最靠后的一个；断点在句末标点（及其后的收尾引号括号）之后。
        for (var k = start; k < limit && k < contentEnd; k++)
        {
            if (!IsSentenceEnder(text[k]))
            {
                continue;
            }

            var after = k + 1;
            while (after < contentEnd && IsCloser(text[after]))
            {
                after++;
            }

            var settled = IsAlwaysEnder(text[k]) || after >= contentEnd || char.IsWhiteSpace(text[after]);
            if (settled && after - start <= budget)
            {
                sentenceCut = after;
            }
        }

        if (paragraphCut > start)
        {
            return paragraphCut;
        }

        if (sentenceCut > start)
        {
            return sentenceCut;
        }

        if (spaceCut > start)
        {
            return spaceCut;
        }

        // 一整段没有任何断点：硬切在预算上，但不拆开一对代理项。
        if (limit < text.Length && char.IsLowSurrogate(text[limit]) && char.IsHighSurrogate(text[limit - 1]))
        {
            limit--;
        }

        return limit > start ? limit : start + (char.IsHighSurrogate(text[start]) && start + 1 < text.Length ? 2 : 1);
    }

    private static int SkipWhitespace(string text, int from)
    {
        while (from < text.Length && char.IsWhiteSpace(text[from]))
        {
            from++;
        }

        return from;
    }

    /// <summary>从 <paramref name="start"/> 起，最后一个非空白字符之后的位置。</summary>
    private static int TrimEnd(string text, int start)
    {
        var end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return end;
    }

    private static bool RunHasNewline(string text, int runStart)
    {
        for (var i = runStart; i < text.Length && char.IsWhiteSpace(text[i]); i++)
        {
            if (text[i] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSentenceEnder(char c) => c is '.' or '!' or '?' or '。' or '！' or '？' or '…';

    /// <summary>全角句末标点无条件算句末；拉丁的要看后面是不是空白。</summary>
    private static bool IsAlwaysEnder(char c) => c is '。' or '！' or '？' or '…';

    /// <summary>句末标点后面可以紧跟的收尾：引号与括号。</summary>
    private static bool IsCloser(char c) => c is '"' or '\'' or ')' or ']' or '”' or '’' or '」' or '』' or '）' or '】' or '》';
}
