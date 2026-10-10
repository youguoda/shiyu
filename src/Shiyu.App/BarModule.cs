using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 窄条模块（O-40 拆自 App.xaml.cs）：常驻窄条与快速粘贴两种呼出意图的
/// 显隐、几何的防抖保存——几何保存进设置走 store，视觉状态从
/// ApplySettings 广播回来。票 26 之后两种意图共用同一扇 BarWindow：
/// 差别（锚插入符、贴完即隐、失焦即隐）住在窗口的粘贴模式里。
/// </summary>
internal sealed class BarModule
{
    private AppShell? _shell;
    private BarWindow? _bar;
    private System.Windows.Threading.DispatcherTimer? _geometrySave;

    public void Attach(AppShell shell)
    {
        _shell = shell;

        // The bar's density knobs take effect on the spot（原单体应用函数的
        // 一段，O-20）。
        shell.SettingsChanged += s => _bar?.ApplySettings(s);
    }

    /// <summary>
    /// Summons or hides the resident narrow bar. One instance, reused: a bar
    /// that keeps its place on screen between summons is a place the user
    /// learns to find things. The list inside does not keep its scroll —
    /// every summon opens at the newest entry (用户实录 2026-10-04).
    /// </summary>
    public void Toggle()
    {
        if (EnsureBar() is null)
        {
            return;
        }

        _bar!.Toggle();

        // 呼出与收起都算「用过这个键」：试一试的清单只问用户会不会唤起窄条。
        _shell!.NoteBarSummoned();
    }

#if DEBUG
    /// <summary>探针命令 bar-newest 的入口（见 BarWindow.ProbeStartsAtNewest）。</summary>
    internal void ProbeStartsAtNewest() => EnsureBar()?.ProbeStartsAtNewest();

    /// <summary>探针命令 preview-image 的入口（见 BarWindow.ProbePreviewImage）。</summary>
    internal void ProbePreviewImage() => EnsureBar()?.ProbePreviewImage();
#endif

    /// <summary>
    /// 快速粘贴（票 26 并入，原 QuickBarWindow 的位）：以粘贴模式呼出同一扇
    /// 窄条——一个名词、两种呼出意图（ADR-0012 #8）。窗口在首次任一意图时
    /// 建一次：一天几十次的呼出不该每次都付一扇窗的造价。
    /// </summary>
    public void ShowQuickPaste()
    {
        if (EnsureBar() is null)
        {
            return;
        }

        _bar!.SummonForPaste();
    }

    /// <summary>
    /// The one construction site（票 26 抽出：常驻与粘贴模式共用一扇窗，构造
    /// 与接线只此一份）。装配守卫同原 Toggle：没有可写剪贴板与取词平台时，
    /// 粘贴无从谈起，安静返回。
    /// </summary>
    private BarWindow? EnsureBar()
    {
        var shell = _shell!;

        if (shell.Writer is null || shell.Capture is null)
        {
            return null;
        }

        if (_bar is { } bar)
        {
            return bar;
        }

        _bar = new BarWindow(
            shell.Store, shell.Icons, shell.Writer, shell.Capture, shell.Settings,
            shell.FileIcons, shell.FileProbe);
        _bar.GeometryChanged += OnBarGeometryChanged;

        // 引导「试一试」的两个信号（票 25/§5.3）：呼出与粘贴都经壳中转，
        // 窄条不知道引导，引导不知道窄条。
        _bar.Pasted += shell.NoteBarPasted;

        // 深链进设置的数据页：条不知道设置的内部，只知道条目 Id。
        _bar.DataSettingsRequested += itemId => shell.OpenSettingsAt?.Invoke(itemId);
        _bar.DeadDragNotice += notice => shell.TellUser(notice);

        // 品牌钮打开管理窗（票 21 §6.1 第 1 行）：窄条管"拿回"，
        // 整理归管理窗。
        _bar.LibraryRequested += () => shell.ShowLibrary?.Invoke();

        // The header's pin reports only what it wants (票 39/O-20): this
        // side turns it into a one-field update through the store, and the
        // pin's visual state comes back via ApplySettings when the
        // store broadcasts — the bar never writes settings itself again,
        // so its snapshot can no longer erase anyone else's changes (S1/S2).
        _bar.TopmostWanted += wanted =>
            shell.TryUpdateSettings(s => s with { BarAlwaysOnTop = wanted });

        return _bar;
    }

    /// <summary>
    /// Geometry saves are debounced rather than per-move: a drag fires this
    /// dozens of times a second and the settings file does not deserve that.
    /// </summary>
    private void OnBarGeometryChanged()
    {
        _geometrySave ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800),
        };

        _geometrySave.Tick -= SaveBarGeometry;
        _geometrySave.Tick += SaveBarGeometry;
        _geometrySave.Stop();
        _geometrySave.Start();
    }

    private void SaveBarGeometry(object? sender, EventArgs e)
    {
        _geometrySave?.Stop();

        var shell = _shell!;
        if (_bar is null)
        {
            return;
        }

        // An unchanged geometry — the common case at shutdown — skips the
        // write entirely: every store update re-applies settings everywhere,
        // and exit has no use for that.
        var settings = shell.Settings;
        if (settings.BarLeft == _bar.BarLeft
            && settings.BarTop == _bar.BarTop
            && settings.BarHeight == _bar.BarHeight)
        {
            return;
        }

        shell.TryUpdateSettings(s => s with
        {
            BarLeft = _bar.BarLeft,
            BarTop = _bar.BarTop,
            BarHeight = _bar.BarHeight,
        });
    }

    /// <summary>原 OnExit 的中段：先落几何（与退出时完全同一函数），再关条窗。</summary>
    public void Shutdown()
    {
        SaveBarGeometry(this, EventArgs.Empty);
        _bar?.Close();
    }
}
