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

        async Task Settled()
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
