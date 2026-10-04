using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 反向输入框（票 43，Xtranslate 的"反向翻译输入法"）：在任何输入框里按热键，旁边出现一个小框，
/// 直接打中文；手一停 300ms 就出英文（或按提示词模板改写）；Enter 把结果贴回原来的输入框，Esc 关闭。
/// 正向的面板用来"看懂别人"，这个框用来"回复别人、写给 AI"。
///
/// <b>与面板相反，它接受焦点</b>：它要接收键盘输入（同窄条粘贴模式，ADR-0007）。失焦即隐藏，但
/// 显示后 300ms 内的失焦忽略，防止刚弹出就被自己触发关掉。
///
/// 这里只做薄壳：判定、状态机、回贴次序、摆放、前台兜底都在 Core（有单测）——
/// 窗口把按键、定时器与请求结果喂给 <see cref="ReverseInputSession"/>，照它回的
/// <see cref="ReverseInputStep"/> 做事，再照它的状态画界面。
/// </summary>
internal partial class ReverseInputWindow : Window
{
    /// <summary>与插入符的间隙（DIP）。翻到上方时它同时让底边避开插入符那一行。</summary>
    private const double GapDip = 20;

    /// <summary>输入框的最高（DIP，与 XAML 的 MaxHeight 同值）。</summary>
    private const double InputMaxDip = 180;

    /// <summary>外壳边距、输入框与输出之间的间距、底栏：输出区的上限 = 整窗上限 − 输入框上限 − 它们。</summary>
    private const double ChromeReserveDip = 96;

    private readonly ReverseInputSession _session = new(TimeProvider.System);
    private readonly Func<ITranslationBackend> _backend;
    private readonly ReversePaste _paste;
    private readonly Action _openSettings;
    private readonly Action<string> _tell;
    private readonly DispatcherTimer _tick = new();
    private readonly DispatcherTimer _restoreTimer = new();

    private AppSettings _settings;

    /// <summary>
    /// 用户当下选着的模板。只活在进程里，不写设置：运行中切换模板写设置会重建热键注册表、还会重报热键冲突
    /// 气泡（票 42 讲过的三个副作用）。窗口是复用的实例，下次呼出仍是这个模板；启动时取
    /// <see cref="AppSettings.ReverseInputTemplateId"/>。
    /// </summary>
    private PromptTemplate _template;

    private ForegroundWindow _returnTo;
    private ReversePlacement? _placement;
    private ScreenRect _workArea;
    private CancellationTokenSource? _inFlight;
    private PendingRestore? _pendingRestore;
    private Popup? _templateList;

    /// <summary>不出声地存一条译文（原文、译文）：「自动复制译文」开着时贴回后用。</summary>
    private readonly Action<string, string>? _keepTranslation;

    public ReverseInputWindow(
        AppSettings settings,
        Func<ITranslationBackend> backend,
        ReversePaste paste,
        Action openSettings,
        Action<string> tell,
        Action<string, string>? keepTranslation = null)
    {
        InitializeComponent();

        _settings = settings;
        _backend = backend;
        _paste = paste;
        _openSettings = openSettings;
        _tell = tell;
        _keepTranslation = keepTranslation;
        _template = PromptTemplates.ResolveReverseDefault(settings);

        Backdrop.AttachShell(this, Shell, () => BackdropKind.Acrylic);

        _tick.Tick += (_, _) =>
        {
            _tick.Stop();
            Apply(_session.Tick());
        };

        _restoreTimer.Interval = ReversePaste.RestoreDelay;
        _restoreTimer.Tick += (_, _) => FlushPendingRestore();

        // 组字状态：WPF 的 TextBox.Text 含着组字中的拼音，防抖要知道用户是不是还在选字。
        TextCompositionManager.AddPreviewTextInputStartHandler(InputBox, (_, _) => _session.SetComposing(true));
        TextCompositionManager.AddPreviewTextInputUpdateHandler(InputBox, OnCompositionUpdate);
        TextCompositionManager.AddPreviewTextInputHandler(InputBox, (_, _) => _session.SetComposing(false));
        InputBox.LostKeyboardFocus += (_, _) => _session.SetComposing(false);

        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
            {
                _tick.Stop();
                CloseTemplateList();
            }
        };
    }

    /// <summary>
    /// 设置变了：运行时模板只在它自己的默认模板或循环列表真的变了时才被覆盖，别处改了无关的设置、
    /// 或面板的默认模板，用户在输入框里切到的模板原样保留；自建模板被改了正文、被删了，跟着变。
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        _template = PromptTemplates.ReconcileReverse(_settings, settings, _template);
        _settings = settings;
    }

    // --- 呼出与收起 ---------------------------------------------------------------------

    /// <summary>
    /// 热键、托盘、探针共用的呼出：记下前台窗口 → 在插入符旁摆好 → 激活并聚焦输入框。
    /// 已经开着时再按一次就是收起（与 Esc 同：回到原窗口、不贴）。
    /// </summary>
    public void Summon()
    {
        if (IsVisible)
        {
            CancelAndClose();
            return;
        }

        // 激活之前记下前台窗口、读插入符：激活之后前台就是我们自己，GetGUIThreadInfo 再也答不出
        // 用户那边的插入符了（窄条粘贴模式同一个顺序）。托盘菜单刚收起时前台可能还是拾语自己的
        // 消息窗口——那不是"用户原来所在的窗口"，不记。
        var foreground = ForegroundWindow.Current();
        _returnTo = foreground.IsSomething && !foreground.BelongsToThisProcess ? foreground : default;
        var anchor = ScreenGeometry.CaretPosition() ?? ScreenGeometry.CursorPosition();

        // 上一次的原文、输出与方向都不带进新的一次；运行时模板沿用。
        Apply(_session.Open(_template, _settings.PromptTemplatesApply));
        InputBox.Clear();
        RenderState();

        PlaceBeside(anchor);
        Show();
        Activate();
        EnsureForeground();
        InputBox.Focus();
        Keyboard.Focus(InputBox);
    }

    /// <summary>
    /// 激活被前台锁挡下时（Activate 不保证成功）：本窗口没在前台，键盘输入就到不了输入框。
    /// 前台兜底序列正是干这个的——这里用它把自己抢到前台。已经在前台时什么都不发。
    /// </summary>
    private void EnsureForeground()
    {
        var self = new ForegroundWindow(new WindowInteropHelper(this).Handle);
        if (!self.IsForeground)
        {
            self.Restore();
        }
    }

    /// <summary>
    /// 摆放：锚点是插入符的左下角（取不到退到鼠标），位置交给 <see cref="ReverseInputPlacement"/>
    /// （复用 BadgePlacement 的翻转与钳制）。DPI 缩放取自显示器本身而不是 WPF——首次呼出时窗口还没有
    /// PresentationSource，WPF 会悄悄按 1.0 算（票 30 的教训）。
    /// </summary>
    private void PlaceBeside(ScreenPoint anchor)
    {
        _workArea = ScreenGeometry.WorkAreaAt(anchor);
        var (scaleX, scaleY) = ScreenGeometry.ScaleAt(anchor);
        var workHeightDip = _workArea.Height / scaleY;

        // 整窗 80–800，矮屏上随工作区；输出区的上限随之收紧，免得输入框与输出加起来超过整窗上限。
        MinHeight = ReverseInputPlacement.MinHeightDip;
        MaxHeight = ReverseInputPlacement.ClampHeightDip(double.MaxValue, workHeightDip);
        OutputScroll.MaxHeight = Math.Max(60, MaxHeight - InputMaxDip - ChromeReserveDip);

        // 还没显示过：没有 ActualHeight，量一遍内容拿到真实的起始高度。
        Measure(new Size(Width, double.PositiveInfinity));
        var heightDip = ReverseInputPlacement.ClampHeightDip(DesiredSize.Height, workHeightDip);

        _placement = ReverseInputPlacement.Place(
            anchor,
            (int)Math.Ceiling(Width * scaleX),
            (int)Math.Ceiling(heightDip * scaleY),
            _workArea,
            (int)Math.Ceiling(GapDip * scaleY));

        // 首次呼出时句柄还不存在，要它就是创建它。物理像素直接落位，绕开按显示器缩放的坐标系。
        var helper = new WindowInteropHelper(this);
        _ = helper.EnsureHandle();
        TransientWindow.MoveTo(
            helper.Handle,
            _placement.PositionFor((int)Math.Ceiling(heightDip * scaleY), _workArea),
            ZBand.Topmost);
    }

    /// <summary>
    /// 内容长高、缩短之后重新落位：翻到上方时底边贴着锚点、向上长（y + 旧高 − 新高），下方时顶边不动。
    /// 用窗口此刻真实的物理像素高度算——不信 WPF 的 DIP 与缩放。
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible || _placement is not { } placement)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (!WindowRects.TryGet(handle, out var rect))
        {
            return;
        }

        var position = placement.PositionFor(rect.Height, _workArea);
        if (position.X != rect.Left || position.Y != rect.Top)
        {
            TransientWindow.MoveTo(handle, position, ZBand.Topmost);
        }
    }

    /// <summary>
    /// 失焦即隐藏，但显示后 300ms 内的失焦忽略（防止刚弹出就被自己触发关掉）。只隐藏，不还原前台：
    /// 焦点此刻正落在用户点下去的窗口上，Restore 只会把它抢回来。模板列表开着时它是我们自己的弹层，
    /// 不算失焦。
    /// </summary>
    private void OnLostFocus(object? sender, EventArgs e)
    {
        if (!IsVisible || _templateList is { IsOpen: true } || !_session.ShouldHideOnFocusLoss())
        {
            return;
        }

        Apply(_session.Escape());
        HideNow();
    }

    private void HideNow()
    {
        _tick.Stop();
        CancelInFlight();
        CloseTemplateList();
        Hide();
    }

    /// <summary>Esc：取消在途请求并关闭，不贴回；回到原来的窗口（与窄条的 Esc 同）。</summary>
    private void CancelAndClose()
    {
        Apply(_session.Escape());
        HideNow();
        _returnTo.Restore();
    }

    // --- 键盘 ---------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // IME：WPF 里组字中的按键以 Key.ImeProcessed 到达，组字中的 Enter、Esc、Tab 一律交给输入法。
        // 不要照搬窄条的"ImeProcessed 归一到真实键"——那是为了让字母动作在中文输入法下也能触发
        // （票 24/26），这里要的恰恰相反：Enter 在组字时是"选字上屏"，不是"贴回"。
        if (e.Key == Key.ImeProcessed)
        {
            return;
        }

        // 模板列表是一层：Esc 先收它，不关窗口。
        if (e.Key == Key.Escape && _templateList is { IsOpen: true })
        {
            e.Handled = true;
            CloseTemplateList();
            InputBox.Focus();
            return;
        }

        var modifiers = Keyboard.Modifiers;

        switch (e.Key)
        {
            case Key.Enter when (modifiers & ModifierKeys.Shift) != 0:
                // Shift+Enter 换行：交给 TextBox（AcceptsReturn）。
                return;

            case Key.Enter:
                e.Handled = true;
                Apply(_session.Enter());
                break;

            case Key.Escape:
                e.Handled = true;
                CancelAndClose();
                break;

            case Key.Tab when (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0:
                // Tab 在这里是"循环方向"，不是焦点导航；不拦的话焦点会走到两枚 chip 上。
                e.Handled = true;
                Apply(_session.CycleDirection());
                break;

            case Key.E when modifiers == ModifierKeys.Control:
                // 与面板的模板按钮共用同一个循环列表（票 42 的 TemplateCycle）。
                e.Handled = true;
                CycleTemplate();
                break;
        }
    }

    private void CycleTemplate()
    {
        Apply(_session.CycleTemplate(PromptTemplates.Cycle(_settings)));
        _template = _session.RuntimeTemplate;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
        => Apply(_session.TextChanged(InputBox.Text));

    /// <summary>组字被取消（Esc、退格删空）时没有最后一次提交事件：组字文本空了就算结束。</summary>
    private void OnCompositionUpdate(object sender, TextCompositionEventArgs e)
    {
        if (e.TextComposition.CompositionText.Length == 0 && e.TextComposition.Text.Length == 0)
        {
            _session.SetComposing(false);
        }
    }

    // --- 把会话的回应做成事 ----------------------------------------------------------------

    private void Apply(ReverseInputStep step)
    {
        // 在途的请求：有新的运行或明确的取消，旧的就作废。序号已经保证它的结果不会上屏，
        // 这里再把它真的停下来——省 token，也别让一次没人要的流继续占着连接。
        if (step.CancelRun || step.Run is not null)
        {
            CancelInFlight();
        }

        if (step.Run is { } run)
        {
            StartRun(run);
        }

        RenderState();
        ArmTick();

        // 贴回放在最后：它会隐藏窗口，此前的渲染与定时器处理已经收尾。
        if (step.Paste is { } text)
        {
            Commit(text);
        }
    }

    private void CancelInFlight()
    {
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
    }

    private void ArmTick()
    {
        if (_session.TimeUntilTick() is not { } wait)
        {
            _tick.Stop();
            return;
        }

        _tick.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
        _tick.Stop();
        _tick.Start();
    }

    /// <summary>
    /// 开一次请求。结果按序号交回会话——过期的（输入又变了、换了方向、Esc）它自己丢掉。
    /// async void 里逃出去的异常是进程级崩溃（O-05），所以整个包在 try 里：失败收敛成状态，
    /// 界面给的是人话，不是异常。
    /// </summary>
    private async void StartRun(ReverseRun run)
    {
        var cancellation = _inFlight = new CancellationTokenSource();

        try
        {
            var outcome = await ReverseInputRunner.RunAsync(
                run,
                _backend(),
                (seq, text) => Dispatcher.Invoke(() => Apply(_session.Partial(seq, text))),
                cancellation.Token);

            Apply(ReverseInputRunner.Deliver(_session, outcome));
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.TranslationFailed, failure, ("reverse", 1));
            Apply(_session.Failed(run.Seq, failure, failure.Message));
        }
        finally
        {
            if (ReferenceEquals(_inFlight, cancellation))
            {
                _inFlight = null;
            }

            cancellation.Dispose();
        }
    }

    // --- 回贴 ---------------------------------------------------------------------------

    /// <summary>
    /// 回贴链路（复用窄条粘贴模式那条路，只补缺的两块）：隐藏自己 → 回到原窗口（前台已对什么都不发；
    /// 不对时 <see cref="ForegroundWindow.Restore"/> 走 F24 强制序列）→ 快照 → 写入 → 发 Ctrl+V →
    /// 400ms 后有条件还原。回不到原窗口就不敢发 Ctrl+V（会贴进别的窗口），译文留在剪贴板上，说一声。
    /// 时序沿用窄条的，没发现需要额外等待——实机若出现第一个字丢失，再加 Xtranslate 的 60ms。
    /// </summary>
    private void Commit(string text)
    {
        var target = _returnTo;
        HideNow();

        // 上一次的还原还没轮到（400ms 内又贴了一次）：先结清，让这次的快照是用户真正的剪贴板，
        // 而不是上一次的译文。
        FlushPendingRestore();

        // 「自动复制译文」（用户需求 2026-10-05）：只管翻译——看实际发给后端的模板，改写类
        // （提示词优化等）的结果照旧只是运输。开着时译文不出声地存入历史（窄条里就能找到），
        // 贴完也留在剪贴板上、不还原。贴不贴得成都存：用户要的是这段译文。
        var keep = _settings.AutoCopyTranslation && _session.Template.Kind == PromptTemplateKind.Translate;
        if (keep)
        {
            _keepTranslation?.Invoke(_session.Text, text);
        }

        if (target.IsSomething && !target.Restore())
        {
            _tell(_paste.Leave(text)
                ? "没能回到原来的输入框，译文已放进剪贴板，请手动粘贴。"
                : "没能回到原来的输入框，译文也没能放进剪贴板。");
            return;
        }

        var result = _paste.Send(text, keep);
        switch (result.Outcome)
        {
            case ReversePasteOutcome.ClipboardUnavailable:
                _tell("没能写入剪贴板（可能被别的程序占着），请再试一次。");
                break;

            case ReversePasteOutcome.KeystrokeFailed:
                _tell("译文已放进剪贴板，但没能发出粘贴，请手动粘贴。");
                break;
        }

        if (result.Pending is { } pending)
        {
            _pendingRestore = pending;
            _restoreTimer.Stop();
            _restoreTimer.Start();
        }
    }

    /// <summary>
    /// 还原剪贴板：序列号没变才还，带排除标记的、有写不回去的格式的不还（判定都在 Core）。写回失败
    /// 要留下痕迹——这是用户会真正在意的那种失败。
    /// </summary>
    private void FlushPendingRestore()
    {
        _restoreTimer.Stop();
        if (_pendingRestore is not { } pending)
        {
            return;
        }

        _pendingRestore = null;
        var settled = _paste.Settle(pending);
        if (settled.Verdict == RestoreVerdict.Restore && !settled.Restored)
        {
            Log.Event(LogEvent.ClipboardRestoreFailed, ("reverse", 1));
        }
    }

    // --- 画界面 -------------------------------------------------------------------------

    /// <summary>
    /// 照会话的状态画界面：取舍都在 Core（<see cref="ReverseInputSession"/>），这里只照着显示。
    /// 输出：旧输出淡显（次级色）、流式中尾部挂静态光标块；失败时人话替它占位。
    /// </summary>
    private void RenderState()
    {
        InputProps.SetPlaceholder(
            InputBox,
            _session.Template.Kind == PromptTemplateKind.Rewrite
                ? "写好需求，按 Enter 整理"
                : "在这里打字，手一停就出译文");

        DirectionChip.Visibility = _session.DirectionVisible ? Visibility.Visible : Visibility.Collapsed;
        DirectionLabel.Text = _session.DirectionLabel;
        DirectionChip.ToolTip = "方向（Tab 切换）";
        AutomationProperties.SetName(DirectionChip, _session.DirectionChipName);

        TemplateChip.Visibility = _session.TemplateChipVisible ? Visibility.Visible : Visibility.Collapsed;
        TemplateName.Text = _session.RuntimeTemplate.Name;
        TemplateChip.ToolTip = $"{_session.RuntimeTemplate.Name} · 点击选择提示词模板，Ctrl+E 切换";
        AutomationProperties.SetName(TemplateChip, _session.TemplateChipName);

        HintText.Text = _session.Hint;

        RenderOutput();
    }

    private void RenderOutput()
    {
        var failed = _session.Status == ReverseInputStatus.Failed;

        OutputText.Inlines.Clear();
        var output = _session.Output;

        // 旧输出淡显：次级色（读得清、又明显不是"现在的答案"）。
        var foreground = _session.OutputFaded ? "Brush.TextSecondary" : "Brush.Text";

        // 结算之后换成可选取的 OutputBox（用户需求 2026-10-05：译文能用鼠标选取复制）。
        // 流式中仍画 TextBlock——光标块只能内联在它里面，流着的字也没人去选。
        var selectable = !_session.Running && output.Length > 0;

        if (output.Length > 0 && !selectable)
        {
            var run = new Run(output);
            run.SetResourceReference(TextElement.ForegroundProperty, foreground);
            OutputText.Inlines.Add(run);
        }

        // 同一段字不重设：每次状态变化都会走到这里，重设会抹掉用户正在拖的选区。
        if (selectable && OutputBox.Text != output)
        {
            OutputBox.Text = output;
        }

        OutputBox.SetResourceReference(ForegroundProperty, foreground);
        OutputBox.Visibility = selectable ? Visibility.Visible : Visibility.Collapsed;

        if (_session.Running)
        {
            // 与面板相同的静态 accent 光标块（2×18，不闪烁，票 22）。
            var caret = new Rectangle
            {
                Width = 2,
                Height = 18,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            caret.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            OutputText.Inlines.Add(new InlineUIContainer(caret));
        }

        OutputText.Visibility = OutputText.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (failed)
        {
            // 失败态用 TranslationUserErrorMapper 的人话（与面板一致）：标题与说明是人话，
            // 原始异常不进界面。附"去设置"——最常见的失败是还没配好服务。
            var error = TranslationUserErrorMapper.Describe(_session.Failure, _session.FailureMessage);
            ErrorBar.Title = error.Title;
            ErrorBar.Message = error.Detail;
            ErrorBar.IsOpen = true;
            ErrorBar.Visibility = Visibility.Visible;
            ErrorActions.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorBar.Visibility = Visibility.Collapsed;
            ErrorActions.Visibility = Visibility.Collapsed;
        }

        OutputScroll.Visibility = OutputText.Visibility == Visibility.Visible
            || OutputBox.Visibility == Visibility.Visible
            || failed
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 流式像对话：跟随最新一行（只在流式中；静止的输出不被拽走）。
        if (_session.Running && OutputScroll.ScrollableHeight > 0)
        {
            OutputScroll.ScrollToEnd();
        }
    }

    // --- chip -----------------------------------------------------------------------------

    private void OnDirectionChipClick(object sender, RoutedEventArgs e)
    {
        Apply(_session.CycleDirection());
        InputBox.Focus();
    }

    /// <summary>
    /// 点模板 chip 弹出完整列表，包括不在 Ctrl+E 循环里的模板。反向输入框接受焦点，所以普通的弹出
    /// 菜单在这里没有面板那样的焦点问题；仍用与窄条同款的自绘 Popup（窄条的菜单同样不是系统菜单）。
    /// </summary>
    private void OnTemplateChipClick(object sender, RoutedEventArgs e)
    {
        if (_templateList is { IsOpen: true })
        {
            CloseTemplateList();
            return;
        }

        var host = new StackPanel { MinWidth = 200 };
        foreach (var template in PromptTemplates.All(_settings))
        {
            host.Children.Add(TemplateRow(template));
        }

        var list = _templateList = new Popup
        {
            Child = BarWindow.WithPopupFont(BarWindow.MenuSurface(host)),
            PlacementTarget = TemplateChip,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        list.Opened += (_, _) => BarWindow.FlipIntoWorkArea(list);
        list.IsOpen = true;
    }

    private UIElement TemplateRow(PromptTemplate template)
    {
        var current = template.Id == _session.RuntimeTemplate.Id;

        // 左是对勾与名字，右是它的类别：翻译 / 改写（改写类不自动跑，按 Enter 才跑）。
        var name = new TextBlock { Text = (current ? "✓  " : "     ") + template.Name };
        var kind = new TextBlock
        {
            Text = template.Kind == PromptTemplateKind.Rewrite ? "改写" : "翻译",
            MinWidth = 36,
            Margin = new Thickness(16, 0, 0, 0),
            TextAlignment = TextAlignment.Right,
        };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        DockPanel.SetDock(kind, Dock.Right);

        var content = new DockPanel();
        content.Children.Add(kind);
        content.Children.Add(name);

        var row = new Button
        {
            Content = content,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand,
        };
        row.SetResourceReference(BackgroundProperty, "Brush.Surface");
        AutomationProperties.SetName(row, $"{template.Name}（{(template.Kind == PromptTemplateKind.Rewrite ? "改写" : "翻译")}）");
        row.Click += (_, _) =>
        {
            CloseTemplateList();
            Apply(_session.SelectTemplate(template));
            _template = _session.RuntimeTemplate;
            InputBox.Focus();
        };
        return row;
    }

    private void CloseTemplateList()
    {
        if (_templateList is not null)
        {
            _templateList.IsOpen = false;
            _templateList = null;
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        // 设置窗要落到焦点上，输入框留在原地只会挡视线：关掉，不贴。
        Apply(_session.Escape());
        HideNow();
        _openSettings();
    }

    // --- 关停 ---------------------------------------------------------------------------

    /// <summary>应用退出时：在途请求取消、没还的剪贴板还回去（退出不该把译文留在用户的剪贴板上）。</summary>
    public void CloseForGood()
    {
        _tick.Stop();
        CancelInFlight();
        FlushPendingRestore();
        Close();
    }
}
