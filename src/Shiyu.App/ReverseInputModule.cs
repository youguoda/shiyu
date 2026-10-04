using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 反向输入框模块（票 43）：窗口的建造与呼出。一扇窗、复用——一天里要呼出很多次，每次都付一扇窗的
/// 造价不值；窗口在首次呼出时建一次（同窄条、面板）。
///
/// 后端走现有的 <see cref="AppSettings.BuildTranslationBackend"/>：自备密钥 → 大模型流式输出；
/// 免费引擎（票 41）→ 分块逐段输出、模板不生效。反向输入框照样能用——按 ADR-0013 默认用免费引擎的新用户，
/// 不配任何东西也能用。
/// </summary>
internal sealed class ReverseInputModule
{
    private AppShell? _shell;
    private ReverseInputWindow? _window;

    public void Attach(AppShell shell)
    {
        _shell = shell;

        // 运行时模板跟随设置（只在它自己的默认模板或共用的循环真的变了时才被覆盖）。
        shell.SettingsChanged += settings => _window?.ApplySettings(settings);
    }

#if DEBUG
    /// <summary>探针命令 reverse-select 的入口（见 ReverseInputWindow.ProbeSelectableOutput）。</summary>
    internal async void ProbeSelectableOutput(string text)
    {
        Show();
        if (_window is null)
        {
            return;
        }

        var directory = DebugOverrides.ProbeDirectory!;
        var log = await _window.ProbeSelectableOutput(text, System.IO.Path.Combine(directory, "reverse-select.png"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "reverse-select.log"), log + "done" + Environment.NewLine);
    }
#endif

    /// <summary>
    /// 热键、托盘与探针共用的呼出。装配守卫同窄条：没有可写剪贴板与取词平台时，回贴无从谈起，安静返回。
    /// 一切失败都收敛成一句托盘提示——热键处理器里逃出去的异常不该带走进程。
    /// </summary>
    public void Show()
    {
        var shell = _shell!;

        if (shell.Writer is null || shell.Capture is null)
        {
            return;
        }

        try
        {
            _window ??= new ReverseInputWindow(
                shell.Settings,
                BuildBackend,
                new ReversePaste(new WindowsReversePasteClipboard(shell.MessageWindow, shell.Writer), shell.Capture),
                () => shell.OpenSettingsAt?.Invoke("service.preset"),
                shell.TellUser,
                // 「自动复制译文」（用户需求 2026-10-05）：经 shell 找翻译模块存，与面板同一处。
                (original, translated) => shell.KeepTranslation?.Invoke(original, translated));
            _window.Summon();
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.TranslationFailed, failure, ("reverse", 1));
            shell.TellUser("反向输入框没能打开，已记录到日志。");
        }
    }

    private ITranslationBackend BuildBackend()
    {
#if DEBUG
        // 探针（票 43）：确定性的假后端，返回 "[EN] " + 原文——不碰网络，端到端流程可断言。
        if (DebugOverrides.FakeBackend)
        {
            return new DebugFakeBackend();
        }
#endif

        return _shell!.Settings.BuildTranslationBackend();
    }

    /// <summary>应用退出：窗口先关，在途请求取消、没还的剪贴板还回去。</summary>
    public void Shutdown() => _window?.CloseForGood();
}
