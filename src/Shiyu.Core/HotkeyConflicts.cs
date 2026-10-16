namespace Shiyu.Core;

/// <summary>
/// 常见应用里另有用处的组合键（评审 2026-10-16 F-04）。拾语运行期间全局键归拾语所有，会盖掉其他应用里
/// 的同名快捷键——RegisterHotKey 不会报这种冲突。这里列的是目标用户（开发、办公、聊天）每天会撞上的
/// 那些，录键时就地说一句；不拦，用户可以照样用。
/// </summary>
public static class HotkeyConflicts
{
    private static readonly (string Hotkey, string Use)[] Known =
    [
        ("Ctrl+Shift+V", "浏览器、聊天软件、Windows 终端里的「粘贴为纯文本」或「粘贴」"),
        ("Ctrl+Shift+Z", "很多编辑器里的「重做」"),
        ("Ctrl+Shift+X", "VS Code 的「扩展」面板"),
        ("Alt+Q", "Microsoft 365（Word、Excel、PowerPoint、Outlook）的「跳到搜索框」"),
        ("Ctrl+Alt+V", "Word、Excel 的「选择性粘贴」"),
        ("Ctrl+Alt+Z", "QQ 的「提取消息」"),
        ("Ctrl+Alt+A", "QQ 的截图"),
        ("Alt+A", "微信的截图"),
        ("Ctrl+Shift+A", "钉钉、飞书的截图"),
    ];

    /// <summary>这个组合在常见应用里另有什么用处；不认识的给 null。按解析后的键比，写法不同也认得。</summary>
    public static string? UseElsewhere(string hotkey)
    {
        if (HotkeySpec.Parse(hotkey) is not { } spec)
        {
            return null;
        }

        foreach (var (known, use) in Known)
        {
            if (HotkeySpec.Parse(known) == spec)
            {
                return use;
            }
        }

        return null;
    }

    /// <summary>录键处的那一句：认识的冲突点名，不认识的什么都不说。</summary>
    public static string? Warning(string hotkey)
        => UseElsewhere(hotkey) is { } use
            ? $"这个组合在别的应用里另有用处：{use}。拾语运行期间它归拾语。"
            : null;
}
