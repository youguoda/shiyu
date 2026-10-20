using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>词典义项的一行：序号 + 释义文本（词性组内编号）。</summary>
public sealed record SenseEntry(int Number, string Text);

/// <summary>
/// 词典卡上的星标要用的三件事（评审 2026-10-16 F-09），由翻译模块接到翻译记录上：收进（原文、译文、模板、
/// 那张卡）、按词头移出、按词头问在不在。
/// </summary>
public sealed record PanelVocabulary(
    Func<string, string, string, DictionaryCard, bool> Star,
    Func<string, int> Unstar,
    Func<string, bool> IsStarred);

/// <summary>
/// 词典例句：斜体只给拉丁（U-22——含中文的 TextBlock 的 FontStyle 永远
/// Normal，中文没有斜体传统，仿斜只会发虚）。
/// </summary>
public sealed record SenseExample(string Text)
{
    public FontStyle ItalicStyle => LanguageGuess.FromText(Text).Script == TextScript.Latin
        ? FontStyles.Italic
        : FontStyles.Normal;
}

/// <summary>词典同义词行（一条拼好的整行）。</summary>
public sealed record SenseSynonyms(string Line);

/// <summary>
/// 一个词性组（票 22 / §6.2 单词态）：默认 2 条释义 + 1 例句，其余折叠进
/// 「展开全部释义（N）」。展开后整组重建、列表重挂——词典是阅读辅助，
/// 不值得为它引入通知管道。
/// </summary>
public sealed class SenseGroupView
{
    private const int VisibleDefinitions = 2;
    private const int VisibleExamples = 1;

    private readonly DictionarySense _sense;
    private bool _expanded;

    public SenseGroupView(DictionarySense sense) => _sense = sense;

    public string? PartOfSpeech => _sense.PartOfSpeech;

    public IReadOnlyList<object> Visible { get; private set; } = [];

    public int HiddenCount { get; private set; }

    public string ExpandLabel => $"展开全部释义（{HiddenCount}）";

    public Visibility ExpandVisibility => !_expanded && HiddenCount > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public void Expand()
    {
        _expanded = true;
    }

    /// <summary>按折叠规则重建可见行。折叠窗口仿样稿：释义 1 · 例句 · 释义 2 · 同义词。</summary>
    public void Rebuild()
    {
        var definitions = _sense.Definitions;
        var examples = _sense.Examples;

        var defCount = _expanded ? definitions.Count : Math.Min(VisibleDefinitions, definitions.Count);
        var exCount = _expanded ? examples.Count : Math.Min(VisibleExamples, examples.Count);
        HiddenCount = (definitions.Count - defCount) + (examples.Count - exCount);

        var visible = new List<object>(definitions.Count + examples.Count + 1);
        for (var i = 0; i < defCount; i++)
        {
            visible.Add(new SenseEntry(i + 1, definitions[i]));

            // 例句跟在第一条释义后面（样稿 panel.png 的读法）；展开时全量。
            if (i == 0)
            {
                for (var j = 0; j < exCount; j++)
                {
                    visible.Add(new SenseExample(examples[j]));
                }
            }
        }

        if (_sense.Synonyms.Count > 0)
        {
            visible.Add(new SenseSynonyms("同义词：" + string.Join("、", _sense.Synonyms)));
        }

        Visible = visible;
    }
}

/// <summary>
/// The translation panel: the original above, the translation growing beneath
/// it as the words arrive.
///
/// 票 22 之后译文槽三态同位（§6.2）：加载骨架（150ms 内首字到达就不出
/// 现）、失败时 InfoBar 就地替代译文位（人话 + 重试 + 折叠的原始异常）、
/// 空时折叠。单词态是另一套层级：词头 + 音标 + 发音钮（读原词，
/// ADR-0012）→ 译文即中文释义 → 1 DIP 分隔 → 不装框的词典。
///
/// Like the badge, it never takes focus. That leaves it unable to receive key
/// presses, so Escape is a global hotkey held only while the panel is on
/// screen — see <see cref="_escape"/>.
/// </summary>
public partial class PanelWindow : Window
{
    /// <summary>骨架延迟（§6.2/U-23）：快后端的面板不该闪一下假骨架。</summary>
    private static readonly TimeSpan SkeletonDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>「已复制」的回落时长（§6.2 footer）。</summary>
    private static readonly TimeSpan CopyResetDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>外壳高度上限（§6.2）：min(560, 工作区高 − 16) 的 560 那半边。</summary>
    private const double MaxShellHeight = 560;

    /// <summary>
    /// 当前热键注册表的取用口，而非一次性捕获（O-43）：注册表随每次设置
    /// 保存整体重建（O-20），攥着退役实例的窗口再按 Esc 只会悄悄失灵。
    /// </summary>
    private readonly Func<HotkeyRegistry> _hotkeys;
    private readonly WindowsClipboardWriter _clipboard;
    private readonly Func<ITranslationBackend> _backend;
    private readonly Action<string, string>? _saveTranslation;

    /// <summary>不出声地存一条译文（「自动复制译文」用）：返回是否真的写了新条目。</summary>
    private readonly Func<string, string, bool>? _keepTranslation;

    /// <summary>记一条翻译记录（原文、结果、模板名）；开关与排除名单由接收方把关。</summary>
    private readonly Action<string, string, string>? _logTranslation;

    /// <summary>按选中文本现造词典端口；null 表示这段文本不吃词典卡。</summary>
    private readonly Func<string, IDictionaryApi?>? _dictionary;

    private readonly SpeechSynthesis? _speech;

    /// <summary>翻译此刻是否有一条能走的路；null 表示"不归我管"（测试与旧调用）。</summary>
    private readonly Func<bool>? _backendReady;

    /// <summary>「去配置」深链：打开设置的服务页，面板自身让开。</summary>
    private readonly Action? _openSettings;

    /// <summary>
    /// 「用免费引擎」：把翻译方式写成免费引擎（票 41），写成功返回 true。点击就是
    /// 同意——没有任何静默切换；null 表示"不归我管"（测试与旧调用）。
    /// </summary>
    private readonly Func<bool>? _enableFreeEngine;

    /// <summary>
    /// 面板上屏期间持有的作用域 Esc。每次写设置热键注册表都会整体重建，旧注册表带着它
    /// 一起被注销，所以它是一个可重挂的持有（<see cref="ReclaimEscape"/>），而不是一次性的句柄。
    /// </summary>
    private readonly ScopedHold _escape;
    private CancellationTokenSource? _inFlight;
    private TranslationSession? _session;

    /// <summary>还在出字、节拍刷新可以碰的那一轮；结算时清空，排着没轮到的刷新就此作废。</summary>
    private TranslationSession? _streaming;

    // --- 钉住与自动收起（用户需求 2026-10-20）：不钉时点别处即收（流式中、
    //     指针悬在面板上时例外）；钉着就不收。可见期间的前台哨。 ----------
    private System.Windows.Threading.DispatcherTimer? _foregroundTick;
    private bool _pointerOnPanel;

    private string _original = string.Empty;

    /// <summary>星标（评审 2026-10-16 F-09）：没接上就不露面；接上了，卡到了才露面。</summary>
    private readonly PanelVocabulary? _vocabulary;

    /// <summary>此刻卡上的那一张：星标收的就是它。换词、卡作废时清掉。</summary>
    private DictionaryCard? _card;
    private string _target;
    private string? _source;

    /// <summary>面板持有的最新设置（票 42）：模板的解析、循环与"模板是否生效"都读它。</summary>
    private AppSettings _settings;

    /// <summary>
    /// 面板当下选着的提示词模板（票 42）。只活在进程里，不写设置：面板在场时
    /// 写设置有三个副作用——① 广播 SettingsChanged 会让 HotkeyModule 整体重建热键
    /// 注册表，面板持有的作用域 Esc 跟着旧注册表一起被注销，点一下模板按钮 Esc 就
    /// 失灵；② ApplySettings 会把 _source/_target 打回设置值，用户先换向、再换模板，
    /// 方向会被打回去；③ 有热键冲突的用户每点一次都会再弹一次冲突气泡。
    /// 面板是复用的实例，下次弹出时仍是这个模板；启动时取默认模板。
    /// </summary>
    private PromptTemplate _template;

    /// <summary>模板是否生效（<see cref="AppSettings.PromptTemplatesApply"/>）：设置变了才重读，不在流式回调里反复建后端。</summary>
    private bool _templatesApply;

    /// <summary>头部与布局据此取舍，随模板、单词态与设置刷新（<see cref="ApplyTemplateChrome"/>）。</summary>
    private PanelTemplateState _templateState;

    /// <summary>逐句对照显示开关。默认关——整段流式是主路径，对照是阅读辅助。</summary>
    private bool _sentenceMode;

    /// <summary>ApplySentenceMode 正在程序化设 IsChecked 的标记（防 Checked 回环）。</summary>
    private bool _applyingMode;

    /// <summary>
    /// 单词态（票 22）：原文是一个词。翻译类模板下摆词典布局——词头行就位、模式分段隐藏、
    /// footer 不放朗读（<see cref="PanelTemplateState.WordLayout"/>）；改写类模板照句子态摆。
    /// </summary>
    private bool _wordMode;

    /// <summary>词典卡换代号：新一次翻译自增，迟到的卡据此知道自己过时了。</summary>
    private int _cardRun;

    /// <summary>
    /// 这段原文的词典卡发查过没有。改写类模板下的单词不查：卡不上屏，查了白花一次模型调用
    /// （中文词与免费词典查不到的英文词走 LLM 词典化）；切回翻译类模板时补查（<see cref="RequestCard"/>）。
    /// </summary>
    private bool _cardRequested;

    private IReadOnlyList<SenseGroupView> _senseGroups = [];

    /// <summary>失败态的人话（§6.2 映射）；原始异常只在展开「详细信息」时上屏。</summary>
    private TranslationUserError? _lastError;

    private bool _errorDetailOpen;
    private DispatcherTimer? _skeletonDelay;
    private DispatcherTimer? _copyReset;

    public PanelWindow(
        Func<HotkeyRegistry> hotkeys,
        WindowsClipboardWriter clipboard,
        Func<ITranslationBackend> backend,
        AppSettings settings,
        Action<string, string>? saveTranslation = null,
        Func<string, IDictionaryApi?>? dictionary = null,
        SpeechSynthesis? speech = null,
        Func<bool>? backendReady = null,
        Action? openSettings = null,
        Func<bool>? enableFreeEngine = null,
        Func<string, string, bool>? keepTranslation = null,
        Action<string, string, string>? logTranslation = null,
        PanelVocabulary? vocabulary = null)
    {
        InitializeComponent();

        _vocabulary = vocabulary;

        _hotkeys = hotkeys;
        _clipboard = clipboard;
        _backend = backend;
        _saveTranslation = saveTranslation;
        _keepTranslation = keepTranslation;
        _logTranslation = logTranslation;
        _dictionary = dictionary;
        _speech = speech;

        // 朗读中按钮显示「停止」（评审 2026-10-16 F-10）；面板关掉时退订，语音服务比面板活得久。
        if (_speech is not null)
        {
            _speech.SpeakingChanged += OnSpeakingChanged;
            Closed += (_, _) => _speech.SpeakingChanged -= OnSpeakingChanged;
        }
        _backendReady = backendReady;
        _openSettings = openSettings;
        _enableFreeEngine = enableFreeEngine;

        // 取的是"当下"的注册表（_hotkeys 每次现取）：重挂时它已是重建之后的新注册表。
        _escape = new ScopedHold(() => _hotkeys().TryRegisterScoped(
            new Hotkey(HotkeyModifiers.None, 0x1B, "关闭面板"),
            () => Dispatcher.Invoke(OnEscape)));

        // 模板列表开着时，点面板别处就收起它（列表与 chip 上的按下各归它们自己）。
        Shell.PreviewMouseDown += (_, _) =>
        {
            if (TemplateList.IsVisible && !TemplateList.IsMouseOver && !TemplateButton.IsMouseOver)
            {
                CloseTemplateList();
            }
        };

        // 拖动（用户需求 2026-10-09）：按在面板的文字、留白、头尾栏上就拖。只管这一次——换模板、
        // 重试都在原地，下一段文字才回到光标旁（MoveBesideCursor）；长高时仍钳进它此刻所在的屏幕。
        Shell.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (DragGrip.IsGrip(e.OriginalSource, Shell, TemplateList))
            {
                e.Handled = true;
                DragGrip.Run(this, e);
            }
        };

        _target = settings.TargetLanguage;
        _source = settings.SourceLanguage;
        _settings = settings;
        _template = PromptTemplates.ResolveDefault(settings);
        _templatesApply = settings.PromptTemplatesApply;
        _templateState = PanelTemplateState.For(_template, _templatesApply, wordMode: false);

        Backdrop.AttachShell(this, Shell, () => BackdropKind.Acrylic);
    }

    /// <summary>
    /// 翻译服务是否已配置。公共通道未上线时不构成可用的路（票 08）——
    /// 热键、划词、复制徽标三条路都汇到这里，未配置的结局只有引导卡。
    /// </summary>
    private bool TranslationReady => _backendReady?.Invoke() != false;

    /// <summary>
    /// 译文语言跟随设置即时换新（O-20）：面板是复用实例，改设置不该等
    /// 重启——下一次翻译就用新语言，正在流式中的那一次按它开始时的语言走完。
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        _target = settings.TargetLanguage;
        _source = settings.SourceLanguage;

        // 运行时模板只在默认模板或循环列表真的变了时才被覆盖（票 42）：别处改了无关
        // 的设置，用户在面板里切到的模板原样保留。
        _template = PromptTemplates.Reconcile(_settings, settings, _template);
        _settings = settings;
        _templatesApply = settings.PromptTemplatesApply;

        // 整套布局而不只是头部：模板生不生效会改单词的摆法（引导卡上点「用免费引擎」就是
        // 这样——改写模板不再作数，单词回到词典布局，随后补的卡才有地方上屏）。
        ApplyLayoutMode();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TransientWindow.MakeNonActivating(new WindowInteropHelper(this).Handle, ZBand.Topmost);
    }

    /// <summary>
    /// Shows the panel beside the cursor and starts translating.
    ///
    /// <paramref name="onDisplayed"/> fires exactly when the panel is on screen
    /// (after placement, before the stream starts) — 划词路径靠它钉住"剪贴板
    /// 还原放在翻译面板显示之后"的次序（票 37 的 Glossy 细节）。
    /// </summary>
    public async Task TranslateAsync(string text, Action? onDisplayed = null)
    {
        CloseTemplateList();
        _original = text;
        _cardRun++;
        _cardRequested = false;
        _lastError = null;
        CollapseError();

        // 新的选中文本作废旧卡：卡还在路上的话，到岸后发现换代号变了就不上屏。
        DictionaryArea.Visibility = Visibility.Collapsed;
        _senseGroups = [];
        CardSenses.ItemsSource = null;
        CardPhonetic.Text = string.Empty;
        CardMeta.Text = string.Empty;
        CardMeta.Visibility = Visibility.Collapsed;
        _cardLater = null;
        _card = null;
        WordStarButton.Visibility = Visibility.Collapsed;
        SetupCard.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Visible;
        SaveLabel.Text = "存入历史";
        ResetCopyLabel();

        // 单词态本地可判（与词典端口同一个纯词形规则），头部立刻进入单词
        // 层级，不等词典卡到岸。
        _wordMode = _dictionary is not null
            && (DictionaryWord.IsEnglishWord(text) || DictionaryWord.IsChineseWord(text));
        CardWord.Text = text.Trim();

        OriginalText.Text = text;
        OriginalText.ToolTip = text;
        TranslatedText.Clear();
        TranslatedText.Visibility = Visibility.Collapsed;
        SentencePairs.ItemsSource = null;
        UpdateDirectionLabel();
        ApplyLayoutMode();

        if (!IsVisible)
        {
            Show();
        }

        // 面板可见期间的前台哨（用户需求 2026-10-20：不钉时点别处即收）。
        _foregroundTick ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _foregroundTick.Tick -= OnForegroundTick;
        _foregroundTick.Tick += OnForegroundTick;
        _foregroundTick.Start();

        // Full opacity immediately, no fade-in: a layered window fading in
        // from transparency never composites (ticket 04's badge finding), so
        // the panel would rely on the translation stream's layout churn to
        // rescue it — arriving late or, with a fast backend, not at all.
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        UpdateLayout();
        MoveBesideCursor();

        // 面板已显示：此刻之后的剪贴板写不再挡在用户和首帧之间。
        onDisplayed?.Invoke();

        HoldEscape();
        UpdateFooterState();
        UpdateHint();

        // 未配置翻译服务：给一张引导卡而不是异常文本（票 08 的验收线——
        // 任何路径都不出现异常英文）。词典卡也不发：没有自己的密钥，
        // LLM 词典路本来就缺席。
        if (!TranslationReady)
        {
            ShowSetupCard();
            return;
        }

        // 两阶段词典（票 35）：阶段一翻译照常跑完上屏；阶段二在它之后补卡。
        // 查询与翻译并行发出（省一轮往返），但渲染严格排在翻译之后——
        // 用户先看到译文，再看到词典细节，顺序即"两阶段"的含义。
        var cardRun = _cardRun;
        var cardTask = RequestCard();
        await RunTranslation();
        await RenderCardWhenCurrent(cardTask, cardRun);
    }

    /// <summary>引导卡：告诉用户去哪，而不是报一个错。</summary>
    private void ShowSetupCard()
    {
        TranslatedText.Clear();
        TranslatedText.Visibility = Visibility.Collapsed;
        SentencePairs.ItemsSource = null;
        ContentScroll.Visibility = Visibility.Collapsed;
        HideSkeleton();
        SetupCard.Visibility = Visibility.Visible;
        UpdateFooterState();
    }

    private void OnOpenSetup(object sender, RoutedEventArgs e)
    {
        _openSettings?.Invoke();

        // 设置窗要落到焦点上，无激活的面板留在原地只会挡视线。
        Dismiss();
    }

    /// <summary>
    /// 引导卡上的「用免费引擎」（票 41）：把翻译方式写成免费引擎，然后就地重译这段文本。
    /// 点击就是同意——文本发往哪里已在这张卡上写明，没有任何静默切换；写设置失败时
    /// 托盘已经说了人话、翻译方式没变，卡就留着。
    ///
    /// 写设置会广播 SettingsChanged、让热键注册表重建——面板的 Esc 随之在新注册表上
    /// 重挂（<see cref="ReclaimEscape"/>），所以点完按钮后 Esc 仍能关面板。
    /// </summary>
    private async void OnUseFreeEngine(object sender, RoutedEventArgs e)
    {
        // async void 逃出去的异常是进程级崩溃（O-05）：与重试、换向同一条纪律。
        try
        {
            if (_enableFreeEngine?.Invoke() != true)
            {
                return;
            }

            SetupCard.Visibility = Visibility.Collapsed;
            ContentScroll.Visibility = Visibility.Visible;

            // 与 TranslateAsync 的主路径同一个次序：词典查询与翻译并行发出，卡排在译文之后渲染。
            var cardRun = _cardRun;
            var cardTask = RequestCard();
            await RunTranslation();
            await RenderCardWhenCurrent(cardTask, cardRun);
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.TranslationFailed, failure, ("freeEngine", 1));
            ShowError(TranslationUserErrorMapper.Describe(failure, failure.Message));
        }
    }

    private async Task RunTranslation()
    {
        // 换方向与重试也会走到这里：未配置的结局同样是引导卡，不是异常文本。
        if (!TranslationReady)
        {
            ShowSetupCard();
            return;
        }

        // A second request while the first is still arriving abandons it; the
        // user has moved on and the old stream's text would interleave.
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = new CancellationTokenSource();

        var session = new TranslationSession(_backend());
        _session = session;
        _streaming = session;

        ArmSkeletonDelay();

        // 出字的节拍（用户实录 2026-10-15：LLM 出字时输出一卡一卡、拖滚动条发涩、偶尔"未响应"）。
        // 原来每到一片就同步、以最高优先级（Send）把整篇重排一遍再强制布局：鼠标、键盘排在它后面，
        // 片来得一密，界面就被排版占满。现在只按节拍排一回刷新，见 StreamRefresh。
        var cadence = new RenderCadence(RenderCadence.Default);
        var clock = Stopwatch.StartNew();
        session.Updated += () => StreamRefresh.Request(Dispatcher, cadence, clock, () => RenderStream(session));

        // 提示词模板（票 42）：模板不生效（免费引擎）时一律按标准构建，这样免费引擎下
        // 回声换向照常工作。单词态也照当前模板走（用户需求 2026-10-13）。
        var template = PanelTemplateState.For(_template, _templatesApply, _wordMode).Template;
        var request = new TranslationRequest(_original, _target) { SourceLanguage = _source, Template = template };
        var token = _inFlight.Token;

        // 整条流在后台线程上读：后端的 await 不再捕获 UI 线程，网络上成串到达的片（同步读出的那些）
        // 不会在 UI 线程上连成一整段几秒钟的活。
        await Task.Run(() => session.RunAsync(request, token));

        // 终态统一在 await 之后结算（流式回调只管增量）：失败在此映射成人
        // 话 InfoBar，部分译文保留在上方——三分之二的译文也是答案。
        Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }

            // 结算之后，还排着没轮到的那一回节拍刷新作废：它会把「已复制/已存入」刷回去。
            _streaming = null;
            HideSkeleton();
            RenderTranslation(session);
            if (session.State == TranslationState.Failed)
            {
                ShowError(TranslationUserErrorMapper.Describe(session.Failure, session.Error));
            }

            UpdateFooterState();
            UpdateHint();

            // 「自动复制译文」（用户需求 2026-10-05）：只在一次翻译真正译完时——失败、取消、
            // 提示词优化之类改写类的结果都不算。放在 UpdateFooterState 之后：它会把按钮
            // 改成"已复制/已存入"，不能再被刷回去。
            if (session.State == TranslationState.Finished
                && template.Kind == PromptTemplateKind.Translate
                && _settings.AutoCopyTranslation)
            {
                AutoKeep(session.Text);
            }

            // 翻译记录（用户需求 2026-10-09）：每一次真正译完都记一笔，改写类的结果也记——记录上
            // 写着模板名，找回时分得清。失败、取消不记；同一结果再交一次由库挡掉。
            if (session.State == TranslationState.Finished && session.Text.Trim().Length > 0)
            {
                _logTranslation?.Invoke(_original, session.Text, template.Name);
            }
        });
    }

    /// <summary>
    /// 译完即复制并存入历史，按钮的样子与手动点过一样：复制钮短暂说「已复制」，存入钮
    /// 变成「已存入」——再点也不会存第二条（翻译模块也会挡掉与最新一条相同的重复）。
    /// </summary>
    private void AutoKeep(string translated)
    {
        if (translated.Trim().Length == 0)
        {
            return;
        }

        CopyTranslation();
        _keepTranslation?.Invoke(_original, translated);
        MarkSaved();
    }

    /// <summary>
    /// 该查卡时发查：摆着词典布局、这段原文还没查过。否则交回一张不会来的卡——
    /// 改写类模板下不查，查过的不重查（换方向、重试都还是这个词）。
    /// </summary>
    private Task<DictionaryCard?> RequestCard()
    {
        if (!_templateState.WordLayout || _cardRequested)
        {
            return Task.FromResult<DictionaryCard?>(null);
        }

        _cardRequested = true;
        return FetchDictionaryCard(_original);
    }

    /// <summary>
    /// 阶段二的取卡：单词判定通过才发查。慢路（LLM 词典化）与快路（600ms
    /// 预算的免费词典）都由 <see cref="_dictionary"/> 决定；任何失败都只是
    /// 没有卡。只取不渲染——渲染时机归 <see cref="RenderCardWhenCurrent"/>。
    /// </summary>
    private async Task<DictionaryCard?> FetchDictionaryCard(string text)
    {
        if (_dictionary is null)
        {
            return null;
        }

        var word = text.Trim();
        IDictionaryApi? api;
        try
        {
            api = _dictionary(word);
        }
        catch (Exception)
        {
            // expected: 组装端口失败与查词失败同类：静默没有卡。
            return null;
        }

        if (api is null)
        {
            return null;
        }

        try
        {
            var card = await api.LookupAsync(DictionaryWord.LookupKey(word), CancellationToken.None);

            // 本地词典的卡先上屏，例句随后补（评审 2026-10-16 F-08）：补的那一趟自己管连接，
            // 这里放掉查卡的端口不碍它。
            _cardLater = card is not null && api is IDictionaryEnricher enricher
                ? enricher.EnrichAsync(card, CancellationToken.None)
                : null;
            return card;
        }
        catch (Exception)
        {
            // expected: 端口契约本不该抛；抛了也一样是"没有卡"。
            return null;
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }
    }

    /// <summary>这一张卡后补的样子（例句），没有为 null。只在查卡那一刻设，渲染时取走。</summary>
    private Task<DictionaryCard?>? _cardLater;

    /// <summary>
    /// 翻译之后补卡：迟到的换代检查保证只有最新一次翻译的卡会上屏——
    /// 翻译失败也补（卡只关于原文的那个词，与译文成败无关）。
    /// </summary>
    private async Task RenderCardWhenCurrent(Task<DictionaryCard?> fetch, int run)
    {
        DictionaryCard? card;
        try
        {
            card = await fetch;
        }
        catch (Exception)
        {
            // expected: 换代竞态或取卡失败——没有卡上屏，翻译不受影响。
            card = null;
        }

        if (card is null || run != _cardRun)
        {
            return;
        }

        var later = _cardLater;
        _cardLater = null;
        RenderIfCurrent(card, run);
        if (later is null)
        {
            return;
        }

        DictionaryCard? richer;
        try
        {
            richer = await later;
        }
        catch (Exception)
        {
            // expected: 补例句与查卡同一个契约——补不上就是没有例句。
            richer = null;
        }

        if (richer is not null)
        {
            RenderIfCurrent(richer, run);
        }
    }

    private void RenderIfCurrent(DictionaryCard card, int run)
    {
        if (run != _cardRun)
        {
            return;
        }

        try
        {
            Dispatcher.Invoke(() =>
            {
                if (run != _cardRun)
                {
                    return;
                }

                RenderCard(card);
            });
        }
        catch (Exception)
        {
            // expected: 应用关停的竞态里 Invoke 会抛——一张迟到的卡
            // 不值得带崩进程。
        }
    }

    private void RenderCard(DictionaryCard card)
    {
        CardPhonetic.Text = card.Phonetic ?? string.Empty;

        // 本地词典（评审 2026-10-16 F-08）：查的是变形就说一声是谁的变形，再列考试标签。
        var meta = new List<string>();
        if (card.InflectionOf is { Length: > 0 })
        {
            meta.Add($"{card.Word} 的变形");
        }

        meta.AddRange(card.Tags);
        CardMeta.Text = string.Join(" · ", meta);
        CardMeta.Visibility = meta.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _card = card;
        WordStarButton.Visibility = _vocabulary is null ? Visibility.Collapsed : Visibility.Visible;
        ShowStar(_vocabulary?.IsStarred(card.Word) == true);
        _senseGroups = card.Senses.Select(sense =>
        {
            var group = new SenseGroupView(sense);
            group.Rebuild();
            return group;
        }).ToList();
        CardSenses.ItemsSource = _senseGroups;

        // 卡在路上时用户可能已换到改写类模板：数据先放着，切回翻译类时由 ApplyLayoutMode 亮出来。
        DictionaryArea.Visibility = _templateState.WordLayout ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();
    }

    private void OnExpandSense(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SenseGroupView group)
        {
            group.Expand();
            group.Rebuild();

            // 整表重挂是最省事的通知方式：词典行数两位数，重建不可感知。
            CardSenses.ItemsSource = null;
            CardSenses.ItemsSource = _senseGroups;
        }
    }

    // === 译文槽三态（§6.2） ==================================================

    /// <summary>150ms 后仍在干等且一个字都没到，骨架才出现。</summary>
    private void ArmSkeletonDelay()
    {
        HideSkeleton();
        _skeletonDelay = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SkeletonDelay,
        };
        _skeletonDelay.Tick += (_, _) =>
        {
            _skeletonDelay.Stop();
            if (_session is { State: TranslationState.Streaming }
                && _session.Text.Length == 0
                && TranslatedText.Visibility == Visibility.Collapsed
                && SetupCard.Visibility == Visibility.Collapsed)
            {
                LoadingSkeleton.Visibility = Visibility.Visible;
                UpdateHint();
            }
        };
        _skeletonDelay.Start();
    }

    private void HideSkeleton()
    {
        _skeletonDelay?.Stop();
        _skeletonDelay = null;
        LoadingSkeleton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 译文渲染：按 Markdown 排（用户需求 2026-10-10，ADR-0014），流式尾随静态 accent 光标块
    /// （与字同高，不闪烁）。复制、存入历史、自动复制拿的仍是 session.Text 原文。
    /// </summary>
    private void RenderTranslation(TranslationSession session)
    {
        var text = session.Text;
        if (text.Length == 0)
        {
            // 空态：折叠，不白占一行（§6.2）。
            TranslatedText.Visibility = Visibility.Collapsed;
            return;
        }

        if (SentenceView)
        {
            TranslatedText.Visibility = Visibility.Collapsed;
            return;
        }

        TranslatedText.Visibility = Visibility.Visible;
        TranslatedText.Show(text, streaming: session.State == TranslationState.Streaming);
    }

    /// <summary>失败态：人话标题 + 说明 + 重试/服务设置；原始异常折叠。</summary>
    private void ShowError(TranslationUserError error)
    {
        _lastError = error;
        ErrorBar.Title = error.Title;
        ErrorBar.Message = error.Detail;
        ErrorBar.IsOpen = true;
        ErrorBar.Visibility = Visibility.Visible;

        ErrorActions.Visibility = Visibility.Visible;
        RetryButton.Visibility = error.ShowRetry ? Visibility.Visible : Visibility.Collapsed;
        ErrorSettingsButton.Visibility = error.ShowSettings ? Visibility.Visible : Visibility.Collapsed;
        CollapseErrorDetail();
    }

    private void CollapseError()
    {
        ErrorBar.Visibility = Visibility.Collapsed;
        ErrorActions.Visibility = Visibility.Collapsed;
        CollapseErrorDetail();
    }

    /// <summary>折叠「详细信息」——原始异常只在展开那一刻才进 UIA 树。</summary>
    private void CollapseErrorDetail()
    {
        _errorDetailOpen = false;
        ErrorDetailText.Text = string.Empty;
        ErrorDetailText.Visibility = Visibility.Collapsed;
        ErrorDetailChevron.Text = "\uE70D";
        ErrorDetailToggle.ToolTip = "展开原始错误信息";
    }

    private void OnToggleErrorDetail(object sender, RoutedEventArgs e)
    {
        _errorDetailOpen = !_errorDetailOpen;
        if (_errorDetailOpen)
        {
            ErrorDetailText.Text = _lastError?.Raw ?? string.Empty;
            ErrorDetailText.Visibility = Visibility.Visible;
            ErrorDetailChevron.Text = "\uE70E";
            ErrorDetailToggle.ToolTip = "收起原始错误信息";
        }
        else
        {
            CollapseErrorDetail();
        }
    }

    private void OnCopyErrorDetail(object sender, RoutedEventArgs e)
    {
        if (_lastError is null)
        {
            return;
        }

        _clipboard.SetText(_lastError.Raw);
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        // async void 逃出去的异常是进程级崩溃（O-05）：与换向同一条纪律。
        try
        {
            CollapseError();
            await RunTranslation();
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.TranslationFailed, failure, ("retry", 1));
            ShowError(TranslationUserErrorMapper.Describe(failure, failure.Message));
        }
    }

    private void OnOpenErrorSettings(object sender, RoutedEventArgs e)
    {
        _openSettings?.Invoke();
        Dismiss();
    }

    // === 布局模式：句子 / 逐句 / 单词 ========================================

    /// <summary>头部与原文块随模式换脸：逐句与单词都藏顶部原文。</summary>
    private void ApplyLayoutMode()
    {
        // 模式分段与方向标签一并随模板取舍（票 42）：单词态没有分段，改写类固定整段。
        ApplyTemplateChrome();

        // 词典布局跟着模板走（用户需求 2026-10-13：单词也能换模板）：改写类收起词头与卡，
        // 切回翻译类，已经到了的卡原样回来。
        var wordLayout = _templateState.WordLayout;
        WordHeader.Visibility = wordLayout ? Visibility.Visible : Visibility.Collapsed;
        DictionaryArea.Visibility = wordLayout && CardSenses.ItemsSource is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplySentenceMode();
    }

    /// <summary>
    /// 实际生效的逐句对照（票 42）：改写类模板下固定为整段——逐句对照是给译文用的，
    /// 改写结果和原文的句子对不上。用户原来的选择仍留在 <see cref="_sentenceMode"/>
    /// 里，切回翻译类模板时恢复。
    /// </summary>
    private bool SentenceView
        => _sentenceMode && _templateState.Template.Kind == PromptTemplateKind.Translate;

    /// <summary>
    /// 头部按模板换脸（票 42）：模板含 {target} 时读作"中文 ⇄ 英语 · 口语"，不含时隐藏
    /// 源语言、⇄ 与目标语言，只显示模板名；模板不生效（免费引擎）时按钮隐藏。
    /// 取舍全在 <see cref="PanelTemplateState"/>（Core，有测试），这里只照着显示。
    /// </summary>
    private void ApplyTemplateChrome()
    {
        var state = _templateState = PanelTemplateState.For(_template, _templatesApply, _wordMode);

        TemplateButton.Visibility = state.ButtonVisible ? Visibility.Visible : Visibility.Collapsed;
        TemplateName.Text = _template.Name;
        if (!state.ButtonVisible)
        {
            // 模板不生效时 chip 退场：它打开的列表不能比它活得久。
            CloseTemplateList();
        }

        // 按钮上的名字按显示宽度截断；tooltip 与无障碍名用全名，随状态更新
        // （探针的 a11y 全件具名扫描要求零无名件）。
        TemplateButton.ToolTip = $"{_template.Name} · 点击选择提示词模板";
        AutomationProperties.SetName(TemplateButton, state.AccessibleName);

        var direction = state.DirectionVisible ? Visibility.Visible : Visibility.Collapsed;
        SourceLabel.Visibility = direction;
        SwapButton.Visibility = direction;
        TargetLabel.Visibility = direction;
        TemplateSeparator.Visibility = direction;

        ModeSegment.Visibility = state.ModeSegmentVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 点 chip：开合模板列表（用户需求 2026-10-09：与反向输入框一样挑，原来是点一下循环到
    /// 下一个）。列表是全部模板，包括不在 Ctrl+E 循环里的那些，当前的打勾。
    /// </summary>
    private void OnTemplateButtonClick(object sender, RoutedEventArgs e)
    {
        if (TemplateList.IsVisible)
        {
            CloseTemplateList();
            return;
        }

        TemplateList.Content = TemplateMenu.Build(PromptTemplates.All(_settings), _template.Id, PickTemplate);
        TemplateList.Visibility = Visibility.Visible;
    }

    private void CloseTemplateList()
    {
        TemplateList.Visibility = Visibility.Collapsed;
        TemplateList.Content = null;
    }

    /// <summary>Esc：先收模板列表；钉着先取消钉（面板留下，恢复自动收起）；都没了才关面板（列表是一层，同反向输入框）。</summary>
    private void OnEscape()
    {
        if (TemplateList.IsVisible)
        {
            CloseTemplateList();
            return;
        }

        if (Pinned)
        {
            Unpin();
            return;
        }

        Dismiss();
    }

    private async void PickTemplate(PromptTemplate template)
    {
        CloseTemplateList();
        await SwitchTemplate(template);
    }

    /// <summary>
    /// 换到选中的模板，并立即用它重跑当前原文（票 42）。走换方向那条重跑路径
    /// （<see cref="OnSwapDirection"/> → <see cref="RunTranslation"/>），在途请求照旧取消。
    /// 只改运行时状态，不写设置——理由见 <see cref="_template"/>。
    /// </summary>
    private async Task SwitchTemplate(PromptTemplate next)
    {
        // 从 async void 进来，逃出去的异常是进程级崩溃（O-05）：与换向同一条纪律。
        try
        {
            if (next.Id == _template.Id)
            {
                // 挑的就是当前这个：不白白重跑一遍。
                return;
            }

            _template = next;

            // 改写类固定整段、单词收起词典布局；切回翻译类恢复用户原来的选择与词典布局。
            ApplyLayoutMode();
            ResetCopyLabel();
            ResetSaveLabel();

            // 单词从改写类切回翻译类：这个词的卡还没查过就此刻补查，与主路径同一个次序。
            var cardRun = _cardRun;
            var cardTask = RequestCard();
            await RunTranslation();
            await RenderCardWhenCurrent(cardTask, cardRun);
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.TranslationFailed, failure, ("template", 1));
            ShowError(TranslationUserErrorMapper.Describe(failure, failure.Message));
        }
    }

    private void OnModeWholeSegment(object sender, RoutedEventArgs e)
    {
        if (_applyingMode)
        {
            return;
        }

        _sentenceMode = false;
        ApplySentenceMode();
    }

    private void OnModeSentenceSegment(object sender, RoutedEventArgs e)
    {
        if (_applyingMode)
        {
            return;
        }

        _sentenceMode = true;
        ApplySentenceMode();
    }

    /// <summary>开关只切显示：对照数据由同一份流式文本现算，切换零成本、随时可翻。
    /// Checked 事件（而非 Click）让键盘与 UIA 也能驱动；程序化设 IsChecked
    /// 由 <see cref="_applyingMode"/> 挡住回环。</summary>
    private void ApplySentenceMode()
    {
        _applyingMode = true;
        try
        {
            WholeSegment.IsChecked = !_sentenceMode;
            SentenceSegment.IsChecked = _sentenceMode;
            SpeakButton.Visibility = _templateState.WordLayout ? Visibility.Collapsed : Visibility.Visible;

            // 顶部原文块：逐句模式的原文在句对里；词典布局下的"原文"是词头。
            OriginalText.Visibility = SentenceView || _templateState.WordLayout
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (SentenceView)
            {
                TranslatedText.Visibility = Visibility.Collapsed;
                SentencePairs.Visibility = Visibility.Visible;
                RenderSentencePairs(_session?.Text ?? string.Empty);
            }
            else
            {
                SentencePairs.Visibility = Visibility.Collapsed;
                SentencePairs.ItemsSource = null;
                TranslatedText.Visibility = _session is { Text.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            _applyingMode = false;
        }
    }

    private void RenderSentencePairs(string translated)
        => SentencePairs.ItemsSource = SentenceAlign.Pair(_original, translated);

    /// <summary>
    /// 按节拍刷新一回流式的译文（UI 线程，后台优先级；见 <see cref="StreamRefresh"/>）。读的是此刻
    /// 会话里完整的一份——两回刷新之间到的片一并带上。不强制布局：原来每一片都 UpdateLayout，布局
    /// 交还给 WPF 按渲染优先级做。
    /// </summary>
    private void RenderStream(TranslationSession session)
    {
        if (!ReferenceEquals(_streaming, session))
        {
            return;
        }

        // 首字到达（或失败/取消）：骨架再也没必要出现。
        if (session.Text.Length > 0 || session.State != TranslationState.Streaming)
        {
            HideSkeleton();
        }

        // 要不要跟到底，按内容长高之前的位置判断。
        var follow = ShouldFollowStream();

        RenderTranslation(session);
        if (SentenceView)
        {
            RenderSentencePairs(session.Text);
        }

        // The label follows the request actually on the wire: an echo
        // retry swaps the direction under the user, and the label must
        // not keep claiming the direction that just failed.
        UpdateDirectionLabel(session.CurrentRequest);

        if (follow)
        {
            ContentScroll.ScrollToEnd();
        }

        UpdateFooterState();
        UpdateHint();
    }

    /// <summary>
    /// 流式像对话：跟随最新一行，除非用户滚上去重读——或者正按着滚动条拖（用户实录 2026-10-15：
    /// 出字时拖滚动条发涩，滑块刚拖到底附近，下一片字一到又被拽回最底下）。
    /// </summary>
    private bool ShouldFollowStream()
        => ContentScroll.ScrollableHeight > 0
            && ContentScroll.VerticalOffset >= ContentScroll.ScrollableHeight - 24
            && !(Mouse.Captured is DependencyObject held && ContentScroll.IsAncestorOf(held));

    // === footer ==============================================================

    /// <summary>没有译文时禁用复制/朗读/存入（U-24——消除假可用）。</summary>
    private void UpdateFooterState()
    {
        var hasText = _session?.Text.Trim().Length > 0;
        CopyButton.IsEnabled = hasText;
        SpeakButton.IsEnabled = hasText && _speech is not null;
        SaveButton.IsEnabled = _saveTranslation is not null
            && _session is { State: TranslationState.Finished }
            && hasText;
    }

    /// <summary>加载中提示「正在翻译 · Esc 取消」，平时「Esc 关闭」。</summary>
    private void UpdateHint()
    {
        var busy = LoadingSkeleton.Visibility == Visibility.Visible
            || _session is { State: TranslationState.Streaming };
        HintText.Text = (busy, !_escape.IsHeld) switch
        {
            (true, true) => "正在翻译 · Esc 取消",
            (true, false) => "正在翻译",
            (false, true) => "Esc 关闭",
            _ => "点 ✕ 关闭",
        };
    }

    private void OnSpeak(object sender, RoutedEventArgs e)
    {
        if (_speech is null || _session?.Text.Trim() is not { Length: > 0 } text)
        {
            return;
        }

        // 读的是当前屏上的译文，用的语言也随它——回声换向后读的应该是
        // 换向后的那段，方向标签看到的是哪个，耳朵听到的就是哪个。
        var language = _session.CurrentRequest?.TargetLanguage ?? _target;
        _speech.SpeakOrStop(text, SpeechLanguage.CulturePrefix(language));
    }


    /// <summary>
    /// 朗读中按钮显示「停止」（评审 2026-10-16 F-10）：两枚朗读钮（底栏读译文、词头读原词）一起换成停止的样子，
    /// 读完、停下就换回来。事件可能来自语音线程，回界面线程再改。
    /// </summary>
    private void OnSpeakingChanged(bool speaking) => Dispatcher.BeginInvoke(() =>
    {
        var glyph = speaking ? "\uE71A" : "\uE767";
        SpeakGlyph.Text = glyph;
        WordSpeakGlyph.Text = glyph;
        SpeakLabel.Text = speaking ? "停止" : "朗读";
        SpeakButton.ToolTip = speaking ? "停止朗读" : "朗读译文；朗读中再点一次即停";
        WordSpeakButton.ToolTip = speaking ? "停止朗读" : "朗读这个词";
        System.Windows.Automation.AutomationProperties.SetName(SpeakButton, speaking ? "停止朗读" : "朗读");
        System.Windows.Automation.AutomationProperties.SetName(WordSpeakButton, speaking ? "停止朗读" : "朗读这个词");
    });

    /// <summary>
    /// 词典卡上的星标（评审 2026-10-16 F-09）：空心点下去收进生词本，实心点下去移出。收的是此刻卡上的那一张，
    /// 连同这次查词的原文、译文与模板；翻译记录关着也收得进（那是用户自己点的）。
    /// </summary>
    private void OnStarWord(object sender, RoutedEventArgs e)
    {
        if (_card is not { } card || _vocabulary is not { } vocabulary)
        {
            return;
        }

        try
        {
            if (vocabulary.IsStarred(card.Word))
            {
                vocabulary.Unstar(card.Word);
                ShowStar(false);
                return;
            }

            ShowStar(vocabulary.Star(_original, _session?.Text ?? string.Empty, _templateState.Template.Name, card));
        }
        catch (Exception failure)
        {
            // 库被占着一类的意外：星标保持原样，留一行日志。
            Log.Event(LogEvent.TranslationLogFailed, failure, ("vocabulary", 1));
        }
    }

    private void ShowStar(bool starred)
    {
        WordStarGlyph.Text = starred ? "\uE735" : "\uE734";
        WordStarButton.ToolTip = starred ? "已在生词本里 · 再点移出" : "收进生词本";
        System.Windows.Automation.AutomationProperties.SetName(WordStarButton, starred ? "移出生词本" : "收进生词本");
    }

    /// <summary>
    /// 词头旁的发音钮读的是**原词**（ADR-0012 拍板）：查词的人想听的是
    /// 发音，不是译文的朗读。音色跟源语言——正是单词态 footer 不再放
    /// 朗读按钮换来的那一枚。
    /// </summary>
    private void OnSpeakWord(object sender, RoutedEventArgs e)
    {
        if (_speech is null || _original.Trim() is not { Length: > 0 } word)
        {
            return;
        }

        var language = _session?.CurrentRequest?.SourceLanguage
            ?? _source
            ?? LanguageGuess.FromText(word).LanguageName;
        _speech.SpeakOrStop(word, SpeechLanguage.CulturePrefix(language));
    }

    /// <summary>
    /// Hands the finished pair to whoever owns history. The link to the
    /// original entry is resolved there — here there is only text.
    /// </summary>
    private void OnSaveToHistory(object sender, RoutedEventArgs e)
    {
        if (_saveTranslation is null || _session?.Text.Trim() is not { Length: > 0 } translated)
        {
            return;
        }

        _saveTranslation(_original, translated);
        MarkSaved();
    }

    /// <summary>存入钮落定为「已存入」：手动点与自动复制译文共用。</summary>
    private void MarkSaved()
    {
        SaveButton.IsEnabled = false;
        SaveLabel.Text = "已存入";
        // 票 27：轻反馈让屏幕阅读器也听见（Polite——不打断）。
        AutomationProperties.SetName(SaveButton, "已存入");
        AutomationProperties.SetLiveSetting(SaveButton, AutomationLiveSetting.Polite);
    }

    /// <summary>
    /// 按住 Shift 点「复制」，复制的是框里看到的纯文字（评审 2026-10-16 F-02：贴进微信这类不认
    /// Markdown 的地方，`**`、`#` 原样留在那里）。不按 Shift 仍是模型给的原文——贴进 Markdown 文档的
    /// 人要的是它。
    /// </summary>
    private void OnCopy(object sender, RoutedEventArgs e)
        => CopyTranslation(plain: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    /// <summary>复制当前译文并让复制钮说一会儿真话：手动点与自动复制译文共用。</summary>
    private void CopyTranslation(bool plain = false)
    {
        if (_session is null || _session.Text.Length == 0)
        {
            return;
        }

        var copied = _clipboard.SetText(plain ? MarkdownText.PasteText(_session.Text) : _session.Text);
        CopyLabel.Text = copied ? (plain ? "已复制纯文本" : "已复制") : "复制失败";

        // 票 27 / U-29：状态变化同步给屏幕阅读器——名字跟着按钮走（内容是
        // 面板，UIA 派生不出名字），轻反馈 Polite、失败 Assertive。
        AutomationProperties.SetName(CopyButton, CopyLabel.Text);
        AutomationProperties.SetLiveSetting(
            CopyButton,
            copied ? AutomationLiveSetting.Polite : AutomationLiveSetting.Assertive);

        // 「已复制」1.5 秒后回落（§6.2）：按钮说真话，但只说一会儿。
        _copyReset?.Stop();
        _copyReset = new DispatcherTimer { Interval = CopyResetDelay };
        _copyReset.Tick += (_, _) =>
        {
            _copyReset.Stop();
            ResetCopyLabel();
        };
        _copyReset.Start();
    }

    private void ResetCopyLabel()
    {
        _copyReset?.Stop();
        _copyReset = null;
        CopyLabel.Text = "复制";
        AutomationProperties.SetName(CopyButton, "复制");
    }

    /// <summary>
    /// 换模板重跑出来的是另一份结果：此前存过的"已存入"不该留在能再存的按钮上
    /// （票 42）。名字与文字一起回落，屏幕阅读器听到的和眼睛看到的一致。
    /// </summary>
    private void ResetSaveLabel()
    {
        SaveLabel.Text = "存入历史";
        AutomationProperties.SetName(SaveButton, "存入历史");
    }

    // === 定位与夹回 ==========================================================

    private void MoveBesideCursor()
    {
        var cursor = ScreenGeometry.CursorPosition();
        var workArea = ScreenGeometry.WorkAreaAt(cursor);

        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var placed = BadgePlacement.Place(
            cursor,
            (int)Math.Ceiling(ActualWidth * scaleX),
            (int)Math.Ceiling(ActualHeight * scaleY),
            workArea);

        // A global surface: the translation panel belongs to the copy, not to
        // the bar, and keeps the topmost band wherever the bar sits.
        TransientWindow.MoveTo(new WindowInteropHelper(this).Handle, placed, ZBand.Topmost);
    }

    /// <summary>
    /// 每次尺寸变化都重新夹回工作区（§6.2/U-23）：词典卡晚到、译文长出
    /// 滚动区时，面板不再越出屏幕底部——先把中间滚动区收矮到外壳上限
    /// 之内，再把整个窗口拉回工作区。
    /// </summary>
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        var scale = DeviceScale();
        if (scale <= 0)
        {
            return;
        }

        CapScrollToWorkArea(scale);
        ClampIntoWorkArea(scale);
    }

    private double DeviceScale()
        => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 0;

    /// <summary>中间滚动区的上限 = min(560, 工作区高 − 16) − 头尾固定高。只收不放。</summary>
    private void CapScrollToWorkArea(double scale)
    {
        var work = WorkAreaAroundWindow(scale);
        var maxShell = Math.Min(MaxShellHeight, work.Height / scale - 16);
        var fixedChrome = ActualHeight - ContentScroll.ActualHeight;
        var cap = maxShell - fixedChrome;
        if (cap > 0 && ContentScroll.MaxHeight > cap)
        {
            ContentScroll.MaxHeight = Math.Floor(cap);
        }
    }

    /// <summary>把整个窗口夹回它所在显示器的工作区（探针 U-23 的那条零越界）。</summary>
    private void ClampIntoWorkArea(double scale)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var work = WorkAreaAroundWindow(scale);
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var left = (int)Math.Round(Left * scale);
        var top = (int)Math.Round(Top * scale);

        var x = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        var y = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        if (x != left || y != top)
        {
            TransientWindow.MoveTo(handle, new ScreenPoint(x, y), ZBand.Topmost);
        }
    }

    private ScreenRect WorkAreaAroundWindow(double scale)
    {
        var center = new ScreenPoint(
            (int)Math.Round((Left + ActualWidth / 2) * scale),
            (int)Math.Round((Top + ActualHeight / 2) * scale));
        return ScreenGeometry.WorkAreaAt(center);
    }

    // === 键与关停 ============================================================

    /// <summary>
    /// Takes Escape for as long as the panel is up. Releasing it reliably
    /// matters more than taking it: an Escape left registered would be
    /// swallowed system-wide.
    /// </summary>
    private void HoldEscape()
    {
        _escape.Hold();

        // Escape being unavailable costs the user a click on the close button;
        // it is not worth refusing to show a translation over.
        UpdateHint();
    }

    private void ReleaseEscape() => _escape.Release();

    /// <summary>
    /// 热键注册表整体重建之后，面板在场就在新注册表上重挂 Esc（票 41）。每次写设置都会
    /// 触发重建，旧注册表带着面板的 Esc 一起被注销，而 <see cref="HoldEscape"/> 不会
    /// 自己重挂——面板里点「用免费引擎」会让这变成常规路径；面板开着时去设置窗改任何
    /// 一项本来就会触发。
    ///
    /// 顺序有两处要求：必须排在热键模块重建<b>之后</b>（由 AppShell.HotkeysRebuilt 事件保证，
    /// 它在重建完成才发）；必须先放旧作用域、再取新的（<see cref="ScopedHold.Reacquire"/>）。
    /// 面板不在场时什么都不做：下一次 TranslateAsync 的 HoldEscape 自会取新注册表。
    /// </summary>
    public void ReclaimEscape()
    {
        if (!IsVisible)
        {
            return;
        }

        _escape.Reacquire();
        UpdateHint();
    }

    private void Dismiss()
    {
        _foregroundTick?.Stop();

        // 钉只挡自动收起；走到这里就是用户点了明确的去处（×、Esc 第二击、去设置）。关即取消钉。
        Pinned = false;
        UpdatePinVisual();
        _inFlight?.Cancel();
        ReleaseEscape();
        CloseTemplateList();

        // 面板没了，朗读也该停：读完一个没人看的译文是对接下来的打扰。
        _speech?.Stop();

        // Instant hide. A fade here reads as jank on a layered window — the
        // text drops out before the tinted sheet does (the user's own words:
        // "先没有字体，再一个灰板") — and the system's Win+V panel, the
        // benchmark for this exact surface, also pops shut with no exit.
        Hide();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Dismiss();

    // --- 钉住（用户需求 2026-10-19）：钉着就不自动收起，一直在屏幕上 -----------------

    /// <summary>钉着时不走任何自动收起；Esc 第一击先取消钉，第二击才关（同反向输入框）。</summary>
    private bool Pinned { get; set; }

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        if (Pinned)
        {
            Unpin();
        }
        else
        {
            Pin();
        }
    }

    private void Pin()
    {
        Pinned = true;
        UpdatePinVisual();
    }

    private void Unpin()
    {
        Pinned = false;
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        PinGlyphFull.Visibility = Pinned ? Visibility.Visible : Visibility.Collapsed;
        PinGlyphEmpty.Visibility = Pinned ? Visibility.Collapsed : Visibility.Visible;
        var state = Pinned ? "已钉住：面板保持显示 · 点一下取消" : "钉住，面板保持显示";
        PinButton.ToolTip = state;
        System.Windows.Automation.AutomationProperties.SetName(PinButton, state);
    }

    /// <summary>指针悬在面板上（自动收起的例外之一）。</summary>
    private void OnPanelMouseEnter(object sender, MouseEventArgs e) => _pointerOnPanel = true;

    /// <summary>指针离开面板。</summary>
    private void OnPanelMouseLeave(object sender, MouseEventArgs e) => _pointerOnPanel = false;

    /// <summary>
    /// 面板可见期间的前台哨（用户需求 2026-10-20：不钉时点别处即收）：
    /// 前台换成别的应用、指针不在面板上、翻译已结算（不在流式中）——收起。
    /// 钉住优先。可见才跑，收起即停。
    /// </summary>
    private void OnForegroundTick(object? sender, EventArgs e)
    {
        if (!IsVisible || Pinned || _pointerOnPanel
            || _inFlight is not null || _streaming is not null)
        {
            return;
        }

        var foreground = ForegroundWindow.Current();
        if (!foreground.IsSomething || foreground.BelongsToThisProcess)
        {
            return;
        }

        Dismiss();
    }

    private async void OnSwapDirection(object sender, RoutedEventArgs e)
    {
        // async void 逃出去的异常是进程级崩溃（O-05）。翻译失败本身已被
        // 会话收敛成状态文字；这里的 catch 罩的是换向组装路径上的意外。
        try
        {
            // Swapping only makes sense between two named languages; with the
            // source left to the backend there is nothing to swap it with.
            (_source, _target) = (_target, _source ?? "English");
            UpdateDirectionLabel();
            ResetCopyLabel();
            await RunTranslation();
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.TranslationFailed, failure, ("swap", 1));
            ShowError(TranslationUserErrorMapper.Describe(failure, failure.Message));
        }
    }

    private void UpdateDirectionLabel(TranslationRequest? attempt = null)
    {
        // 源语言未声明时用本地文字系统先验代替"自动识别"占位——纯码位
        // 统计，不产生请求，流式照旧。猜不出（纯数字之类）才退回占位。
        var request = attempt ?? new TranslationRequest(_original, _target)
        {
            SourceLanguage = _source,
        };

        // 语言名统一中文（票 22 头部）：设置值与先验短名同过一张映射表，
        // 认识不了的原样显示。
        var source = request.SourceLanguage
            ?? LanguageGuess.FromText(_original).LanguageName
            ?? "自动识别";

        SourceLabel.Text = LanguageDisplay.Name(source);
        TargetLabel.Text = LanguageDisplay.Name(request.TargetLanguage);
    }

    /// <summary>Lets the application close it for real on shutdown.</summary>
    public void CloseForGood()
    {
        _inFlight?.Cancel();
        ReleaseEscape();
        _speech?.Stop();
        HideSkeleton();
        ResetCopyLabel();
        Close();
    }
}
