namespace Shiyu.Core;

/// <summary>翻译类模板的方向选项（Tab 循环：自动 → 中→英 → 英→中 → 自动）。</summary>
public enum ReverseDirectionChoice
{
    /// <summary>按 <see cref="ReverseDirection.Detect"/> 的汉字 ×5 规则判定。</summary>
    Auto,

    ChineseToEnglish,
    EnglishToChinese,
}

/// <summary>
/// 改写类模板的输出语言。反向输入框是"往外写"的地方，提示词优化默认就该出英文，
/// 不能因为输入碰巧是英文就输出中文。
/// </summary>
public enum ReverseOutputLanguage
{
    English,
    Chinese,
}

/// <summary>反向输入框此刻的处境：界面据此选提示语与输出的样子。</summary>
public enum ReverseInputStatus
{
    /// <summary>没有输入。</summary>
    Empty,

    /// <summary>翻译类：输入刚改过，手停 300ms 就自动翻译。</summary>
    Debouncing,

    /// <summary>改写类：输出还没有或已过期，按 Enter 才跑。</summary>
    NeedsEnter,

    /// <summary>请求在途。</summary>
    Running,

    /// <summary>输出已经对应当前输入，Enter 就贴回。</summary>
    Ready,

    /// <summary>这一次请求失败了。</summary>
    Failed,
}

/// <summary>要发出去的一次请求。<paramref name="Seq"/> 随结果一起回来，过期的结果凭它认出。</summary>
public sealed record ReverseRun(int Seq, TranslationRequest Request);

/// <summary>
/// 会话对一次事件的回应：要界面做什么。<see cref="Run"/> 隐含"先取消在途的请求"；
/// 光是取消（输入被清空、换了方向、Esc）用 <see cref="CancelRun"/>；
/// <see cref="Paste"/> 是该贴回去的文字，界面隐藏窗口、回到原窗口、贴。
/// </summary>
public readonly record struct ReverseInputStep(ReverseRun? Run = null, bool CancelRun = false, string? Paste = null);

/// <summary>
/// 反向输入框的会话状态机（票 43，Xtranslate 的防抖状态机照拾语纪律重写）。纯 Core、不碰
/// WPF 与网络：由 <see cref="TimeProvider"/> 记时间，界面把按键、定时器与请求结果喂进来，
/// 拿回"该做什么"（<see cref="ReverseInputStep"/>）。
///
/// <list type="bullet">
/// <item>翻译类模板：手一停 300ms 自动跑；改写类模板（提示词优化、自建模板）不自动跑，按 Enter 才跑。</item>
/// <item>竞态守卫：新的输入取消在途请求；每次请求带序号，过期的结果一律丢弃；与上一次完全相同的
/// （原文、方向、模板）不重复请求——但失败过的会重来。</item>
/// <item>Enter 统一表示"往前走一步"：输出已对应当前输入 → 贴回；否则翻译类标 wantCommit，译文一到
/// 自动贴回（短句翻译不必先复核），改写类开跑、跑完<b>停下等第二次 Enter</b>（整理出来的提示词要先过目）。</item>
/// </list>
///
/// 不是线程安全的：只在 UI 线程上用。
/// </summary>
public sealed class ReverseInputSession(TimeProvider clock)
{
    /// <summary>手停多久才算"停了"。</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    /// <summary>显示后多久之内的失焦忽略：防止刚弹出就被自己触发关掉。</summary>
    public static readonly TimeSpan FocusGrace = TimeSpan.FromMilliseconds(300);

    private enum AttemptState
    {
        Running,
        Done,
        Failed,
    }

    /// <summary>
    /// 一个请求的身份：去重按它比。文字取首尾去空白之后的——只差一个空格不算新请求。
    /// </summary>
    private readonly record struct Key(string Text, string? Source, string Target, string TemplateId);

    private sealed class Attempt(int seq, Key key)
    {
        public int Seq { get; } = seq;
        public Key Key { get; } = key;
        public AttemptState State { get; set; } = AttemptState.Running;
        public string Text { get; set; } = string.Empty;
        public Exception? Failure { get; set; }
        public string? Message { get; set; }
    }

    private PromptTemplate _runtime = PromptTemplates.Standard;
    private bool _templatesApply = true;
    private string _text = string.Empty;
    private ReverseDirectionChoice _choice = ReverseDirectionChoice.Auto;
    private ReverseOutputLanguage _output = ReverseOutputLanguage.English;
    private int _seq;

    /// <summary>
    /// 最新发出的那次请求。不变式：它在跑时一定对着当前输入——输入、方向或模板一变，
    /// 在跑的那次立刻作废（置空并要求取消），迟到的结果凭序号认不出它而被丢掉。
    /// 已完成或失败的会留着，让"打一个字又删掉"不必重新请求。
    /// </summary>
    private Attempt? _attempt;

    /// <summary>最近一次有内容的输出（含被作废的那次的部分译文）：等新结果期间淡显。</summary>
    private string _lastOutput = string.Empty;

    private DateTimeOffset? _debounceAt;
    private bool _wantCommit;
    private bool _composing;
    private DateTimeOffset _shownAt;

    private static readonly ReverseInputStep CancelStep = new(CancelRun: true);

    // --- 读：界面照着显示 ---------------------------------------------------------------

    /// <summary>输入框里的原样文字。</summary>
    public string Text => _text;

    /// <summary>
    /// 实际发给后端的模板：模板不生效（免费引擎没有 prompt）时一律是标准。取舍规则复用面板的
    /// <see cref="PanelTemplateState"/>——面板与反向输入框读到同一个答案。
    /// </summary>
    public PromptTemplate Template => State.Template;

    /// <summary>用户当下选着的模板（模板 chip 上显示的那个）；与 <see cref="Template"/> 只在模板不生效时不同。</summary>
    public PromptTemplate RuntimeTemplate => _runtime;

    /// <summary>模板 chip 是否显示：模板不生效（免费引擎）时隐藏。</summary>
    public bool TemplateChipVisible => State.ButtonVisible;

    /// <summary>
    /// 方向 chip 是否显示：翻译类一定有；改写类看模板正文写没写 <c>{target}</c>——不写就由提示词
    /// 自己决定输出语言，方向没有可切的东西（与面板头部同一条规则）。
    /// </summary>
    public bool DirectionVisible => State.DirectionVisible;

    public ReverseDirectionChoice Direction => _choice;

    public ReverseOutputLanguage OutputLanguage => _output;

    /// <summary>方向 chip 上的字。"自动"有内容时显示实际方向（"自动 · 中→英"）。</summary>
    public string DirectionLabel
    {
        get
        {
            if (Template.Kind == PromptTemplateKind.Rewrite)
            {
                return _output == ReverseOutputLanguage.English ? "输出英文" : "输出中文";
            }

            return _choice switch
            {
                ReverseDirectionChoice.ChineseToEnglish => "中→英",
                ReverseDirectionChoice.EnglishToChinese => "英→中",
                _ => Current() is null
                    ? "自动"
                    : $"自动 · {PairLabel(ReverseDirection.Detect(_text.Trim()))}",
            };
        }
    }

    /// <summary>方向 chip 的无障碍名：念得出当前状态与怎么操作。</summary>
    public string DirectionChipName => $"方向：{DirectionLabel}，点击切换";

    /// <summary>模板 chip 的无障碍名。点击弹出完整列表，Ctrl+E 循环。</summary>
    public string TemplateChipName => $"提示词模板：{_runtime.Name}，点击选择";

    /// <summary>
    /// 屏幕上的输出。对着当前输入的就是它自己的（流式中的部分也算）；否则是旧输出——
    /// 此时 <see cref="OutputFaded"/> 为真。失败时没有输出，错误替它占位。
    /// </summary>
    public string Output
    {
        get
        {
            if (Current() is null)
            {
                return string.Empty;
            }

            return CurrentAttempt switch
            {
                { State: AttemptState.Failed } => string.Empty,
                { Text.Length: > 0 } own => own.Text,
                _ => _lastOutput,
            };
        }
    }

    /// <summary>屏幕上的是旧输出（输入改过、或新结果的首字还没到）：淡显。</summary>
    public bool OutputFaded
        => Output.Length > 0 && CurrentAttempt is not { State: not AttemptState.Failed, Text.Length: > 0 };

    /// <summary>请求在途：输出尾部挂静态光标块（与面板同一个，票 22），不另做闪烁动画。</summary>
    public bool Running => _attempt is { State: AttemptState.Running };

    /// <summary>Enter 已按下、译文一到就自动贴回（只有翻译类会有）。</summary>
    public bool WantCommit => _wantCommit;

    public ReverseInputStatus Status
    {
        get
        {
            if (Current() is null)
            {
                return ReverseInputStatus.Empty;
            }

            return CurrentAttempt switch
            {
                { State: AttemptState.Running } => ReverseInputStatus.Running,
                { State: AttemptState.Done } => ReverseInputStatus.Ready,
                { State: AttemptState.Failed } => ReverseInputStatus.Failed,
                _ => Template.Kind == PromptTemplateKind.Translate
                    ? ReverseInputStatus.Debouncing
                    : ReverseInputStatus.NeedsEnter,
            };
        }
    }

    /// <summary>失败态的异常（映射成人话交给 <see cref="TranslationUserErrorMapper"/>）；输入改了就不再显示。</summary>
    public Exception? Failure => CurrentAttempt is { State: AttemptState.Failed } failed ? failed.Failure : null;

    public string? FailureMessage => CurrentAttempt is { State: AttemptState.Failed } failed ? failed.Message : null;

    /// <summary>底部提示：Enter 此刻会做什么。</summary>
    public string Hint
    {
        get
        {
            var rewrite = Template.Kind == PromptTemplateKind.Rewrite;
            return Status switch
            {
                ReverseInputStatus.Empty => rewrite
                    ? "写好后按 Enter 整理 · Esc 关闭"
                    : "打字即译 · Enter 贴回 · Esc 关闭",
                ReverseInputStatus.Debouncing => "Enter 贴回 · Esc 关闭",
                ReverseInputStatus.NeedsEnter => "Enter 开始整理 · Esc 关闭",
                ReverseInputStatus.Running => _wantCommit
                    ? "译完就贴回… · Esc 取消"
                    : rewrite ? "正在整理 · Esc 取消" : "正在翻译 · Esc 取消",
                ReverseInputStatus.Ready => rewrite
                    ? "整理好了，过目后再按 Enter 贴回 · Esc 关闭"
                    : "Enter 贴回 · Esc 关闭",
                _ => "Enter 重试 · Esc 关闭",
            };
        }
    }

    /// <summary>防抖还要等多久；没有待跑的防抖给 null。界面据此装一个单发的定时器，到点调 <see cref="Tick"/>。</summary>
    public TimeSpan? TimeUntilTick()
        => _debounceAt is { } at ? TimeSpan.FromTicks(Math.Max(0, (at - clock.GetUtcNow()).Ticks)) : null;

    /// <summary>失焦要不要隐藏：显示后 300ms 内的失焦忽略，防止刚弹出就被自己触发关掉。</summary>
    public bool ShouldHideOnFocusLoss() => clock.GetUtcNow() - _shownAt >= FocusGrace;

    // --- 写：事件 ------------------------------------------------------------------------

    /// <summary>
    /// 每次呼出：从头开始。模板沿用用户当下选着的（运行时状态，不写设置）；方向每次回到自动、
    /// 改写类输出回到英文。<paramref name="templatesApply"/> 为假（免费引擎）时模板 chip 隐藏、请求一律按标准。
    /// </summary>
    public ReverseInputStep Open(PromptTemplate template, bool templatesApply = true)
    {
        var cancel = Running;

        _runtime = template;
        _templatesApply = templatesApply;
        _text = string.Empty;
        _choice = ReverseDirectionChoice.Auto;
        _output = ReverseOutputLanguage.English;
        _attempt = null;
        _lastOutput = string.Empty;
        _debounceAt = null;
        _wantCommit = false;
        _composing = false;
        _shownAt = clock.GetUtcNow();

        return cancel ? CancelStep : default;
    }

    /// <summary>
    /// 输入法正在组字（拼音还没选字上屏）。WPF 的 TextBox.Text 含着组字中的拼音——"nihao" 还没选字就被
    /// 译了，既白费又闪一下乱码；用户盯着候选词看的那几百毫秒，不算"手停了"。组字期间不排防抖，
    /// 结束时（选字上屏、或被取消）重新计 300ms。
    /// </summary>
    public void SetComposing(bool composing)
    {
        if (_composing == composing)
        {
            return;
        }

        _composing = composing;
        _debounceAt = null;

        if (!composing && Template.Kind == PromptTemplateKind.Translate
            && Current() is { } current && !Covered(current.Key))
        {
            _debounceAt = clock.GetUtcNow() + Debounce;
        }
    }

    /// <summary>输入框的文字变了。</summary>
    public ReverseInputStep TextChanged(string text)
    {
        _text = text ?? string.Empty;

        // 还在打字：之前按下的 Enter（译文一到就贴）不再算数。
        _wantCommit = false;
        _debounceAt = null;

        if (Current() is not { } current)
        {
            // 清空 = 清掉输出，在途的取消（空文本不发请求）。
            var cancelEmpty = Running;
            _attempt = null;
            _lastOutput = string.Empty;
            return cancelEmpty ? CancelStep : default;
        }

        // 新的输入取消在途请求——除非它恰好还是同一个请求（只差首尾空白）。
        var cancel = false;
        if (_attempt is { State: AttemptState.Running } running && running.Key != current.Key)
        {
            _attempt = null;
            cancel = true;
        }

        // 翻译类：手停 300ms 才跑；已经有对着它的无错请求（在跑或已完成）就不再排；
        // 输入法组字期间不排（见 SetComposing）。
        if (Template.Kind == PromptTemplateKind.Translate && !_composing && !Covered(current.Key))
        {
            _debounceAt = clock.GetUtcNow() + Debounce;
        }

        return cancel ? CancelStep : default;
    }

    /// <summary>防抖定时器到点。到点而没有待跑的，什么都不做。</summary>
    public ReverseInputStep Tick()
    {
        if (_debounceAt is not { } at || clock.GetUtcNow() < at)
        {
            return default;
        }

        _debounceAt = null;
        if (Template.Kind != PromptTemplateKind.Translate || Current() is not { } current || Covered(current.Key))
        {
            return default;
        }

        return Start(current);
    }

    /// <summary>Enter：往前走一步。</summary>
    public ReverseInputStep Enter()
    {
        if (Current() is not { } current)
        {
            return default;
        }

        var translate = Template.Kind == PromptTemplateKind.Translate;

        switch (CurrentAttempt)
        {
            case { State: AttemptState.Done } done:
                // 输出已经对应当前输入：贴回。
                _wantCommit = false;
                return new ReverseInputStep(Paste: done.Text);

            case { State: AttemptState.Running }:
                // 在跑：翻译类标记"译完就贴"；改写类已经在跑了，跑完停下等第二次 Enter。
                _wantCommit |= translate;
                return default;
        }

        // 输出已过期、还没有、或上一次失败了：翻译类标 wantCommit 并立刻跑（不必等满 300ms）；
        // 改写类开跑，跑完停下。
        _wantCommit = translate;
        return Start(current);
    }

    /// <summary>Tab：循环方向（翻译类）或切换输出语言（改写类）。</summary>
    public ReverseInputStep CycleDirection()
    {
        if (!DirectionVisible)
        {
            return default;
        }

        if (Template.Kind == PromptTemplateKind.Rewrite)
        {
            _output = _output == ReverseOutputLanguage.English
                ? ReverseOutputLanguage.Chinese
                : ReverseOutputLanguage.English;
        }
        else
        {
            _choice = _choice switch
            {
                ReverseDirectionChoice.Auto => ReverseDirectionChoice.ChineseToEnglish,
                ReverseDirectionChoice.ChineseToEnglish => ReverseDirectionChoice.EnglishToChinese,
                _ => ReverseDirectionChoice.Auto,
            };
        }

        return Reshaped();
    }

    /// <summary>Ctrl+E：按面板共用的循环列表（<see cref="AppSettings.TemplateCycle"/>）换下一个模板。</summary>
    public ReverseInputStep CycleTemplate(IReadOnlyList<PromptTemplate> cycle)
        => _templatesApply ? SelectTemplate(PromptTemplates.Next(_runtime, cycle)) : default;

    /// <summary>选定一个模板（chip 弹出的完整列表，含不在循环里的）。选着的就是它时什么都不做。</summary>
    public ReverseInputStep SelectTemplate(PromptTemplate template)
    {
        if (!_templatesApply || template.Id == _runtime.Id)
        {
            return default;
        }

        _runtime = template;
        return Reshaped();
    }

    /// <summary>Esc：取消在途请求。关闭窗口、不贴回是界面的事。</summary>
    public ReverseInputStep Escape()
    {
        var cancel = Running;
        _attempt = null;
        _debounceAt = null;
        _wantCommit = false;
        return cancel ? CancelStep : default;
    }

    /// <summary>流式中间结果。过期的（序号对不上）丢掉。</summary>
    public ReverseInputStep Partial(int seq, string text)
    {
        if (_attempt is not { State: AttemptState.Running } attempt
            || attempt.Seq != seq
            || string.IsNullOrEmpty(text))
        {
            return default;
        }

        attempt.Text = text;
        _lastOutput = text;
        return default;
    }

    /// <summary>
    /// 一次请求完成。译文一到、且 Enter 已经按过（wantCommit）就回应"贴回"；改写类永远不在
    /// 这里贴——跑完停下，等第二次 Enter。空内容不是可以贴的东西：当作失败。
    /// </summary>
    public ReverseInputStep Completed(int seq, string text)
    {
        if (_attempt is not { State: AttemptState.Running } attempt || attempt.Seq != seq)
        {
            return default;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Fail(attempt, null, "服务没有返回内容，请再试一次。");
            return default;
        }

        attempt.State = AttemptState.Done;
        attempt.Text = text;
        _lastOutput = text;

        if (_wantCommit && Template.Kind == PromptTemplateKind.Translate)
        {
            _wantCommit = false;
            return new ReverseInputStep(Paste: text);
        }

        _wantCommit = false;
        return default;
    }

    /// <summary>一次请求失败。<paramref name="failure"/> 与 <paramref name="message"/> 用来映射人话。</summary>
    public ReverseInputStep Failed(int seq, Exception? failure, string? message)
    {
        if (_attempt is { State: AttemptState.Running } attempt && attempt.Seq == seq)
        {
            Fail(attempt, failure, message);
        }

        return default;
    }

    // --- 内部 ----------------------------------------------------------------------------

    private PanelTemplateState State => PanelTemplateState.For(_runtime, _templatesApply, wordMode: false);

    /// <summary>当前输入对应的请求身份与请求；没有可发的文字（空白）给 null。</summary>
    private (Key Key, TranslationRequest Request)? Current()
    {
        var text = _text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var template = Template;
        TranslationRequest request;

        if (template.Kind == PromptTemplateKind.Rewrite)
        {
            // 改写类：只定输出语言；源语言没有意义（模板自己说怎么处理）。
            request = new TranslationRequest(
                text, _output == ReverseOutputLanguage.English ? "English" : "Chinese")
            {
                Template = template,
            };
        }
        else
        {
            var pair = _choice switch
            {
                ReverseDirectionChoice.ChineseToEnglish => ReversePair.ChineseToEnglish,
                ReverseDirectionChoice.EnglishToChinese => ReversePair.EnglishToChinese,
                _ => ReverseDirection.Detect(text),
            };

            // 方向落成请求的源语言与目标语言：显式声明源语言，所以回声换向（票 34）不会插手——
            // 反向输入框的方向是用户选的、或按规则定的，不该被悄悄反过来。
            var (source, target) = ReverseDirection.Languages(pair);
            request = new TranslationRequest(text, target) { SourceLanguage = source, Template = template };
        }

        return (new Key(text, request.SourceLanguage, request.TargetLanguage, template.Id), request);
    }

    /// <summary>对着当前输入的那次请求；输入、方向或模板变了就没有。</summary>
    private Attempt? CurrentAttempt
        => Current() is { } current && _attempt is { } attempt && attempt.Key == current.Key ? attempt : null;

    /// <summary>已经有对着它的、无错的请求（在跑或已完成）：不必再发。失败过的不算——重来是应该的。</summary>
    private bool Covered(Key key) => _attempt is { } attempt && attempt.Key == key && attempt.State != AttemptState.Failed;

    private ReverseInputStep Start((Key Key, TranslationRequest Request) current)
    {
        _debounceAt = null;
        _attempt = new Attempt(++_seq, current.Key);
        return new ReverseInputStep(Run: new ReverseRun(_seq, current.Request));
    }

    /// <summary>
    /// 方向或模板变了。对这句话请求没变（自动认成的方向恰好是点到的那个）就什么都不做，
    /// 贴回的意愿仍然有效；变了：在途的作废，翻译类立刻重跑（不是在打字，不必等防抖），
    /// 改写类等 Enter。之前那次"译完就贴"不再算数。
    /// </summary>
    private ReverseInputStep Reshaped()
    {
        _debounceAt = null;

        if (Current() is not { } current || Covered(current.Key))
        {
            return default;
        }

        _wantCommit = false;

        if (Template.Kind == PromptTemplateKind.Translate)
        {
            return Start(current);
        }

        var cancel = Running;
        if (cancel)
        {
            _attempt = null;
        }

        return cancel ? CancelStep : default;
    }

    private void Fail(Attempt attempt, Exception? failure, string? message)
    {
        attempt.State = AttemptState.Failed;
        attempt.Text = string.Empty;
        attempt.Failure = failure;
        attempt.Message = message;
        _wantCommit = false;
    }

    private static string PairLabel(ReversePair pair)
        => pair == ReversePair.ChineseToEnglish ? "中→英" : "英→中";
}
