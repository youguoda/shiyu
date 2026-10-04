using System.ComponentModel;
using System.Diagnostics;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 热键与两个全局钩子（O-40 拆自 App.xaml.cs）：热键方案来自 Core 的
/// <see cref="HotkeyPlan"/>（O-27），注册表整体随每次设置保存重建（O-20）
/// ——退役的注册表会大声拒绝再干活（O-43）。Win+V 接管与拖选徽标的鼠标
/// 钩子同属"全局的、用户点过头的才装"这一族，随设置即时装卸。
/// </summary>
internal sealed class HotkeyModule
{
    private AppShell? _shell;
    private HotkeyRegistry? _registry;
    private WinVHook? _winV;
    private MouseDragHook? _mouseDrag;

    /// <summary>当前注册表——面板经 shell 的取用口拿它（O-43）。</summary>
    public HotkeyRegistry Current => _registry!;

    public void Attach(AppShell shell)
    {
        _shell = shell;

        _registry = new HotkeyRegistry(shell.MessageWindow);
        shell.Hotkeys = () => Current;
        RegisterHotkeys();
        ApplyWinVTakeover();
        ApplySelectionBadge();

        // Hotkeys are dropped and taken again as a set: working out which
        // individual ones changed would be more code than redoing all four.
        shell.SettingsChanged += _ =>
        {
            _registry?.Dispose();
            _registry = new HotkeyRegistry(shell.MessageWindow);
            RegisterHotkeys();
            ApplyWinVTakeover();
            ApplySelectionBadge();
        };
    }

    private void RegisterHotkeys()
    {
        var shell = _shell!;

        // A probe owns no global keys (票 15): they belong to the user's real
        // instance, and the guard also covers the re-registration that a
        // settings save would otherwise trigger.
        if (shell.IsProbe)
        {
            Trace.WriteLine("probe mode: hotkeys/hooks off (RegisterHotkeys skipped)");
            return;
        }

        // The plan (parsing, pairwise collisions, unreadable settings) comes
        // from Core (O-27): the same verdict the settings window and the
        // onboarding guide show the user, applied here at registration.
        // Actions go through the shell's relay slots — by the time a key can
        // physically fire, the host has wired them.
        var actions = new Dictionary<HotkeyAction, Action>
        {
            // Ctrl+Shift+Z: deliberately a combination whose modifiers the
            // user is still holding when it fires, so the released-modifier
            // handling in the capture platform is exercised every single time
            // rather than only in some configurations.
            [HotkeyAction.CaptureSelection] = () => shell.TranslateSelection?.Invoke(),

            // Ctrl+Shift+V sits next to the paste the user already knows.
            // 票 26 之后它呼出的是粘贴模式的窄条——选一条、贴、消失（ADR-
            // 0012 #8 的两种呼出意图之一）。
            [HotkeyAction.QuickBar] = () => shell.ShowQuickPaste?.Invoke(),

            // The resident narrow bar: summoned and hidden by the same key.
            [HotkeyAction.Bar] = () => shell.ToggleBar?.Invoke(),

            // The escape hatch. Without it the user cannot tell a filter that
            // judged wrongly from a tool that broke, and has no way to insist.
            [HotkeyAction.ClipboardTranslate] = () => shell.TranslateClipboard?.Invoke(),

            // 打开管理窗（§5.1 新增，默认不设）：键盘重度用户不必绕托盘。
            [HotkeyAction.Library] = () => shell.ShowLibrary?.Invoke(),

            // 反向输入框（票 43，默认 Alt+Q）：在当前输入框旁呼出，打中文出英文，Enter 贴回。
            // 在 Microsoft 365 里 Alt+Q 是应用内的"跳到搜索框"——RegisterHotKey 不会报冲突，注册后
            // 会悄悄把它盖掉；设置项的说明里写明了，用户可以自己改键。
            [HotkeyAction.ReverseInput] = () => shell.ShowReverseInput?.Invoke(),
        };

        var (bindings, problems) = HotkeyPlan.Build(shell.Settings);
        foreach (var problem in problems)
        {
            // Unreadable or colliding settings, named per action. Never fatal:
            // losing a hotkey is ordinary, and the ones that parse still work.
            shell.Tray?.ShowNotification("拾语", problem);
        }

        // RegisterHotKey failures are a different thing from plan problems:
        // those are other software winning, and are reported together rather
        // than one balloon after another.
        var conflicts = new List<HotkeyConflict>();
        foreach (var binding in bindings)
        {
            var hotkey = new Hotkey(
                Translate(binding.Spec.Modifiers),
                binding.Spec.Key,
                HotkeyPlan.ActionNames[binding.Action]);
            if (_registry!.Register(hotkey, actions[binding.Action]) is { } conflict)
            {
                conflicts.Add(conflict);
            }
        }

        if (conflicts.Count > 0)
        {
            shell.Tray?.ShowNotification("拾语", string.Join("\n", conflicts.Select(c => c.Message)));
        }

        static HotkeyModifiers Translate(HotkeyModifier modifiers)
        {
            var result = HotkeyModifiers.None;
            if (modifiers.HasFlag(HotkeyModifier.Control)) result |= HotkeyModifiers.Control;
            if (modifiers.HasFlag(HotkeyModifier.Shift)) result |= HotkeyModifiers.Shift;
            if (modifiers.HasFlag(HotkeyModifier.Alt)) result |= HotkeyModifiers.Alt;
            if (modifiers.HasFlag(HotkeyModifier.Windows)) result |= HotkeyModifiers.Windows;
            return result;
        }
    }

    /// <summary>
    /// Installs or removes the Win+V takeover to match the setting, on the
    /// spot — no restart, and the unhook is immediate. The hook lives in this
    /// process, so if the process dies the key returns to Windows by itself.
    /// </summary>
    private void ApplyWinVTakeover()
    {
        var shell = _shell!;

        if (shell.IsProbe)
        {
            return;
        }

        if (shell.Settings.TakeOverWinV)
        {
            if (_winV is null)
            {
                try
                {
                    _winV = new WinVHook();

                    // Win+V 的心智是"选一条、粘贴、消失"（ADR-0012 #8）——
                    // 接管后唤起的是粘贴模式的窄条，不是常驻条。换轨常驻
                    // 的钥匙是 Ctrl+Shift+B。
                    _winV.Triggered += () => shell.ShowQuickPaste?.Invoke();
                }
                catch (Win32Exception failure)
                {
                    // 装钩失败回告（O-16）：字段保持 null——null 就是"未接管"
                    // 的唯一事实，下次进设置或重启会再试。装不上的钩子若装作
                    // 在管，用户要按下 Win+V 看到系统面板弹出那一刻才知道。
                    shell.TellUser($"未能接管 Win+V：{failure.Message}。本次运行不接管。");
                }
            }
        }
        else if (_winV is not null)
        {
            _winV.Dispose();
            _winV = null;
        }
    }

    /// <summary>
    /// 装上/摘掉划词的鼠标钩子，随设置即时生效——与 Win+V 接管同一个模式。
    /// 默认不装（票 37 的硬约束）：全局低级鼠标钩子让每一次鼠标事件都多绕
    /// 一段本进程，这笔开销只有用户自己点头才花。钩子活在本进程里，关闭
    /// 即刻摘钩，进程退出或被强杀时系统自动还原。
    /// </summary>
    private void ApplySelectionBadge()
    {
        var shell = _shell!;

        // 同热键：探针不装全局鼠标钩子（票 15）。
        if (shell.IsProbe)
        {
            return;
        }

        if (shell.Settings.SelectionBadge)
        {
            if (_mouseDrag is null)
            {
                try
                {
                    _mouseDrag = new MouseDragHook();

                    // 拖选的裁决在取词模块：钩子只转达（模块间经 shell）。
                    _mouseDrag.DragCompleted += () => shell.DragSelected?.Invoke();
                }
                catch (Win32Exception failure)
                {
                    // 同 Win+V：字段保持 null，徽标本次不生效，失败要说出来。
                    shell.TellUser($"未能开启拖选翻译徽标：{failure.Message}。本次运行不开启。");
                }
            }
        }
        else if (_mouseDrag is not null)
        {
            _mouseDrag.Dispose();
            _mouseDrag = null;
        }
    }

    /// <summary>退役次序即原 OnExit：注册表，随后两个各自住在专用线程上的钩子。</summary>
    public void Shutdown()
    {
        _registry?.Dispose();

        // 两个低级钩子各自住在专用线程上（O-16）：Dispose 投 WM_QUIT、在钩子
        // 线程上卸钩后 Join（带超时），退出路径不会因它们挂住。
        _winV?.Dispose();
        _mouseDrag?.Dispose();
    }
}
