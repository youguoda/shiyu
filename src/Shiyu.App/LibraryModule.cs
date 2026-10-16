using System.Windows;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>管理窗模块（O-40 拆自 App.xaml.cs）：一扇常驻库窗的显隐与复用。</summary>
internal sealed class LibraryModule
{
    private AppShell? _shell;
    private LibraryWindow? _library;

    /// <summary>上一次呼出历史窗口前用户所在的窗口（评审 2026-10-16 F-12「粘贴全部」贴回那里）。</summary>
    private ForegroundWindow _pasteTarget;

    public void Attach(AppShell shell)
    {
        _shell = shell;

        // 「来源图标」（用户需求 2026-10-14）：开着的管理窗当场照新设置重建各行。只认这一项，
        // 别的设置改了不打扰列表（重建会把行模型整个换掉）。
        var showIcons = shell.Settings.ShowSourceIcons;
        shell.SettingsChanged += settings =>
        {
            if (settings.ShowSourceIcons != showIcons)
            {
                showIcons = settings.ShowSourceIcons;
                _library?.ApplySourceIcons();
            }
        };
    }

    /// <summary>
    /// One library window, reused. Opening a second copy of the same history
    /// would be two views that immediately disagree with each other.
    /// </summary>
    public void Show() => ShowFor(ForegroundWindow.Current());

    /// <summary>
    /// 呼出历史窗口，并记下呼出前用户所在的窗口——「粘贴全部」（评审 2026-10-16 F-12）贴回那里。
    /// 拾语自己的窗口（窄条、托盘菜单的消息窗口、设置、历史窗口本身）不算，沿用上一次记下的；
    /// 窄条的品牌钮把它自己记着的那扇窗传进来。
    /// </summary>
    public void ShowFor(ForegroundWindow before)
    {
        var shell = _shell!;
        if (before.IsSomething && !before.BelongsToThisProcess)
        {
            _pasteTarget = before;
        }

        // 探针实例没有剪贴板写入器（刻意不接监听，票 15 的合成数据纪律），
        // 但管理窗必须能开——探针的 SHIYU_PROBE_CMD=library 正是从这里进
        // 来。写入器只被复制/拖出路径用到，传 null 与探针场景一致。
        if (_library is null)
        {
            // 动作（总结/合并/改写/建议标签）与批量翻译要的是通用模型，
            // 与面板共用同一个后端工厂（票 08）：预设的附加字段与温度
            // 规则对它们一视同仁。没配密钥时按钮在管理窗侧禁用并说明。
            _library = new LibraryWindow(
                shell.Store, shell.Writer!, shell.Images, shell.BuildStreamingModel!,
                shell.Icons, () => shell.Settings, shell.Pipeline)
            {
                // 翻译记录页关着时的空态「去设置」（用户需求 2026-10-09）。
                OpenSettingsAt = id => shell.OpenSettingsAt?.Invoke(id),
                Paster = shell.Capture,
                PasteTarget = _pasteTarget,
            };
            _library.Closed += (_, _) => _library = null;
            _library.Show();
        }
        else
        {
            _library.PasteTarget = _pasteTarget;
            _library.ReloadOnReturn();
            if (_library.WindowState == WindowState.Minimized)
            {
                _library.WindowState = WindowState.Normal;
            }

            _library.Activate();
        }
    }

    /// <summary>管理窗打开在「翻译记录」页（设置里的「查看翻译记录」）。</summary>
    public void ShowTranslationLog()
    {
        Show();
        _library?.ShowTranslationLogPage();
    }

#if DEBUG
    /// <summary>探针命令 source-icons 的管理窗一段（见 LibraryWindow.ProbeIconSlots）。</summary>
    internal string ProbeIconSlots(string phase)
        => _library?.ProbeIconSlots(phase) ?? $"lib-{phase}|missing{Environment.NewLine}";
#endif
}
