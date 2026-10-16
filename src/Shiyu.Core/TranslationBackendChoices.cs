namespace Shiyu.Core;

/// <summary>
/// 设置里「翻译方式」分段的顺序（评审 2026-10-16 F-05）：只有免费引擎与自备密钥两段——公共通道
/// 上线条件迟迟未满足，一个占着首位、永远点不动的选项只会让人多想一次"我的文本到底发给谁"，界面上
/// 不再显示（<see cref="RelayBackend"/> 与服务端代码保留、冻结，ADR-0009）。
///
/// 分段存下标、设置存枚举，两端靠这张表对上。原来靠"分段顺序 = 枚举声明顺序"，删掉一段就会错位：
/// 选「免费引擎」会悄悄存成别的。
/// </summary>
public static class TranslationBackendChoices
{
    public static IReadOnlyList<TranslationBackendKind> Order { get; } =
        [TranslationBackendKind.Free, TranslationBackendKind.OwnKey];

    /// <summary>分段上的名字，与 <see cref="Order"/> 一一对应。</summary>
    public static IReadOnlyList<string> Labels { get; } = ["免费引擎", "自备密钥"];

    /// <summary>
    /// 设置里存的值落在哪一段。存着公共通道的老设置（早期版本、或没配密钥而留下的）落在自备密钥上——
    /// 公共通道没上线时，它走的本来就是自备密钥那条路（<see cref="AppSettings.BuildTranslationBackend"/>）。
    /// </summary>
    public static int IndexOf(TranslationBackendKind kind)
    {
        var shown = kind == TranslationBackendKind.Relay ? TranslationBackendKind.OwnKey : kind;
        for (var index = 0; index < Order.Count; index++)
        {
            if (Order[index] == shown)
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>某一段对应的翻译方式；下标越界按第一段（免费引擎）算。</summary>
    public static TranslationBackendKind At(int index)
        => index >= 0 && index < Order.Count ? Order[index] : Order[0];
}
