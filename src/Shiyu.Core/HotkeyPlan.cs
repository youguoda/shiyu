namespace Shiyu.Core;

/// <summary>What one of Shiyu's global hotkeys makes happen.</summary>
public enum HotkeyAction
{
    /// <summary>划词翻译：抓前台选中文字并翻译。</summary>
    CaptureSelection,

    /// <summary>快速粘贴：以粘贴模式呼出窄条（票 26 合并后唯一的轻量呼出意图）。</summary>
    QuickBar,

    /// <summary>唤出/收起常驻窄条。</summary>
    Bar,

    /// <summary>翻译剪贴板内容。</summary>
    ClipboardTranslate,

    /// <summary>打开历史管理窗（默认不设，§5.1）。</summary>
    Library,

    /// <summary>反向输入框（票 43）：在当前输入框旁呼出，打中文出英文，Enter 贴回原处。</summary>
    ReverseInput,
}

/// <summary>One hotkey that parsed and survived deduplication, ready to register.</summary>
public sealed record HotkeyBinding(HotkeyAction Action, HotkeySpec Spec);

/// <summary>
/// 把设置里的热键字符串（五个动作加管理窗，票 43 起再加反向输入，共六个槽位）变成一份
/// 注册方案（O-27 下沉候选 3）。
///
/// 这曾是三份各自为政的实现：设置窗校验四键互异、引导只校验三个（漏了
/// 快速粘贴——用户把窄条设成 Ctrl+Shift+V 时，保存照常通过，随后 App 注册
/// 失败，托盘误报"已被其他软件占用"，而占住它的是拾语自己的快速粘贴）、
/// App 注册循环各写各的解析。现在三处都问这一份：
///
/// - 解析各键（<see cref="HotkeySpec.Parse"/>，裸键缺修饰键在此就被拒绝）；
/// - 键间互撞逐对点名——问题文案说清是哪两个动作撞了哪一个组合；
/// - 能注册的照常返回，坏一个不赔上其余的。
/// </summary>
public static class HotkeyPlan
{
    /// <summary>动作的中文名，注册失败与冲突文案都用它指名道姓。</summary>
    public static readonly IReadOnlyDictionary<HotkeyAction, string> ActionNames =
        new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.CaptureSelection] = "划词翻译",
            [HotkeyAction.QuickBar] = "快速粘贴",
            [HotkeyAction.Bar] = "窄条",
            [HotkeyAction.ClipboardTranslate] = "翻译剪贴板",
            [HotkeyAction.Library] = "打开管理窗",
            [HotkeyAction.ReverseInput] = "反向输入",
        };

    /// <summary>
    /// Builds the registration plan for the hotkeys in the settings.
    /// Bindings come back in the order Shiyu registers them; Problems are
    /// plain Chinese sentences, each naming the action (or the two actions)
    /// it is about.
    ///
    /// 空串是"不设"，不是错误（打开管理窗默认不设，§5.1）：清掉一个键
    /// 应当是合法的卸载，而不是一条开机就响的报错。
    /// </summary>
    public static (IReadOnlyList<HotkeyBinding> Bindings, IReadOnlyList<string> Problems) Build(
        AppSettings settings)
    {
        // Fixed order — the registration order the App has always used, so a
        // collision still resolves the same way it did (first one wins).
        var slots = new (HotkeyAction Action, string? Text)[]
        {
            (HotkeyAction.CaptureSelection, settings.CaptureHotkey),
            (HotkeyAction.QuickBar, settings.QuickBarHotkey),
            (HotkeyAction.Bar, settings.BarHotkey),
            (HotkeyAction.ClipboardTranslate, settings.ClipboardTranslateHotkey),
            (HotkeyAction.Library, settings.LibraryHotkey),
            (HotkeyAction.ReverseInput, settings.ReverseInputHotkey),
        };

        var problems = new List<string>();
        var parsed = new List<(HotkeyAction Action, HotkeySpec Spec)>();

        foreach (var (action, text) in slots)
        {
            // Empty means deliberately unset — no binding, no complaint.
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var spec = HotkeySpec.Parse(text);
            if (spec is null)
            {
                // Parse rejects bare keys (no modifier) as well as unreadable
                // text, so "缺修饰键" is covered here rather than as a second
                // rule that could drift from the first.
                problems.Add(
                    $"{ActionNames[action]}的快捷键「{text.Trim()}」无法识别，"
                    + "需要形如 Ctrl+Shift+Z 且至少带一个修饰键。");
                continue;
            }

            parsed.Add((action, spec));
        }

        // Every colliding pair is named, both sides: "第二个永远不会生效" is
        // only actionable when the user is told which two to separate.
        for (var i = 0; i < parsed.Count; i++)
        {
            for (var j = i + 1; j < parsed.Count; j++)
            {
                if (parsed[i].Spec == parsed[j].Spec)
                {
                    problems.Add(
                        $"{ActionNames[parsed[i].Action]}与{ActionNames[parsed[j].Action]}"
                        + $"的快捷键都是「{parsed[i].Spec}」，后注册的那个永远不会生效——请改掉其中一个。");
                }
            }
        }

        // First registered wins, as RegisterHotKey always resolved it — but
        // here the loser is left out rather than registered to fail, so the
        // collision is reported once, by name, instead of a second time as a
        // misleading "taken by other software".
        var bindings = new List<HotkeyBinding>();
        var seen = new HashSet<HotkeySpec>();
        foreach (var (action, spec) in parsed)
        {
            if (seen.Add(spec))
            {
                bindings.Add(new HotkeyBinding(action, spec));
            }
        }

        return (bindings, problems);
    }
}
