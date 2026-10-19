#if DEBUG
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>翻译面板的调试探针区（只在 Debug 构建存在，同 App.DebugProbes.cs）。</summary>
public partial class PanelWindow
{
    /// <summary>
    /// 探针命令 translation-log 的面板一段（用户需求 2026-10-09：翻译框能拖）：文字与语言名是
    /// 拖动的把手；按钮、模板列表各管自己的按下。
    /// </summary>
    /// <summary>探针：点一下钉、读钉状态、模拟 Esc 一击（用户需求 2026-10-19 的验收）。</summary>
    internal void ProbeClickPin() =>
        PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal bool ProbePinned => Pinned;

    internal void ProbeEscape() => OnEscape();

    internal string ProbeGrips()
        => FormattableString.Invariant(
            $"panelGrip|original={DragGrip.IsGrip(OriginalText, Shell, TemplateList)}|label={DragGrip.IsGrip(SourceLabel, Shell, TemplateList)}|copy={DragGrip.IsGrip(CopyButton, Shell, TemplateList)}|close={DragGrip.IsGrip(CloseButton, Shell, TemplateList)}|list={DragGrip.IsGrip(TemplateList, Shell, TemplateList)}")
            + Environment.NewLine;

    /// <summary>
    /// 探针命令 panel-templates（用户需求 2026-10-09：翻译面板也像反向输入框一样挑模板）。
    /// 等译完，点 chip 看列表：是不是全部模板、当前的打勾；挑另一个，看列表收起、chip 换名、
    /// 面板还在；再开列表按 Esc，看收起的是列表、不是面板。只写 ASCII（名字比对在这边做）。
    /// </summary>
    internal async Task<string> ProbeTemplatePicker()
    {
        var log = new StringBuilder();

        await Settled();
        log.AppendLine(FormattableString.Invariant($"chip|visible={TemplateButton.IsVisible}"));

        TemplateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Settled();
        var rows = Rows();
        var ticked = rows.Count(row => row.Tag is PromptTemplate template && template.Id == _template.Id
            && Descendants(row).OfType<TextBlock>().Any(text => text.Text.StartsWith('✓')));
        log.AppendLine(FormattableString.Invariant(
            $"list|visible={TemplateList.IsVisible}|rows={rows.Count}|templates={PromptTemplates.All(_settings).Count}|ticked={ticked}"));
        if (Content is FrameworkElement root)
        {
            App.RenderToPng(root, System.IO.Path.Combine(DebugOverrides.ProbeDirectory!, "panel-templates-list.png"));
        }

        var target = rows.Select(row => row.Tag).OfType<PromptTemplate>().FirstOrDefault(template => template.Id != _template.Id);
        rows.FirstOrDefault(row => ReferenceEquals(row.Tag, target))?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Settled();
        log.AppendLine(FormattableString.Invariant(
            $"picked|listVisible={TemplateList.IsVisible}|switched={target is not null && _template.Id == target.Id && TemplateName.Text == target.Name}|panelVisible={IsVisible}"));

        TemplateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Settled();
        var openBefore = TemplateList.IsVisible;
        OnEscape();
        await Settled();
        log.AppendLine(FormattableString.Invariant(
            $"escape|listWasOpen={openBefore}|listVisible={TemplateList.IsVisible}|panelVisible={IsVisible}"));

        return log.ToString();

        List<Button> Rows() => Descendants(TemplateList).OfType<Button>().ToList();
    }

    /// <summary>
    /// 探针命令 panel-speech（评审 2026-10-16 F-10）：点底栏的「朗读」，朗读中按钮换成「停止」，读完换回来；用的是
    /// 哪条路、哪个音色由语音服务报。语音服务已静音（探针不在用户机器上出声）。判断在这里做完，日志只有真假与音色名。
    /// </summary>
    internal async Task<string> ProbeSpeech()
    {
        await Settled();
        SpeakButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        var shown = false;
        for (var waited = 0; waited < 100 && !shown; waited++)
        {
            await Task.Delay(50);
            shown = SpeakLabel.Text == "停止";
        }

        var back = false;
        for (var waited = 0; waited < 300 && !back; waited++)
        {
            await Task.Delay(50);
            back = SpeakLabel.Text == "朗读";
        }

        return FormattableString.Invariant(
            $"speech|voice={_speech?.LastVoice}|stopShown={shown}|backAfter={back}{Environment.NewLine}");
    }

    /// <summary>
    /// 探针命令 panel-dictionary（评审 2026-10-16 F-08）：单词态的卡来自离线词典——音标、中文释义、考试标签
    /// 都在；查变形（went）说是 go 的变形。配 SHIYU_FAKE_BACKEND=1 与 SHIYU_LOCAL_DICTIONARY=1，不碰网络。
    /// 判断在这里做完，日志里只有真假（探针脚本只认 ASCII）；音标传 null 时只看有没有。
    /// </summary>
    internal async Task<string> ProbeDictionaryCard(string word, string? phonetic, string meaning, string meta, bool star = false)
    {
        for (var waited = 0; waited < 50 && !DictionaryArea.IsVisible; waited++)
        {
            await Task.Delay(100);
        }

        await Settled();
        var definitions = _senseGroups
            .SelectMany(group => group.Visible.OfType<SenseEntry>())
            .Select(entry => entry.Text)
            .ToList();
        if (Content is FrameworkElement root)
        {
            App.RenderToPng(root, System.IO.Path.Combine(DebugOverrides.ProbeDirectory!, $"panel-dictionary-{word}.png"));
        }

        // 生词本（评审 2026-10-16 F-09）：点一下星，看它收没收进去、星有没有变实心。
        var starred = string.Empty;
        if (star)
        {
            WordStarButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Settled();
            starred = FormattableString.Invariant(
                $"|starred={_card is { } card && _vocabulary?.IsStarred(card.Word) == true}|filled={WordStarGlyph.Text == "\uE735"}");
        }

        return FormattableString.Invariant(
            $"{word}|card={DictionaryArea.IsVisible}|phonetic={(phonetic is null ? CardPhonetic.Text.Length > 0 : CardPhonetic.Text == phonetic)}|meaning={definitions.Any(text => text.Contains(meaning, StringComparison.Ordinal))}|meta={CardMeta.Text.Contains(meta, StringComparison.Ordinal)}{starred}{Environment.NewLine}");
    }

    /// <summary>
    /// 探针命令 panel-templates 的单词一段（用户需求 2026-10-13：翻译单词时也能换模板）。面板已
    /// 开在一个词上：chip 在、词典布局在；换到改写类，词头与卡让位、原文回来；切回翻译类，卡原样
    /// 回来、不重查。再在改写类下开一个新词：不查卡；切回翻译类才补查一次。
    /// </summary>
    internal async Task<string> ProbeWordTemplates()
    {
        var log = new StringBuilder();
        var rewrite = PromptTemplates.All(_settings).First(template => template.Kind == PromptTemplateKind.Rewrite);

        await Settled();
        Line("word");
        Render("panel-word.png");

        await SwitchTemplate(rewrite);
        await Settled();
        Line("rewrite");
        Render("panel-word-rewrite.png");

        await SwitchTemplate(PromptTemplates.Standard);
        await Settled();
        Line("back");

        await SwitchTemplate(rewrite);
        await TranslateAsync("record");
        await Settled();
        Line("fresh-rewrite");

        await SwitchTemplate(PromptTemplates.Standard);
        await Settled();
        Line("fresh-back");

        return log.ToString();

        void Line(string step) => log.AppendLine(FormattableString.Invariant(
            $"{step}|chip={TemplateButton.IsVisible}|header={WordHeader.IsVisible}|original={OriginalText.IsVisible}|card={DictionaryArea.IsVisible}|speak={SpeakButton.IsVisible}|lookups={DebugFakeDictionary.Lookups}"));

        void Render(string file)
        {
            if (Content is FrameworkElement root)
            {
                App.RenderToPng(root, System.IO.Path.Combine(DebugOverrides.ProbeDirectory!, file));
            }
        }
    }

    /// <summary>
    /// 探针命令 stream-jank（用户实录 2026-10-15：LLM 出字时输出一卡一卡、拖滚动条发涩、偶尔
    /// "未响应"）。假后端按真实模型的节奏吐一篇长 Markdown；出字全程每 25 毫秒投一个输入优先级的
    /// 空活，量它等了多久才轮到——鼠标、键盘的消息就排在这一级；同时用调度器钩子记下 UI 线程上
    /// 最长的一段活、以及 UI 线程忙着的时间占比。出完字，再拿一个新框把全文整篇排一遍，核对这一框
    /// 一路排出来的字与之一致。然后切到逐句对照、再出一遍，对照表跟着刷新时同样量一遍。
    /// </summary>
    internal async Task<string> ProbeStreamJank()
    {
        var whole = await MeasureStream("whole");

        _sentenceMode = true;
        ApplySentenceMode();
        var again = RunTranslation();
        var sentence = await MeasureStream("sentence");
        await again;
        return whole + sentence;
    }

    private async Task<string> MeasureStream(string pass)
    {
        var clock = Stopwatch.StartNew();
        var started = new Dictionary<DispatcherOperation, long>();
        long longest = 0;
        long busy = 0;
        void OnStarted(object? sender, DispatcherHookEventArgs e) => started[e.Operation] = clock.ElapsedTicks;
        void OnEnded(object? sender, DispatcherHookEventArgs e)
        {
            if (started.Remove(e.Operation, out var at))
            {
                var took = clock.ElapsedTicks - at;
                longest = Math.Max(longest, took);
                busy += took;
            }
        }

        // 等这一轮真正开始出字。
        for (var waited = 0; waited < 100 && _session is not { State: TranslationState.Streaming, Text.Length: > 0 }; waited++)
        {
            await Task.Delay(20);
        }

        var hooks = Dispatcher.Hooks;
        hooks.OperationStarted += OnStarted;
        hooks.OperationCompleted += OnEnded;
        hooks.OperationAborted += OnEnded;
        var streamStart = clock.ElapsedTicks;

        var latencies = new List<double>();
        using var stop = new CancellationTokenSource();
        var pinger = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var posted = clock.ElapsedTicks;
                await Dispatcher.InvokeAsync(
                    () => latencies.Add((clock.ElapsedTicks - posted) * 1000.0 / Stopwatch.Frequency),
                    DispatcherPriority.Input);
                await Task.Delay(25);
            }
        });

        for (var waited = 0; waited < 1200 && _session is { State: TranslationState.Streaming }; waited++)
        {
            await Task.Delay(50);
        }

        var streamTicks = clock.ElapsedTicks - streamStart;
        stop.Cancel();
        await pinger;
        hooks.OperationStarted -= OnStarted;
        hooks.OperationCompleted -= OnEnded;
        hooks.OperationAborted -= OnEnded;
        await Settled();

        // 整段视图下核对：一路增量排出来的字，与新框整篇排一遍的一致。逐句对照下整段框是收起的。
        var same = true;
        if (TranslatedText.IsVisible)
        {
            var fresh = new MarkdownBox();
            fresh.Show(_session?.Text ?? string.Empty, streaming: false);
            same = PlainText(TranslatedText.Document) == PlainText(fresh.Document);
        }

        latencies.Sort();
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var p95 = latencies.Count == 0 ? -1 : latencies[(int)Math.Floor(latencies.Count * 0.95)];
        return FormattableString.Invariant(
            $"stream|pass={pass}|state={_session?.State}|chars={_session?.Text.Length ?? 0}|seconds={Ms(streamTicks) / 1000:F1}|pings={latencies.Count}|inputMaxMs={(latencies.Count == 0 ? -1 : latencies[^1]):F0}|inputP95Ms={p95:F0}|longestOpMs={Ms(longest):F0}|busyPct={100.0 * busy / Math.Max(1, streamTicks):F0}|sameAsFresh={same}|pairs={SentencePairs.Items.Count}")
            + Environment.NewLine;

        static string PlainText(FlowDocument document)
            => new TextRange(document.ContentStart, document.ContentEnd).Text;
    }

    /// <summary>等这一轮翻译收尾（最多 5 秒），再让界面把排着的布局与渲染做完。</summary>
    private async Task Settled()
    {
        for (var waited = 0; waited < 50 && _session is { State: TranslationState.Idle or TranslationState.Streaming }; waited++)
        {
            await Task.Delay(100);
        }

        for (var round = 0; round < 4; round++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }
}
#endif
