using System.Text;

namespace Shiyu.Core;

/// <summary>
/// 把整段译文切成按序吐出的片段，让一次性到达的答案在面板里仍有"正在到来"
/// 的流式观感。原本是 <see cref="RelayBackend"/> 的私事（上游非流式，中转一次
/// 回完）；免费引擎的网页接口同样整块返回，票 41 把它抽出来共用，不另抄一份。
///
/// 只切不删：片段拼回必须与原文一字不差，丢一个换行都是破坏。
/// </summary>
internal static class StreamingSlicer
{
    /// <summary>
    /// 一片的最大长度：无标点的长句也切成几步到达，"流式"不能在极端
    /// 形状下退化成一次性甩一大块。
    /// </summary>
    internal const int MaxPieceChars = 160;

    /// <summary>片间小步节奏：够让译文"正在到来"，不至于拖慢总和。</summary>
    internal static readonly TimeSpan PieceInterval = TimeSpan.FromMilliseconds(35);

    /// <summary>
    /// 句末标点（含引号收尾）与换行是天然的界，无标点的长句按
    /// <see cref="MaxPieceChars"/> 截断。
    /// </summary>
    internal static IReadOnlyList<string> Split(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var pieces = new List<string>();
        var builder = new StringBuilder();

        foreach (var rune in text.EnumerateRunes())
        {
            builder.Append(rune.ToString());

            if (builder.Length >= MaxPieceChars)
            {
                Close();
                continue;
            }

            if (rune.Value is '\n')
            {
                Close();
                continue;
            }

            if (IsEnder(rune))
            {
                Close();
            }
        }

        Close();
        return pieces;

        void Close()
        {
            if (builder.Length == 0)
            {
                return;
            }

            pieces.Add(builder.ToString());
            builder.Clear();
        }
    }

    /// <summary>句末标点：拉丁三件套加全角对应与省略号。小数点不在此列。</summary>
    private static bool IsEnder(Rune rune) => rune.Value switch
    {
        '.' => true,
        '!' => true,
        '?' => true,
        '。' => true,
        '！' => true,
        '？' => true,
        '…' => true,
        _ => false,
    };
}
