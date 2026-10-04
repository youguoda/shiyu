namespace Shiyu.Core;

/// <summary>一种语言在两家网页引擎里各自的码。</summary>
public sealed record FreeEngineLanguage(string Bing, string Tencent);

/// <summary>
/// 免费引擎的语言码表（票 41）。名字的归一与 <see cref="LanguageDisplay"/> 共用同一
/// 套别名（english / 英文 / 英语 …），所以设置里存的英文短名、先验给的短名、
/// 界面上选的中文名都认；认不出的名字返回 null——绝不替用户猜一种。
///
/// 八种语言恰是设置里「译文语言」「源语言」两个下拉的全部选项（有测试钉住这条
/// 不变量）。必应的中文是 zh-Hans，腾讯的是 zh；其余六种两家同码。码值由网络
/// 探针各验一条：tools/probes/probe-free-engines.ps1 里有同一张表的副本，
/// 改这里要一起改那边。
/// </summary>
public static class FreeEngineLanguages
{
    private static readonly IReadOnlyDictionary<string, FreeEngineLanguage> ByDisplayName =
        new Dictionary<string, FreeEngineLanguage>(StringComparer.Ordinal)
        {
            ["中文"] = new("zh-Hans", "zh"),
            ["英语"] = new("en", "en"),
            ["日语"] = new("ja", "ja"),
            ["韩语"] = new("ko", "ko"),
            ["俄语"] = new("ru", "ru"),
            ["法语"] = new("fr", "fr"),
            ["德语"] = new("de", "de"),
            ["西班牙语"] = new("es", "es"),
        };

    /// <summary>名字 → 两家的码；码表里没有（含空值与「自动检测」）则为 null。</summary>
    public static FreeEngineLanguage? Find(string? name)
        => LanguageDisplay.Name(name) is { Length: > 0 } display
            && ByDisplayName.TryGetValue(display, out var language)
                ? language
                : null;
}
