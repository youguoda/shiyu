using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 提示词模板的挑选列表（票 42；用户需求 2026-10-09：翻译面板也要像反向输入框一样挑）。
/// 每行左边是对勾与名字，右边是它的类别——翻译 / 改写（改写类不自动跑，按 Enter 才跑）。
/// 反向输入框把它装进自绘 Popup；翻译面板不接受焦点，把它直接长在面板里（见 PanelWindow）。
/// 行的样子只此一份。
/// </summary>
internal static class TemplateMenu
{
    public static Border Build(IEnumerable<PromptTemplate> templates, string currentId, Action<PromptTemplate> pick)
    {
        var host = new StackPanel { MinWidth = 200 };
        foreach (var template in templates)
        {
            host.Children.Add(Row(template, template.Id == currentId, pick));
        }

        return BarWindow.MenuSurface(host);
    }

    private static Button Row(PromptTemplate template, bool current, Action<PromptTemplate> pick)
    {
        var kindText = template.Kind == PromptTemplateKind.Rewrite ? "改写" : "翻译";

        var name = new TextBlock { Text = (current ? "✓  " : "     ") + template.Name };
        var kind = new TextBlock
        {
            Text = kindText,
            MinWidth = 36,
            Margin = new Thickness(16, 0, 0, 0),
            TextAlignment = TextAlignment.Right,
        };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        DockPanel.SetDock(kind, Dock.Right);

        var content = new DockPanel();
        content.Children.Add(kind);
        content.Children.Add(name);

        // 菜单行的样子（左对齐、拉满行宽、悬停色块）走共用的 MenuRowButton，与管理窗的菜单一致。
        var row = new Button { Content = content, Tag = template };
        row.SetResourceReference(FrameworkElement.StyleProperty, "MenuRowButton");
        AutomationProperties.SetName(row, $"{template.Name}（{kindText}）");
        row.Click += (_, _) => pick(template);
        return row;
    }
}
