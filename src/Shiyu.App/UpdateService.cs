using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 更新通道的 App 侧薄壳（票 09）：取清单、下载、验签、解压的编排都在
/// Core 的 <see cref="UpdateOrchestrator"/> 里（可单测）；这里只剩离不开
/// 这个进程的两件事——报告当前版本，以及把已暂存的更新交给第二个进程
/// 去替换安装目录（一个运行中的应用换不了自己的文件）。
/// </summary>
internal sealed class UpdateService
{
    private readonly UpdateOrchestrator _orchestrator;
    private readonly string _dataDirectory;

    public UpdateService(string dataDirectory, UpdateChannel? channel = null, HttpClient? http = null)
    {
        _dataDirectory = dataDirectory;
        _orchestrator = new UpdateOrchestrator(
            http ?? HttpClients.Shared,
            dataDirectory,
            channel,
            userAgent: $"Shiyu/{Current.Text}");
    }

    /// <summary>
    /// 比较用的版本号：AssemblyVersion 的前三段。它存不下 -rc1 这类尾巴，
    /// 恰好也是比较时想要的——0.9.0-rc1 在新老比较里就是 0.9.0。
    /// </summary>
    public static UpdateVersion Current
        => UpdateVersion.Parse(Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)) ?? new UpdateVersion();

    /// <summary>
    /// 给用户看的版本：InformationalVersion 带着 -rc1 尾巴（AssemblyVersion
    /// 存不下它），源链接补在 + 后面的提交哈希不展示。丢了这个尾巴，rc 用户
    /// 在关于页看到的就是一个说谎的 0.9.0。
    /// </summary>
    public static string CurrentDisplay
    {
        get
        {
            var informational = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            var text = informational?.Split('+')[0];
            return text is { Length: > 0 } ? text : Current.Text;
        }
    }

    /// <summary>The staged swap has this many attempts to see the old process out.</summary>
    private static readonly TimeSpan OldProcessTimeout = TimeSpan.FromSeconds(15);

    public Task<UpdateCheck> CheckAsync(CancellationToken cancel = default)
        => _orchestrator.CheckAsync(cancel);

    public Task<(bool Ok, string Error)> DownloadAsync(
        ReleaseManifest release, IProgress<double>? progress, CancellationToken cancel)
        => _orchestrator.DownloadAsync(release, progress, cancel);

    /// <summary>
    /// Starts the staged copy with the finalizer command and exits this
    /// instance. The two-phase shape is what keeps the old version bootable:
    /// the swap only happens once this process is gone, and only after a
    /// backup exists.
    /// </summary>
    public void ApplyAndRestart()
    {
        var stagedExe = Path.Combine(UpdateStaging.StagedDirectory(_dataDirectory), "Shiyu.App.exe");
        Trace($"apply: staged exists={File.Exists(stagedExe)}");
        if (!File.Exists(stagedExe))
        {
            return;
        }

        // Trailing backslashes trimmed on purpose: BaseDirectory ends with
        // one, and a backslash directly before a closing quote escapes it in
        // Windows command-line parsing — the finalizer would then receive a
        // mangled argument list and fall through as a normal application.
        var installDirectory = AppContext.BaseDirectory.TrimEnd('\\');
        var backup = Path.Combine(UpdateStaging.Root(_dataDirectory), "backup").TrimEnd('\\');

        var arguments =
            $"--finalize-update {Environment.ProcessId} " +
            $"\"{installDirectory}\" \"{backup}\" \"{_dataDirectory.TrimEnd('\\')}\"";

        try
        {
            // using：记下 pid 后立即还句柄（O-43）。Dispose 不杀进程——
            // 交接出去的 finalizer 自己要活到替换完成。
            using var finalizer = Process.Start(
                new ProcessStartInfo(stagedExe, arguments) { UseShellExecute = false });
            Trace($"apply: finalizer pid={(finalizer is null ? "null" : finalizer.Id.ToString())}");
        }
        catch (Exception failure)
        {
            // 安装没能交接出去（O-24）：用户点了"安装"却停在原地，这里没法
            // 从服务够到窗口——留给日志与托盘兜底去说。
            Log.Event(LogEvent.UpdateApplyFailed, failure, ("stage", 1));
            Trace("apply: start failed " + failure.Message);
            return;
        }

        System.Windows.Application.Current.Shutdown();
    }

    private static void Trace(string line)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shiyu-apply.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException)
        {
            // expected: 落 trace 失败——trace 自己不能成为它要记录的故障。
        }
    }

    /// <summary>
    /// The finalizer entry, run by the staged copy before any of the
    /// application starts: swap the install, restart the real application,
    /// and never show a window. True when the arguments were a finalizer call
    /// and the process should now exit.
    ///
    /// Arguments: <c>--finalize-update &lt;old pid&gt; &lt;install dir&gt; &lt;backup dir&gt; &lt;data dir&gt;</c>
    /// </summary>
    public static bool TryRunFinalizer(string[] arguments)
    {
        if (arguments.Length != 5 || arguments[0] != "--finalize-update"
            || !int.TryParse(arguments[1], out var oldProcessId))
        {
            return false;
        }

        var installDirectory = arguments[2];
        var backup = arguments[3];
        var staged = AppContext.BaseDirectory;
        var dataRoot = arguments[4];

        var outcome = UpdateApply.Finalize(
            staged,
            installDirectory,
            backup,
            oldProcessId,
            pid => WaitForExit(pid, OldProcessTimeout));

        Trace($"finalize: outcome={outcome} staged={staged} install={installDirectory}");

        if (outcome == ApplyOutcome.Applied)
        {
            try
            {
                // Launched before the staging reset on purpose: the reset
                // removes this process's own files, and everything after it
                // must already be loaded. 打开即弃（O-43）。
                using var restarted = Process.Start(new ProcessStartInfo(
                    Path.Combine(installDirectory, "Shiyu.App.exe")) { UseShellExecute = true });
            }
            catch (Exception failure) when (
                failure is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // expected: 新版本已装好，只是没能在这里代为启动——用户
                // 手上仍有它可点。
            }

            try
            {
                // Everything except the staged directory this process runs
                // from — that one is the relaunched application's first chore.
                UpdateStaging.CleanAfterApply(dataRoot);
            }
            catch (Exception failure) when (
                failure is IOException or UnauthorizedAccessException)
            {
                // expected: 暂存目录的残余是杂物，从来不是启动问题。
            }
        }

        return true;
    }

    private static bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // Already gone — that is the good case.
            return true;
        }
    }
}
