#if DEBUG
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
