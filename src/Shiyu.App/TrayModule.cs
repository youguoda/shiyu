using System.Linq;
using System.Windows;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 托盘与托盘菜单（O-40 拆自 App.xaml.cs）：托盘的建造、菜单事件的转达，
/// 以及托盘起来那一刻要说的两句话——坏设置文件的隔离通知、公共通道未上
/// 线的存量迁移提示。
///
/// 菜单结构按 UI 报告 §5.2 重排（票 25）：动作 + KeyMap 渲染的加速键列。
/// 曾经最显眼的 10 行灰显最近剪贴板内容整个退役——它点不了，在共享屏幕
/// 时还是一份意外的泄露清单。
/// </summary>
internal sealed class TrayModule
{
    /// <summary>菜单行的稳定标识（回传事件的 Key）。</summary>
    private const string BarKey = "bar";
    private const string QuickPasteKey = "quick-paste";
    private const string TranslateClipboardKey = "translate-clipboard";
    private const string LibraryKey = "library";
    private const string SettingsKey = "settings";
    private const string KeymapKey = "keymap";
    private const string UpdateKey = "update";
    private const string QuitKey = "quit";

    /// <summary>
    /// Builds the tray and wires its menu. <paramref name="quarantineNotice"/>
    /// is the one sentence the settings load deferred until a tray existed to
    /// say it in — said once, then dropped.
    /// </summary>
    public void Attach(AppShell shell, string? quarantineNotice)
    {
        var tray = new TrayIcon(shell.MessageWindow, "拾语")
        {
            // 每次右键都重建：设置里改了键，下一次点开就是新的列。
            Menu = () => MenuRows(shell.Settings),
        };

        shell.Tray = tray;

        // The menu is the manual entrance to everything the hotkeys also reach:
        // each item goes through the shell's relay slots, never at a module.
        tray.Command += key => Run(shell, key);

        // Left click keeps going straight to the library — the thing the user
        // most often wants (the reason it survived the §5.2 redesign).
        tray.OpenLibraryRequested += () => shell.ShowLibrary?.Invoke();

        // An unparseable settings file was renamed aside, not overwritten:
        // that deserves one honest sentence once a tray exists to say it in.
        if (quarantineNotice is { } notice)
        {
            tray.ShowNotification("拾语", notice);
        }

        // 公共通道未上线的存量迁移（票 08/ADR-0009）：选中公共通道的用户
        // 只提示这一次——有自备密钥就切过去；没有就保留选择（设置里显示为
        // 「即将推出」），翻译时由面板给配置引导卡。走 store 增量写。
        var settings = shell.Settings;
        if (settings.TranslationBackend == TranslationBackendKind.Relay
            && !settings.RelayUnavailableNoticed)
        {
            var hasOwnKey = settings.Backend.IsConfigured;
            if (shell.TryUpdateSettings(s => s with
            {
                TranslationBackend = hasOwnKey
                    ? TranslationBackendKind.OwnKey
                    : s.TranslationBackend,
                RelayUnavailableNoticed = true,
            }))
            {
                tray.ShowNotification("拾语", hasOwnKey
                    ? "公共翻译通道还未开放，已改用你自己的密钥翻译。"
                    : "公共翻译通道还未开放；可在 设置 → 翻译 改用免费引擎（无需账号），或配置自己的密钥（有免费的预设可选）。");
            }
        }
    }

    /// <summary>
    /// §5.2 的菜单结构：三个有键的动作 / 分隔 / 管理历史（设了键才带列）、
    /// 设置、键位速查 / 分隔 / 检查更新、退出。标签与加速键全部由
    /// <see cref="KeyMap"/> 从现设置渲染——改键即改菜单。
    /// </summary>
    internal static IReadOnlyList<TrayMenuRow> MenuRows(AppSettings settings)
    =>
    [
        new(BarKey, KeyMap.TrayLabel(HotkeyAction.Bar), KeyMap.Combination(HotkeyAction.Bar, settings)),
        new(QuickPasteKey, KeyMap.TrayLabel(HotkeyAction.QuickBar), KeyMap.Combination(HotkeyAction.QuickBar, settings)),
        new(TranslateClipboardKey, KeyMap.TrayLabel(HotkeyAction.ClipboardTranslate), KeyMap.Combination(HotkeyAction.ClipboardTranslate, settings)),
        new(string.Empty, null, null),
        new(LibraryKey, KeyMap.TrayLabel(HotkeyAction.Library), KeyMap.Combination(HotkeyAction.Library, settings)),
        new(SettingsKey, "设置…", null),
        new(KeymapKey, "键位速查…", null),
        new(string.Empty, null, null),
        new(UpdateKey, "检查更新…", null),
        new(QuitKey, "退出拾语", null),
    ];

    private static void Run(AppShell shell, string key)
    {
        switch (key)
        {
            case BarKey:
                shell.ToggleBar?.Invoke();
                break;
            case QuickPasteKey:
                shell.ShowQuickPaste?.Invoke();
                break;
            case TranslateClipboardKey:
                shell.TranslateClipboard?.Invoke();
                break;
            case LibraryKey:
                shell.ShowLibrary?.Invoke();
                break;
            case SettingsKey:
                shell.ShowSettings?.Invoke();
                break;
            case KeymapKey:
                // 深链进快捷键页的速查区：托盘问"键怎么按"，直接落到答案上。
                shell.OpenSettingsAt?.Invoke("hotkeys.cheatsheet");
                break;
            case UpdateKey:
                shell.ShowUpdateWindow?.Invoke();
                break;
            case QuitKey:
                Application.Current.Shutdown();
                break;
        }
    }
}
