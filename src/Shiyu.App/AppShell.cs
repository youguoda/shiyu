using System.IO;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 模块们的共享根（O-40）：store、消息窗口、托盘这些人人要用的东西挂在这
/// 里，功能模块之间不互相认识——要找彼此，经下面的中转位。
///
/// 设置的唯一写入口仍是 store（O-20）：一切读走 <see cref="Settings"/>，一
/// 切写走 <see cref="TryUpdateSettings"/>——此前每个写入方攥着整份快照各自
/// 写回，最后保存的一方获胜。变更经 <see cref="SettingsChanged"/> 广播给各
/// 模块自己的 Apply；原来那条单体应用函数拆散在这里与各模块里。
/// </summary>
internal sealed class AppShell
{
    public required SettingsStore SettingsStore { get; init; }
    public required MessageWindow MessageWindow { get; init; }
    public required EntryStore Store { get; init; }
    public required AppIconCache Icons { get; init; }
    public required FileTypeIcons FileIcons { get; init; }

    /// <summary>Shared file-existence verdicts for every renderer (O-36).</summary>
    public required FileExistenceCache FileProbe { get; init; }

    public required ImageArchive Images { get; init; }
    public required ThemeManager Theme { get; init; }

    /// <summary>Rules are swapped in on the live policy object (see ApplyPipelineSettings).</summary>
    public required ExclusionPolicy Exclusions { get; set; }

    /// <summary>
    /// 探针模式（票 15）：全局的东西——热键、钩子、剪贴板监听、更新检查——
    /// 都让开，让探针实例和用户正在用的实例并排跑、互不打扰。
    /// </summary>
    public required bool IsProbe { get; init; }

    /// <summary>剪贴板写入与取词平台：装配晚期就位，窗口构造前完备。</summary>
    public WindowsClipboardWriter? Writer { get; set; }

    public WindowsCapturePlatform? CapturePlatform { get; set; }

    public SelectionCapture? Capture { get; set; }

    /// <summary>托盘——托盘模块装配。装配前 <see cref="TellUser"/> 静默（与原 null 字段同义）。</summary>
    public TrayIcon? Tray { get; set; }

    /// <summary>剪贴板监听与流水线：探针实例没有——合成的数据从不来自真实剪贴板。</summary>
    public WindowsClipboardMonitor? Clipboard { get; set; }

    public ClipboardPipeline? Pipeline { get; set; }

    /// <summary>生效设置，永远是 store 的最新值。</summary>
    public AppSettings Settings => SettingsStore.Current;

    /// <summary>单一变更广播（O-20）：各模块订阅自己的 Apply，宿主装配完成时 <see cref="Connect"/>。</summary>
    public event Action<AppSettings>? SettingsChanged;

    /// <summary>
    /// 热键注册表刚被整体重建、新热键已经装好（票 41）。面板持有的作用域 Esc 靠它在新
    /// 注册表上重挂——重挂必须排在重建之后：订阅 SettingsChanged 的话次序全靠订阅序，
    /// 而这个事件由热键模块在重建完成那一刻才发，次序是构造出来的，不是约定出来的。
    /// </summary>
    public event Action? HotkeysRebuilt;

    /// <summary>热键模块在注册表重建完成后调用。</summary>
    public void NoteHotkeysRebuilt() => HotkeysRebuilt?.Invoke();

    /// <summary>上一次已应用的设置：靠它分辨"数据位置是否刚被改过"。</summary>
    private AppSettings _applied = new();

    /// <summary>挂起的数据位置搬迁提示：压轴的订阅者来取（原单体里垫底的 movedData）。</summary>
    private bool _pendingDataMove;

    // --- 跨模块中转位：宿主装配完成时接线，模块经它们找彼此 ---

    public Action? ToggleBar { get; set; }

    /// <summary>快速粘贴（票 26）：以粘贴模式呼出窄条——贴完/失焦即隐的呼出意图。</summary>
    public Action? ShowQuickPaste { get; set; }
    public Action? TranslateSelection { get; set; }
    public Action? TranslateClipboard { get; set; }

    /// <summary>一次拖选完成（UI 线程）：热键模块的钩子把事件转给取词模块。</summary>
    public Action? DragSelected { get; set; }

    /// <summary>翻译面板：文本 + 面板上屏那一刻的一次性回调（票 37 的债转手）。</summary>
    public Action<string, Action?>? ShowPanel { get; set; }

    public Action<string, DeferredCapture?>? ShowBadge { get; set; }
    public Action<string>? OpenSettingsAt { get; set; }
    public Action? ShowLibrary { get; set; }
    public Action? ShowSettings { get; set; }
    public Action? ShowUpdateWindow { get; set; }
    public Func<IStreamingModel>? BuildStreamingModel { get; set; }

    /// <summary>窄条被呼出/收起（热键或托盘菜单）。引导「试一试」靠它打勾（§5.3）。</summary>
    public event Action? BarSummoned;

    /// <summary>从窄条粘贴了一条（Enter 或编号键）。引导「试一试」靠它打勾。</summary>
    public event Action? BarPasted;

    /// <summary>窄条模块回放上面的两个信号（模块间经壳中转，互不认识）。</summary>
    public void NoteBarSummoned() => BarSummoned?.Invoke();

    public void NoteBarPasted() => BarPasted?.Invoke();

    /// <summary>当前热键注册表的取用口（O-43）：注册表随设置保存整体重建，面板持它而非一次性捕获。</summary>
    public Func<HotkeyRegistry>? Hotkeys { get; set; }

    /// <summary>
    /// 开始镜像 store 的变更，并记下"已应用"基线。装配路径上单线程、无人
    /// 写设置，接线时序无关紧要；此后每一次变更按订阅序广播——主题先行
    /// （换调色板会重解析每扇开着的窗口里的 DynamicResource），数据位置的
    /// 搬迁提示压轴（原单体里 movedData 垫底的次序）。
    /// </summary>
    public void Connect()
    {
        _applied = SettingsStore.Current;

        SettingsStore.Changed += updated =>
        {
            NoteApplied(updated);
            Theme.Apply(updated.Theme);
            SettingsChanged?.Invoke(updated);

            if (TakePendingDataMove())
            {
                TellUser("数据位置已更改，重启拾语后生效。");
            }
        };
    }

    /// <summary>
    /// The relay's device identity: an anonymous install id, generated once
    /// and stable for the machine's life. Persisted right away — a new id
    /// every launch would quietly double the device's daily quota draw.
    /// A failed write has nothing to show itself in yet; the id simply
    /// regenerates next launch.
    /// </summary>
    public void EnsureRelayClientId()
    {
        if (Settings.RelayClientId.Length == 0)
        {
            try
            {
                SettingsStore.Update(
                    s => s with { RelayClientId = Guid.NewGuid().ToString("N") },
                    AppPaths.SettingsFile);
            }
            catch (SettingsSaveException)
            {
            }
        }
    }

    /// <summary>
    /// 记一笔"已应用到"：<paramref name="updated"/> 与上一份的差异里若含数据
    /// 位置搬迁，挂起等压轴的提示来取——与原单体在应用函数入口先算 movedData
    /// 同义（S 系列缺陷防的是漏判，不是晚说）。
    /// </summary>
    public void NoteApplied(AppSettings updated)
    {
        _pendingDataMove |= updated.DataDirectoryOverride != _applied.DataDirectoryOverride;
        _applied = updated;
    }

    /// <summary>取走挂起的数据位置搬迁提示（单发）。</summary>
    public bool TakePendingDataMove()
    {
        var moved = _pendingDataMove;
        _pendingDataMove = false;
        return moved;
    }

    /// <summary>
    /// 设置的启动装载（O-40 拆自宿主）：密钥保护器先于一切设置 IO——首次
    /// 读取可能遇见受保护的密钥，首次保存（中转客户端 id）时若已有密钥则
    /// 必须保护（ADR-0011）。中转覆盖骑在 store 的不落盘旁路上：读得到，
    /// 文件从不认识它。探针的数据目录覆盖排在设置行之后——真实使用里
    /// 设置文件永远赢，变量只为探针而存在。
    /// </summary>
    public static (SettingsStore Store, string? QuarantineNotice) LoadSettings()
    {
        AppSettings.SecretProtector = new DpapiSecretProtector();

        var loaded = SettingsStore.Load(
            AppPaths.SettingsFile,
            DebugOverrides.RelayUrl is { } relayUrl ? s => s with { RelayEndpoint = relayUrl } : null);

        AppPaths.UseDirectory(loaded.Store.Current.DataDirectoryOverride);

        if (DebugOverrides.ProbeDirectory is { } dataDirectory)
        {
            AppPaths.UseDirectory(dataDirectory);
        }

        // An unparseable settings file was renamed aside, not overwritten:
        // that deserves one honest sentence once a tray exists to say it in.
        var quarantine = loaded.QuarantinedPath is { } quarantined
            ? "设置文件无法读取，已把原文件保留为 " + Path.GetFileName(quarantined) + "，并暂时使用默认设置。"
            : null;

        return (loaded.Store, quarantine);
    }

    /// <summary>
    /// 剪贴板监听与流水线的装配（O-40 拆自宿主）。A probe instance watches no
    /// clipboard: the user's copies must not land in the probe's database
    /// (synthetic data only, ever — that is what the screenshots are allowed
    /// to contain), and a copied English sentence must not raise a translation
    /// badge from the probe process onto the user's screen.
    /// </summary>
    public void AttachClipboardPipeline()
    {
        if (IsProbe)
        {
            return;
        }

        var clipboard = new WindowsClipboardMonitor(MessageWindow);
        var pipeline = new ClipboardPipeline(
            clipboard, Store, TimeProvider.System, Exclusions, Images,
            icons: new SourceIconCache(Store, new WindowsSourceIcons()))
        {
            RecordImages = Settings.RecordImages,
            RecordFiles = Settings.RecordFiles,
        };

        pipeline.ImageFailed += reason => TellUser($"复制的图片没能保存：{reason}");

        // 复制徽标与划词徽标共用一扇窗：取词模块经中转位接手（票 16 的账本）。
        pipeline.BadgeDeserved += text => ShowBadge?.Invoke(text, null);

        Clipboard = clipboard;
        Pipeline = pipeline;
    }

    /// <summary>剪贴板写入与取词平台就位（装配晚期，窗口构造前完备）。</summary>
    public void AttachCapture()
    {
        Writer = new WindowsClipboardWriter(MessageWindow);
        CapturePlatform = new WindowsCapturePlatform(MessageWindow, Writer);
        Capture = new SelectionCapture(CapturePlatform);
    }

    /// <summary>
    /// Writes one settings change through the store. A failed write becomes a
    /// tray event rather than an exception escaping a UI handler — and the
    /// store has already refused the change, so memory and disk still agree.
    /// </summary>
    public bool TryUpdateSettings(Func<AppSettings, AppSettings> mutate)
    {
        try
        {
            SettingsStore.Update(mutate, AppPaths.SettingsFile);
            Log.Event(LogEvent.SettingsSaved);
            return true;
        }
        catch (SettingsSaveException failure)
        {
            Log.Event(LogEvent.SettingsSaveFailed, failure);
            Tray?.ShowNotification("拾语", failure.Message);
            return false;
        }
    }

    /// <summary>
    /// 子窗口往托盘说一句话的通道：它们没有托盘引用，也不该有——界面上
    /// "用户点了却什么都没发生"的失败，配得上一句人话（O-24）。
    /// </summary>
    public void TellUser(string message) => Tray?.ShowNotification("拾语", message);

    /// <summary>带标题的托盘话（更新检查这类标题不是"拾语"的）。</summary>
    public void Notify(string title, string message) => Tray?.ShowNotification(title, message);

    /// <summary>
    /// 排除名单与录制开关的换新（原单体应用函数的第 4 段）：规则在活的策略
    /// 对象上就地换新，下一次复制就按新规则裁决。
    /// </summary>
    public void ApplyPipelineSettings(AppSettings updated)
    {
        Exclusions = updated.BuildExclusionPolicy();
        Pipeline?.UseExclusions(Exclusions);
        if (Pipeline is not null)
        {
            Pipeline.RecordImages = updated.RecordImages;
            Pipeline.RecordFiles = updated.RecordFiles;
        }
    }

    /// <summary>反向拆除——顺序即 OnExit 的原顺序：托盘、流水线、监听、消息窗、库。</summary>
    public void Dispose()
    {
        Tray?.Dispose();
        Pipeline?.Dispose();
        Clipboard?.Dispose();
        MessageWindow.Dispose();
        Store.Dispose();
    }
}
