using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// The value half of the settings tree: what each id reads from and writes to
/// on <see cref="AppSettings"/>. The renderer never knows a setting exists;
/// adding one is a schema leaf plus a case here, and both are data-shaped.
/// </summary>
internal static class SettingsBindings
{
    private const string PatternPrefix = "re:";

    /// <summary>An item's current value as editor text; null for "nothing to show".</summary>
    public static string? ReadText(string id, AppSettings settings) => id switch
    {
        "exclusions" => string.Join(
            Environment.NewLine,
            settings.ExclusionRules.Select(rule => rule.Kind == ExclusionRuleKind.ContentPattern
                ? PatternPrefix + rule.Value
                : rule.Value)),
        "bar.actions" => string.Join(",", settings.BarActions.Select(HoverActions.Name)),
        "record.text" => "始终开启",
        "theme" => null,
        "action.sound" => null,
        "store.protect" => null,
        "store.protect-favorites" => null,
        "store.protect-pinned" => null,
        "store.start-with-windows" => null,
        "about.update-auto" => null,
        "store.retention-days" => settings.ImageRetentionDays.ToString(),
        "bar.text-lines" => settings.BarTextLines.ToString(),
        "bar.image-height" => settings.BarImageHeight.ToString(),
        "bar.file-count" => settings.BarFileCount.ToString(),
        "look.preview-hover" => settings.PreviewHoverDelayMs.ToString(),
        "hotkey.capture" => settings.CaptureHotkey,
        "hotkey.clipboard" => settings.ClipboardTranslateHotkey,
        "hotkey.quickbar" => settings.QuickBarHotkey,
        "hotkey.bar" => settings.BarHotkey,
        "hotkey.library" => settings.LibraryHotkey,
        "hotkey.reverse" => settings.ReverseInputHotkey,
        "service.target-language" => settings.TargetLanguage,
        "service.source-language" => settings.SourceLanguage ?? string.Empty,

        // 预设下拉的当前项即预设 Id；空串表示「自定义」。
        "service.preset" => settings.BackendPresetId,
        "service.base-url" => settings.BackendBaseUrl,
        "service.model" => settings.BackendModel,

        // The stored credential is never put back into an editor.
        "service.api-key" => string.Empty,
        "store.directory" => settings.DataDirectoryOverride,
        "about.version" => VersionText,
        _ => null,
    };

    /// <summary>The index a segmented control rests on, for choice-shaped items.</summary>
    public static int ReadChoice(string id, AppSettings settings) => id switch
    {
        "theme" => (int)settings.Theme,
        "look.content-size" => (int)settings.ContentFontSize,
        "service.backend-kind" => (int)settings.TranslationBackend,
        _ => 0,
    };

    /// <summary>Whether a toggle item rests on. Null for not-a-toggle.</summary>
    public static bool? ReadToggle(string id, AppSettings settings) => id switch
    {
        "action.sound" => settings.ActionSound,
        "look.lightweight" => settings.LightweightWhenHidden,
        "bar.at-cursor" => settings.BarAtCursor,
        "look.bar-topmost" => settings.BarAlwaysOnTop,
        "record.images" => settings.RecordImages,
        "record.files" => settings.RecordFiles,
        "winv.takeover" => settings.TakeOverWinV,
        "hotkeys.selection-badge" => settings.SelectionBadge,
        "bar.card-tooltips" => settings.BarCardTooltips,
        "look.preview-on-hover" => settings.PreviewOnHover,
        "translate.auto-copy" => settings.AutoCopyTranslation,
        "store.protect" => settings.ProtectEntries,
        "store.protect-favorites" => settings.ProtectFavorites,
        "store.protect-pinned" => settings.ProtectPinned,
        "about.update-auto" => settings.UpdateAutoCheck,
        _ => null,
    };

    /// <summary>
    /// Writes one edited value into a growing settings copy. Text is what
    /// editors naturally hold; each case turns it into its typed shape.
    /// </summary>
    public static AppSettings Apply(string id, AppSettings current, string text, int choice) => id switch
    {
        "exclusions" => current with { ExclusionRules = ParseExclusionRules(text) },
        "bar.actions" => ParseBarActions(text) is { Count: > 0 } actions
            ? current with { BarActions = actions }
            : current,
        "theme" => current with { Theme = (AppTheme)choice },
        "look.content-size" => current with { ContentFontSize = (ContentFontSize)choice },
        "service.backend-kind" => current with { TranslationBackend = (TranslationBackendKind)choice },
        "action.sound" => current with { ActionSound = AsBool(text) },
        "bar.at-cursor" => current with { BarAtCursor = AsBool(text) },
        "look.bar-topmost" => current with { BarAlwaysOnTop = AsBool(text) },
        "look.lightweight" => current with { LightweightWhenHidden = AsBool(text) },
        "record.images" => current with { RecordImages = AsBool(text) },
        "record.files" => current with { RecordFiles = AsBool(text) },
        "winv.takeover" => current with { TakeOverWinV = AsBool(text) },
        "hotkeys.selection-badge" => current with { SelectionBadge = AsBool(text) },
        "bar.card-tooltips" => current with { BarCardTooltips = AsBool(text) },
        "look.preview-on-hover" => current with { PreviewOnHover = AsBool(text) },
        "translate.auto-copy" => current with { AutoCopyTranslation = AsBool(text) },
        "store.protect" => current with { ProtectEntries = AsBool(text) },
        "store.protect-favorites" => current with { ProtectFavorites = AsBool(text) },
        "store.protect-pinned" => current with { ProtectPinned = AsBool(text) },
        "about.update-auto" => current with { UpdateAutoCheck = AsBool(text) },
        "store.start-with-windows" => current with { StartWithWindows = AsBool(text) },
        "store.retention-days" => current with { ImageRetentionDays = int.Parse(text) },
        "bar.text-lines" => current with { BarTextLines = int.Parse(text) },
        "bar.image-height" => current with { BarImageHeight = int.Parse(text) },
        "bar.file-count" => current with { BarFileCount = int.Parse(text) },
        "look.preview-hover" => current with { PreviewHoverDelayMs = int.Parse(text) },
        "hotkey.capture" => current with { CaptureHotkey = text },
        "hotkey.clipboard" => current with { ClipboardTranslateHotkey = text },
        "hotkey.quickbar" => current with { QuickBarHotkey = text },
        "hotkey.bar" => current with { BarHotkey = text },
        "hotkey.library" => current with { LibraryHotkey = text },
        "hotkey.reverse" => current with { ReverseInputHotkey = text },
        "service.target-language" => current with { TargetLanguage = text },
        "service.source-language" => current with
        {
            SourceLanguage = text.Trim() is { Length: > 0 } source ? source : null,
        },

        // 只写 Id 本身：地址与模型由各自的行写。数据层还有一道裁决
        // （ProviderPresets.ResolveFor）——手改过的地址/模型不会带着预设
        // 的附加字段发给别家，界面清空 Id 只是让用户看得见"已是自定义"。
        "service.preset" => current with { BackendPresetId = text.Trim() },
        "service.base-url" => current with { BackendBaseUrl = text },
        "service.model" => current with { BackendModel = text },

        // Blank means keep: the user should not have to retype a secret to
        // change an unrelated setting. A typed key is written together with
        // the service address it was saved under (票 29) — never alone.
        "service.api-key" => text.Length > 0 ? current.WithApiKey(text) : current,
        "store.directory" => current with { DataDirectoryOverride = text.Trim() },
        _ => current,
    };

    private static bool AsBool(string text) => text == "1";

    /// <summary>
    /// 动作清单文本 → 顺序化 Id 列表（票 23 前这是保存时的窗口私有逻辑，
    /// 即改即生效后它成了单字段落盘的一部分，搬进绑定层）。列表 UI 只会
    /// 产出合法名字，但旧设置文件里可能存着任何手打内容——认不出的静默
    /// 丢弃是"清单悄悄变短"，所以空结果返回 null，调用方保持旧值。
    /// </summary>
    public static List<string>? ParseBarActions(string text)
    {
        var byName = HoverActions.All.ToDictionary(HoverActions.Name, StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var raw in text.Split([',', '，', '、'], StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (byName.TryGetValue(raw, out var id) && !result.Contains(id))
            {
                result.Add(id);
            }
        }

        return result.Count > 0 ? result : null;
    }

    // --- 只写改过的项（O-20） ---------------------------------------------------
    //
    // 设置窗和引导窗都攥着一份开窗快照：保存时若把所有项重放一遍，别处
    // 在此期间改过的字段就会被旧值覆盖（S1–S4 的共同根源）。以下两个
    // 成员把"哪些项真的改了"变成数据：对照快照逐项比较，未变的项不动。

    /// <summary>
    /// 一项的编辑值是否仍与快照一致。一致的项在保存时不写——让别处的
    /// 修改活下来。
    /// </summary>
    public static bool IsUnchanged(string id, AppSettings baseline, ItemState state) => id switch
    {
        "theme" => state.Choice == (int)baseline.Theme,
        "service.backend-kind" => state.Choice == (int)baseline.TranslationBackend,

        // 这个开关的基线是 Windows 的现状而非文件值（两者可能不一致，
        // 而 Windows 是事实）。既有保存行为是每次都照 Windows 现状写回
        // 文件——保持不变，别让"没碰过的开关"在保存后反向改写注册表。
        "store.start-with-windows" => false,

        _ when ReadToggle(id, baseline) is { } on => state.Text == (on ? "1" : "0"),
        _ => ReadText(id, baseline) is { } text
            && string.Equals(text.Trim(), state.Text.Trim(), StringComparison.Ordinal),
    };

    /// <summary>
    /// 把编辑器状态变成"在最新设置上只应用改过项"的增量函数。
    /// </summary>
    public static Func<AppSettings, AppSettings> ChangedOnly(
        AppSettings baseline, IReadOnlyDictionary<string, ItemState> states)
        => current =>
        {
            var updated = current;
            foreach (var (id, state) in states)
            {
                if (IsUnchanged(id, baseline, state))
                {
                    continue;
                }

                updated = Apply(id, updated, state.Text, state.Choice);
            }

            return updated;
        };

    internal static List<StoredExclusionRule> ParseExclusionRules(string text)
        => text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith(PatternPrefix, StringComparison.OrdinalIgnoreCase)
                ? new StoredExclusionRule(ExclusionRuleKind.ContentPattern, line[PatternPrefix.Length..].Trim())
                : new StoredExclusionRule(ExclusionRuleKind.SourceApp, line))
            .Where(rule => rule.Value.Length > 0)
            .ToList();

    /// <summary>
    /// 关于页的版本展示走 InformationalVersion（带 -rc1 尾巴），不是三段式
    /// 的 AssemblyVersion——rc 用户得能看见自己装的是 rc（票 09）。
    /// </summary>
    public static string VersionText => "v" + UpdateService.CurrentDisplay;
}

/// <summary>
/// 语言项的显示名 ↔ 存储值（§3.9 P1：语言用下拉）。存储词汇是设置文件
/// 的英文短名（English…），显示词汇走 <see cref="LanguageDisplay"/> 的中文。
/// 认不出的值原样进出：绝不替用户换成最近似的一种。
/// </summary>
internal static class LanguageOptions
{
    public const string AutoDetect = "自动检测";

    private static readonly (string Value, string Display)[] Map =
    [
        ("Chinese", "中文"),
        ("English", "英语"),
        ("Japanese", "日语"),
        ("Korean", "韩语"),
        ("Russian", "俄语"),
        ("French", "法语"),
        ("German", "德语"),
        ("Spanish", "西班牙语"),
    ];

    /// <summary>显示名 → 存储值；「自动检测」= 空串；认不出的显示名原样返回。</summary>
    public static string ToValue(string display)
        => display == AutoDetect
            ? string.Empty
            : Map.FirstOrDefault(pair => pair.Display == display) is { } known
                ? known.Value
                : display;

    /// <summary>存储值 → 显示名；空值（自动检测）只有源语言会遇到，映射回占位。</summary>
    public static string ToDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return AutoDetect;
        }

        return Map.FirstOrDefault(pair => pair.Value == value) is { } known
            ? known.Display
            : LanguageDisplay.Name(value);
    }

    /// <summary>
    /// 系统显示语言对应的译文语言存储值（§5.3：引导的译文语言默认取系统）。
    /// 不在支持列表里的语言给 null——保持现值，绝不硬换。
    /// </summary>
    public static string? FromSystemUi()
        => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "zh" => "Chinese",
            "en" => "English",
            "ja" => "Japanese",
            "ko" => "Korean",
            "ru" => "Russian",
            "fr" => "French",
            "de" => "German",
            "es" => "Spanish",
            _ => null,
        };
}
