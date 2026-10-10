using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// The narrow bar (ticket 12): about 360px wide, no system chrome,
/// topmost, dragged by its background and resized by a grip, summoned at the
/// caret by the quick-paste hotkey — its only entrance since 用户需求
/// 2026-10-10 — and gone after the paste unless pinned (常驻钉住). It serves
/// the dozens-of-times-a-day quick reads; the full library window stays for
/// the weekly tidy and is untouched by this.
///
/// List structure follows the issue 02 spike: pinned cards stacked above the
/// scrolling region (visually identical to overlaying, no scroll sync), main
/// list virtualized with recycling so ten thousand entries live on a handful
/// of containers.
/// </summary>
internal partial class BarWindow : Window
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(220);
    private const string AnyTag = "全部";

    private readonly EntryStore _store;
    private readonly AppIconCache _icons;
    private readonly WindowsClipboardWriter _clipboard;
    private readonly SelectionCapture _capture;
    private readonly FileTypeIcons _fileIcons;

    /// <summary>
    /// The shared file-existence verdicts (O-36): every renderer — cards,
    /// the preview panel — reads this instead of the disk, and background
    /// probes fill it. Shared with the preview window on purpose: a path
    /// probed for a card is a path the preview never has to wait for.
    /// </summary>
    private readonly FileExistenceCache _fileProbe;

    private readonly HistoryBrowser _browser;
    private readonly ObservableCollection<BarCard> _pinned = [];
    private readonly ObservableCollection<BarCard> _cards = [];
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce;

    // --- preview panel (ticket 17) ------------------------------------------------
    // The policy decides when the preview opens, follows, and closes; this
    // window supplies the events and owns the timers. Time-based decisions
    // each arm one single-shot timer for exactly the pending deadline (O-37),
    // so a bar that is merely visible asks the scheduler for nothing.

    private PreviewWindow? _preview;

    private PreviewPolicy _previewPolicy = new(500, () => Environment.TickCount64);

    /// <summary>
    /// 显隐与刷新的判定（票 16 / O-27 下沉候选 5）：何时重读、何时进轻量、
    /// 一条 Changed 该不该触发刷新，都由这台 Core 状态机裁定；本窗只剩
    /// "把命令应用到窗口"的 <see cref="RunRefresh"/> 一层。
    /// </summary>
    private BarRefreshPolicy _refreshPolicy = new(true);

    private System.Windows.Threading.DispatcherTimer? _previewTick;

    private BarCard? _selected;
    private AppSettings _settings;

    /// <summary>
    /// 贴回哪扇窗。呼出那一刻记一次；钉住的窄条一直开着，用户在别的窗口里
    /// 打了一阵字再回来点卡片，要贴回的是他刚才所在的那扇——点下窄条的那一刻
    /// 再记一次（<see cref="NoteClickActivation"/>）。
    /// </summary>
    private ForegroundWindow _returnTo;

    /// <summary>
    /// 一次呼出正开着（用户需求 2026-10-10 起只有快速粘贴一种呼出）。贴完、
    /// 失焦收不收看 <see cref="AppSettings.BarPinned"/>：没钉住即贴即走、点别处
    /// 即隐；钉住了都不收。Enter/编号粘贴、Esc、筛选与键帽钉不钉同一套——差别
    /// 只有"什么时候走"。隐藏路径先清它：Hide() 会让窗口失去激活，失焦处理不能
    /// 把正在结束的这一次当成再隐藏一次的理由。
    /// </summary>
    private bool _summoned;

    /// <summary>Raised when the window is moved or resized; the owner persists geometry, throttled its own way.</summary>
    public event Action? GeometryChanged;

    /// <summary>一条从窄条粘贴出去了（Enter 或编号键）。引导「试一试」靠它打勾（票 25）。</summary>
    public event Action? Pasted;

    /// <summary>
    /// 窄条头部的图钉想要的钉住状态（票 39 / O-20 的做法，用户需求 2026-10-10
    /// 起是「常驻钉住」）：只上报一个布尔值，由拥有者经 SettingsStore 落一个
    /// 字段。窗口自己不写设置——它手里那份快照曾经能把别人的改动整份抹掉
    /// （S1/S2）。生效走 <see cref="ApplySettings"/> 回流。
    /// </summary>
    public event Action<bool>? PinWanted;

    /// <summary>头部的齿轮：打开设置。窗口只上报意愿，拥有者决定落到哪一页。</summary>
    public event Action? SettingsRequested;

    public BarWindow(
        EntryStore store,
        AppIconCache icons,
        WindowsClipboardWriter clipboard,
        SelectionCapture capture,
        AppSettings settings,
        FileTypeIcons fileIcons,
        FileExistenceCache? fileProbe = null)
    {
        InitializeComponent();

        _store = store;
        _icons = icons;
        _clipboard = clipboard;
        _capture = capture;
        _settings = settings;
        _fileIcons = fileIcons;
        _fileProbe = fileProbe ?? new FileExistenceCache();
        _browser = new HistoryBrowser(store);

        _searchDebounce = new System.Windows.Threading.DispatcherTimer { Interval = SearchDelay };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter();
        };

        // A resident window goes stale: copies keep arriving while it sits
        // open. The store says so now, the moment a write lands — the old
        // two-second count probe could only see the count, so a re-copied
        // entry Touching its way back to the top never showed (O-37).
        _store.Changed += OnStoreChanged;

        // 预览窗是独立顶层窗，不是本窗的视觉孩子：本窗无论经哪条路
        // 不可见或关闭（Alt+F4、系统收窗、未来新增的隐藏路径），它都
        // 不能比宿主活得久（用户实录：窄条关了预览弹窗留在桌面上）。
        // Dismiss/失焦路径已各自清理；这里是结构兜底，不替代它们。
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
            {
                _preview?.TakeDown();
            }
        };
        Closed += (_, _) =>
        {
            _preview?.TakeDown();
            _preview?.Close();
        };

        _previewPolicy = PreviewPolicy.For(settings, () => Environment.TickCount64);
        _refreshPolicy = new BarRefreshPolicy(settings.LightweightWhenHidden);

        // 在不在条里，决定一条外部写要不要把列表带回最新（见 StoreChanged）。
        // 预览窗不抢激活，悬停预览不算离开。
        Activated += (_, _) => _refreshPolicy.Activated();
        Deactivated += (_, _) => _refreshPolicy.Deactivated();

        // Win11 material behind the sheet (ticket 30): the window went layered
        // in XAML, which is the only surface the backdrop renders on. The
        // shell contract rides along (ticket 20): DWM draws the contour, and
        // a system that will not draws the fallback chrome instead.
        Backdrop.AttachShell(this, Shell, () => BackdropKind.Acrylic);

        // Kind is a segmented control now: four chips, one index. Each chip
        // carries its own ←→ cap（就地替换选中字形，票 21/U-07）.
        _kindChips = [KindChipAll, KindChipText, KindChipImage, KindChipFiles];
        _kindCaps = [KindCapAll, KindCapText, KindCapImage, KindCapFiles];
        SetKindIndex(0);

        // 键位即数据（§5.2/O-42）：按住 Ctrl 浮出的键帽与 tooltip 里的组合键
        // 全部从 KeyMap 来——改表即改帽，XAML 里的字只是占位。帽上文本取
        // 短形（Ctrl 正被按住时搜索帽只写 F），tooltip 取完整键。
        SearchKeyBadge.Content = KeyMap.BadgeText("search");
        FavoriteKeyBadge.Content = KeyMap.BadgeText("favorite-filter");
        TagKeyBadge.Content = KeyMap.BadgeText("tag");
        var kindCap = KeyMap.BadgeText("kind");
        foreach (var cap in _kindCaps)
        {
            cap.Content = kindCap;
        }

        FavoriteOnly.ToolTip = $"只看收藏（{KeyMap.BadgeText("favorite-filter")}）";
        TagFilter.ToolTip = $"标签筛选（{KeyMap.BadgeText("tag")} 循环）";
        KindChipAll.ToolTip = $"全部（{KeyMap.BadgeText("kind")} 切换类型）";
        SearchBox.ToolTip = $"搜索（{KeyMap.BadgeText("search")} 聚焦）";

        PinnedList.ItemsSource = _pinned;
        Cards.ItemsSource = _cards;

        // Always in front while it is up（用户需求 2026-10-10：「保持在最前」
        // 并进了钉住）——没钉住时它只在用户正用着它的时候开着，钉住了就该一直
        // 看得见。钉住本身是设置，不是心情（票 39）：文件怎么说，头部就怎么画。
        Topmost = true;
        SyncPinChrome();

        // 点下没激活的窄条那一刻，前台还是用户刚才所在的窗口：钉住的窄条贴回去
        // 靠这一刻记下的那扇窗。
        SourceInitialized += (_, _) =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)
                ?.AddHook(NoteClickActivation);

        RestoreGeometry();
        RefreshTagChoices();
        RefreshGroups();
        Rebuild();
    }

    /// <summary>New settings apply on the spot: density clamps change, hotkey label follows.</summary>
    public void ApplySettings(AppSettings settings)
    {
        // Geometry saves travel through the settings store now too (O-20),
        // and they arrive here like any other change; a mere move must not
        // rebuild the cards — that would also drop the selection.
        var affectsLayout =
            settings.BarTextLines != _settings.BarTextLines
            || settings.ContentFontSize != _settings.ContentFontSize
            || settings.BarImageHeight != _settings.BarImageHeight
            || settings.BarFileCount != _settings.BarFileCount
            || settings.BarCardTooltips != _settings.BarCardTooltips
            || !settings.BarActions.SequenceEqual(_settings.BarActions);
        var pinChanged = settings.BarPinned != _settings.BarPinned;

        _settings = settings;

        // A changed dwell or a disabled hover takes effect on the next event;
        // a preview already up keeps its own rules until it closes.
        _previewPolicy = PreviewPolicy.For(settings, () => Environment.TickCount64);
        _refreshPolicy.ApplySettings(settings.LightweightWhenHidden);
        ArmPreviewTick();
        if (pinChanged)
        {
            SyncPinChrome();

            // 在设置页把钉住关掉时窄条开着、却不是前台：没钉住的窄条只在用户
            // 正用着它时开着，那就照失焦的规矩收起。用头部图钉关掉时它正是
            // 前台，留到下一次点别处。
            if (!settings.BarPinned && _summoned && IsVisible && !IsActive)
            {
                HideAfterFocusLost();
            }
        }

        if (affectsLayout)
        {
            Rebuild();
        }
    }

    // --- the pin (用户需求 2026-10-10，接替票 39 的置顶钮) ------------------------

    /// <summary>
    /// The header pin = 常驻钉住: pinned, the bar stays after a paste and when
    /// the user clicks elsewhere; unpinned, it is gone after either. It is a
    /// mode, not a layer: it never joins the Esc stack, and Esc hides a pinned
    /// bar like any other. Only the wish is reported — the change lands
    /// through the store and returns via ApplySettings, so a failed save
    /// never leaves the pin asserting something untrue.
    /// </summary>
    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        PinWanted?.Invoke(!_settings.BarPinned);
    }

    /// <summary>
    /// The pin's face: a filled accent pin (E841) while pinned, the hollow pin
    /// (E718) in the button's own quiet colour otherwise. 卡片的"条目置顶"在
    /// 悬停托盘里只用 E718/E77A 两态，从不出现实心那一枚——U-19 要的"一个字形
    /// 一个意思"靠这一枚实心图钉与它所在的位置（头部，不在卡片上）。
    /// </summary>
    private void SyncPinChrome()
    {
        if (_settings.BarPinned)
        {
            PinGlyph.Text = "\uE841";
            PinGlyph.SetResourceReference(ForegroundProperty, "Brush.Accent");
            PinToggle.ToolTip = "常驻钉住：已开启。贴完、点别处都不收起，Esc 收起；点击取消";
        }
        else
        {
            PinGlyph.Text = "\uE718";
            PinGlyph.ClearValue(ForegroundProperty);
            PinToggle.ToolTip = "常驻钉住：点击后窄条贴完、点别处都不收起";
        }
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    /// <summary>
    /// 品牌钮（§6.1 第 1 行）：单击打开管理窗——窄条是"拿回"的入口，整理
    /// 归管理窗。窗口只上报意愿，打开由拥有者（BarModule → AppShell.
    /// ShowLibrary）执行。
    /// </summary>
    public event Action? LibraryRequested;

    private void OnBrandClicked(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke();

    /// <summary>
    /// 品牌钮两用（用户需求 2026-10-05）：按住即拖动整扇窄条；松手时窗口
    /// 没有位移的这一次按点击算——打开管理窗。HTCAPTION 的系统移动循环
    /// 而非 DragMove：DragMove 吞掉抬起，点击与拖动无法在同一枚钮上区分；
    /// 这也不是第二条 DragMove（背景拖动仍是一处，见 OnBackgroundPressed）。
    /// </summary>
    private void OnBrandPressed(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        WindowRects.TryGet(handle, out var before);
        var point = PointToScreen(e.GetPosition(this));
        WindowRects.RunCaptionDrag(handle, (int)point.X, (int)point.Y);
        if (!WindowRects.TryGet(handle, out var after)
            || (after.Left == before.Left && after.Top == before.Top))
        {
            LibraryRequested?.Invoke();
        }
    }

    private void Select(BarCard? card)
    {
        if (_selected == card)
        {
            return;
        }

        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = card;

        if (card is not null)
        {
            card.IsSelected = true;
        }
    }

    // --- showing and hiding ----------------------------------------------------

    /// <summary>
    /// 呼出窄条（快速粘贴；用户需求 2026-10-10 起唯一的呼出意图）：出现在我要
    /// 贴的地方。锚点取文本插入符——键盘呼出时用户正打字的地方，窄条左上角
    /// 落在这一行的正下方——取不到退回鼠标指针尖。搜索词清零（会话永远从全部
    /// 历史开始，系统 Win+V 的心智），搜索框预聚焦，↑↓/Enter/编号粘贴；贴完、
    /// 失焦收不收看「常驻钉住」。Shown at full opacity — per the motion rule,
    /// fade-ins from transparency never composite on WPF windows.
    ///
    /// 已经开着（钉住了，或者这一次还没结束）就不挪：窗口留在用户放的地方。
    /// 会话照样从头开始——呼出之后直接 Enter 贴的永远是最新的那条，钉不钉
    /// 都是这一个手感。
    /// </summary>
    public void SummonForPaste()
    {
        // The caret and the return target must be read before this window
        // activates itself: after Activate the foreground is us and neither
        // query would ever answer again. An open bar may itself be the
        // foreground, which is never where a paste should go back to.
        var stayPut = IsVisible;
        var foreground = ForegroundWindow.Current();
        if (stayPut)
        {
            NoteReturnTarget(foreground);
        }
        else
        {
            _returnTo = foreground;
        }

        var anchor = stayPut
            ? (ScreenRect?)null
            : ScreenGeometry.CaretBounds() ?? BarPlacement.Pointer(ScreenGeometry.CursorPosition());
        _summoned = true;

        // A fresh session starts unfiltered. Clearing arms the debounce; the
        // summon reads now instead (the same disarm the old quick bar did),
        // and the filter's query follows the box without a second rebuild —
        // Shown's reload is the one and only.
        SearchBox.Clear();
        _searchDebounce.Stop();
        _browser.Query = string.Empty;
        UpdateFilterChrome();

        // Whatever changed while hidden was ignored for a reason: showing
        // again reads the world as it is now, in one go, from the newest
        // entry (the policy's Shown verdict, O-37). 会话从"全部历史"开始也
        // 意味着从最新的那条开始（验收 B2 实录 2026-10-05、用户实录 2026-10-04）。
        RunRefresh(_refreshPolicy.Shown());
        if (anchor is { } corner)
        {
            PlaceBeside(corner);
        }

        ShowFocused();
    }

    /// <summary>The summon's tail: show at full opacity, take the keyboard, teach once.</summary>
    private void ShowFocused()
    {
        Show();
        TakeKeyboard();

        // First-use teaching: the interactions are good but invisible — the
        // footer mentions them for the first few summons, then never again.
        if (FirstUseHints.ShowOnSummon)
        {
            FirstUseHints.RegisterSummon();
            ShowFirstUseHint();
        }
    }

    /// <summary>Takes the foreground and aims the search box, its query selected so typing replaces it.</summary>
    private void TakeKeyboard()
    {
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// 记下贴回哪扇窗——窄条自己除外：钉住的窄条正是前台时再按一次快速粘贴，
    /// 前台就是它；把它记成"用户原来所在的窗口"，贴就贴进了自己的搜索框。
    /// </summary>
    private void NoteReturnTarget(ForegroundWindow candidate)
    {
        if (candidate.IsSomething
            && candidate.Handle != new System.Windows.Interop.WindowInteropHelper(this).Handle)
        {
            _returnTo = candidate;
        }
    }

    private const int WmMouseActivate = 0x21;

    /// <summary>
    /// WM_MOUSEACTIVATE 先于激活到达：此刻前台还是用户刚才所在的窗口（用户需求
    /// 2026-10-10，常驻钉住）。钉住的窄条一直开着，用户在别处打了一阵字再回来
    /// 点卡片——贴回的应是这扇窗，不是很久以前呼出那一刻的那扇。只记不拦：
    /// 激活照常发生。
    /// </summary>
    private IntPtr NoteClickActivation(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate)
        {
            NoteReturnTarget(ForegroundWindow.Current());
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The placement body（票 26 抽出）：落点规则在 <see cref="BarPlacement"/>
    /// （Core）——左上角对准锚点（插入符那一行，取不到就是鼠标指针尖），放不下
    /// 才挪，挪也只挪到刚好放得下。
    /// </summary>
    private void PlaceBeside(ScreenRect anchor)
    {
        var corner = new ScreenPoint(anchor.Left, anchor.Top);
        var workArea = ScreenGeometry.WorkAreaAt(corner);

        // The scale comes from the monitor itself (GetDpiForMonitor), not from
        // WPF: on the first summon the PresentationSource does not exist yet,
        // and its silent 1.0 fallback made Place see DIU-sized dimensions —
        // no flip, no clamp, the bar's real bottom off the work area (ticket
        // 30's screenshot probe).
        var (scaleX, scaleY) = ScreenGeometry.ScaleAt(corner);

        // Declared Width/Height, never Actual*: nothing has been laid out on
        // the first summon, so the Actual values are 0.
        var width = Width;
        var height = Math.Min(Height, workArea.Height / scaleY);
        var placed = BarPlacement.Place(
            anchor,
            (int)Math.Ceiling(width * scaleX),
            (int)Math.Ceiling(height * scaleY),
            workArea);

        // The very first summon happens before the window was ever shown, when
        // the handle does not exist yet — asking for it creates the HWND.
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        _ = helper.EnsureHandle();

        // The band rides with every placement (验收缺陷 A): the raw move once
        // hard-inserted HWND_TOPMOST, so a bar whose setting said "not
        // topmost" was resurrected above everything on every summon. The
        // window's own Topmost decides the band here — always on since 用户
        // 需求 2026-10-10, and the window stays the one place that says so.
        TransientWindow.MoveTo(helper.Handle, placed, ZBandPolicy.FollowsHost(Topmost));
    }

    /// <summary>
    /// Hides with the standard fade, from a painted surface, and gives focus back.
    ///
    /// The reason rides along so the policy can hold the invariant that a
    /// paste-driven hide runs the same full teardown as a toggle (票 14).
    /// </summary>
    public void Dismiss(BarHideReason reason = BarHideReason.Toggled)
    {
        // Cleared first: Hide() deactivates the window, and the Deactivated
        // handler must not read the summon that is already ending as a
        // reason to hide (and NOT restore focus) a second time.
        _summoned = false;

        RunPreviewCommand(_previewPolicy.BarHidden());

        // Instant hide, matching the instant summon. Fading a layered window
        // out reads as "text first, then a grey sheet" — the user's words —
        // and the system's Win+V panel, the benchmark surface, closes with no
        // exit either. For a 3-second tool, speed *is* the polish.
        DismissFirstUseHint();
        Hide();
        _returnTo.Restore();
        RunRefresh(_refreshPolicy.Hidden(reason));
    }

    /// <summary>
    /// Arms the single-shot preview timer for the policy's next deadline, or
    /// stands it down when nothing is pending (O-37). Called after every
    /// policy decision: the deadline that mattered a moment ago may be gone,
    /// and a new state may have just started one.
    ///
    /// The old arrangement — a 50 ms repeating tick for as long as the bar
    /// was visible — kept the process waking twenty times a second to
    /// discover nothing had happened; the acceptance bar for O-37 is a hidden
    /// bar that wakes close to never, and a visible idle one is held to the
    /// same standard.
    /// </summary>
    private void ArmPreviewTick()
    {
        if (_previewPolicy.TimeUntilDecision() is not { } wait)
        {
            _previewTick?.Stop();
            return;
        }

        if (_previewTick is null)
        {
            _previewTick = new System.Windows.Threading.DispatcherTimer();
            _previewTick.Tick += (_, _) =>
            {
                // One-shot by design: if the decision it fired left another
                // deadline pending, RunPreviewCommand re-arms it on the way
                // out.
                _previewTick.Stop();
                RunPreviewCommand(_previewPolicy.Tick());
            };
        }

        // Zero means the deadline has already passed; a DispatcherTimer with
        // a zero interval fires at the next dispatch, which is exactly that.
        _previewTick.Interval = wait;
        _previewTick.Stop();
        _previewTick.Start();
    }

    /// <summary>
    /// 把刷新策略的裁决应用到窗口（票 16）：重读、进轻量，命令说什么做什么。
    /// 判定本身在 <see cref="BarRefreshPolicy"/>（Core）里，有它自己的单测。
    /// </summary>
    private void RunRefresh(BarRefreshCommand command)
    {
        if (command.HasFlag(BarRefreshCommand.Reload))
        {
            ReloadData();
        }

        if (command.HasFlag(BarRefreshCommand.EnterLightweight))
        {
            EnterLightweight();
        }

        // After the reload, never before: the position is about the list as
        // it now stands.
        if (command.HasFlag(BarRefreshCommand.ScrollToNewest))
        {
            ScrollToNewest();
        }
    }

    /// <summary>
    /// While hidden, the realised cards are the whole cost of the window —
    /// thumbnails decoded, rows laid out — and none of it is doing anything.
    /// Dropping them and trimming the working set costs a rebuild on the next
    /// summon, which Shown's Reload already does; recording never pauses,
    /// because the listener and the pipeline do not live here.
    ///
    /// Executed only on the policy's say-so — the LightweightWhenHidden gate
    /// lives in Core now.
    /// </summary>
    private void EnterLightweight()
    {
        // The cards being dropped are also the destination of every decode
        // and probe still in flight; their generation ends here.
        _cardBackfills.Invalidate();
        _pendingBackfills.Clear();

        _pinned.Clear();
        _cards.Clear();
        _selected = null;

        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
        MemoryTrim.WorkingSet();
    }

    // --- geometry ----------------------------------------------------------------

    public double BarLeft => Left;

    public double BarTop => Top;

    public double BarHeight => Height;

    private void RestoreGeometry()
    {
        if (_settings.BarLeft is { } left)
        {
            Left = left;
        }

        if (_settings.BarTop is { } top)
        {
            Top = top;
        }

        var work = SystemParameters.WorkArea;
        Height = Math.Clamp(_settings.BarHeight ?? 620, 320, work.Height);

        // Strand check against the WHOLE virtual desktop, not the primary
        // work area: the bar legitimately lives on either monitor of this
        // dual-screen desk, and a primary-only check yanked it back every
        // restart — the cross-screen disconnect the user reported. Two
        // monitors at different scales make the DIU→physical mapping loose,
        // so the containment test carries generous slack by design.
        var vx = SystemParameters.VirtualScreenLeft;
        var vy = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        var onScreen = Left + Width > vx && Left < vx + vw && Top + Height > vy && Top < vy + vh;

        if (!onScreen)
        {
            // A monitor layout change between sessions can strand the bar off
            // every screen; a stranded bar looks exactly like a broken hotkey.
            Left = work.Right - Width - 16;
            Top = work.Top + 16;
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);

        // 窄条被拖走时预览锚的屏幕位置即刻作废——收掉，孤悬的面板比没有
        // 面板误导得多（验收 B4 双屏拖动 20 次实录：预览留在原屏）。松手
        // 后的下一次悬停会重新开一个锚对的。第六轮的生命周期兜底覆盖了
        // 隐藏与关闭，唯独没覆盖"窗口还在、只是动了"。
        if (_preview is { IsVisible: true })
        {
            RunPreviewCommand(_previewPolicy.BarHidden());
        }

        GeometryChanged?.Invoke();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        GeometryChanged?.Invoke();
    }

    private void OnGripDrag(object sender, DragDeltaEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        Height = Math.Clamp(Height + e.VerticalChange, 320, work.Height);
    }

    // --- background dragging -------------------------------------------------

    private void OnBackgroundPressed(object sender, MouseButtonEventArgs e)
    {
        // Card presses mark themselves handled, and input controls swallow
        // their own clicks, so what reaches here is genuinely background.
        DragMove();
    }
}
