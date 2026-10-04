#if DEBUG
using System.Diagnostics;

namespace Shiyu.App;

/// <summary>
/// 宿主的调试探针区（票 06/15，O-40 拆出宿主文件）：只在 Debug 构建存在的
/// 启动旋钮与呼出通道。发布构建里整个文件编译为空——旋钮的读取本身住在
/// <see cref="DebugOverrides"/>（O-41）。
/// </summary>
public partial class App
{
    /// <summary>
    /// 探针决策先于互斥量（票 15）：名字就是让探针实例与用户真实例并排跑
    /// 的全部机关——同一数据目录得到同一互斥量，同目录探针仍然互斥；不同
    /// 目录、以及与用户的 <c>Shiyu</c>，永不相撞。
    /// </summary>
    private static string PrepareProbe()
    {
        if (DebugOverrides.ProbeDirectory is not { } probeDirectory)
        {
            return "Shiyu";
        }

        // Settings move into the probe directory too: reading the real ones
        // would aim the probe at the user's backend, and writing them back
        // (geometry, relay id) would reach the real file.
        AppPaths.UseProbeDirectory(probeDirectory);
        Trace.WriteLine($"probe mode: hotkeys/hooks off, data dir {probeDirectory}");
        return DebugOverrides.ProbeMutexName(probeDirectory);
    }

    /// <summary>
    /// 实机验收用的隐藏命令（票 06）：在 DispatcherTimer.Tick 里抛一个异常，
    /// 验证 UI 线程兜底——进程存活、日志一行、托盘提示一次。与
    /// <c>SHIYU_DATA_DIR</c> 同族，绝不进发布。
    /// </summary>
    private static void InstallDebugTickCrash()
    {
        if (!DebugOverrides.DebugTickCrash)
        {
            return;
        }

        var crash = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        crash.Tick += (_, _) =>
        {
            // 单发：验证的是"抛 + 存活 + 提示一次"，不是压测节流。
            crash.Stop();
            throw new InvalidOperationException("debug tick crash probe");
        };
        crash.Start();
    }

    /// <summary>
    /// 探针便利（同 SHIYU_DATA_DIR 一族）：启动即打开设置窗/更新窗，自动化
    /// 检查不必找托盘图标、可以端到端驱动手工流程。
    /// </summary>
    private void OpenDebugStartupWindows()
    {
        if (DebugOverrides.OpenSettings)
        {
            _modules?.Settings.Show();
        }

        if (DebugOverrides.OpenUpdate)
        {
            _modules?.Update.ShowWindow();
        }
    }

    /// <summary>
    /// 探针的呼出通道（票 15）：热键一颗都没注册，所以窗口只能这样开——
    /// <c>SHIYU_PROBE_CMD=bar|panel|library|settings|quickbar|reverse</c> 启动即直接
    /// 显示对应窗口，走的是和热键完全相同的内部方法。
    /// <c>SHIYU_PROBE_ITEM</c> 让设置窗直接落到某个设置项（既有深链）；
    /// <c>SHIYU_PROBE_TEXT</c> 给面板一句要翻译的话。仅在探针模式生效。
    /// </summary>
    private void RunProbeCommand()
    {
        if (_shell is not { } shell || !DebugOverrides.IsProbe)
        {
            return;
        }

        var text = DebugOverrides.ProbeText;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Placeholder sentence for probe 15.";
        }

        switch (DebugOverrides.ProbeCommand)
        {
            case "bar":
                shell.ToggleBar?.Invoke();
                break;

            case "quickbar":
                // 票 26 合并后 quickbar 命令映射到粘贴模式的窄条：探针仍能
                // 检"快速粘贴"这条意图，定位参数（384 DIP）在探针脚本里同步。
                shell.ShowQuickPaste?.Invoke();
                break;

            case "library":
                shell.ShowLibrary?.Invoke();
                break;

            case "settings":
                if (DebugOverrides.ProbeItem is { } item)
                {
                    _modules!.Settings.ShowAt(item);
                }
                else
                {
                    _modules!.Settings.Show();
                }
                break;

            case "panel":
                _modules!.Translation.ShowPanel(text);
                break;

            // 票 43：反向输入框。探针没有全局热键，所以直接开；脚本在此之前把记事本置于前台——
            // 框记下的"原来的窗口"就是那时的前台。配 SHIYU_FAKE_BACKEND=1 用确定性的假后端。
            case "reverse":
                shell.ShowReverseInput?.Invoke();
                break;

            // 票 25：引导五屏的走查通道。探针原则上跳过首启引导（票 15），
            // 但 §5.3 的验收要真走一遍五屏——显式点名才打开，与 settings
            // 同族；数据落在本探针目录里，走完即记 OnboardingCompleted。
            case "onboarding":
                new OnboardingWindow(shell.SettingsStore, firstRun: true, shell).Show();
                break;
        }
    }
}
#endif
