using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 设置·翻译下的「提示词模板」（票 42）：每个模板一行，两个开关——"设为默认"与
/// "出现在切换里"。前者决定面板启动时用哪个模板，后者决定它在面板的模板按钮（以及日后
/// 反向输入框的 Ctrl+E）循环里出不出现；两者互相独立。
///
/// 数据是 <see cref="AppSettings.DefaultPromptTemplateId"/> 与
/// <see cref="AppSettings.TemplateCycle"/>；怎样落到设置上的逻辑在 Core 的
/// <see cref="PromptTemplates"/>（有测试），这里只负责摆控件、转达点击。不另做一个
/// 静态 Choice 再替换掉。
///
/// 控件按 <see cref="Refresh"/> 收到的设置现算：自己点一下写设置，store 的变更广播
/// 回来时同位刷新，别处（备份导入）改了设置也一样跟上。
/// </summary>
internal sealed class PromptTemplatesCard
{
    // 两列开关的宽度固定：每行是各自的 Grid，Auto 列对不齐。
    private const double DefaultColumnWidth = 92;
    private const double CycleColumnWidth = 100;

    private sealed record RowControls(ToggleButton Default, CheckBox Cycle);

    private readonly Func<AppSettings> _current;
    private readonly Func<Func<AppSettings, AppSettings>, bool> _update;

    private readonly StackPanel _root = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _notice = new();
    private readonly Dictionary<string, RowControls> _controls = [];

    /// <summary>当前行集合的指纹（id + 名字）：集合没变就原地刷新状态，不重建控件。</summary>
    private string _shape = string.Empty;

    public FrameworkElement Element => _root;

    /// <param name="current">最新设置（写失败后据此把开关拨回事实）。</param>
    /// <param name="update">
    /// 在最新设置上做一次增量写；失败时宿主已经在卡片里说了话，答 false。
    /// </param>
    public PromptTemplatesCard(
        AppSettings baseline,
        Func<AppSettings> current,
        Func<Func<AppSettings, AppSettings>, bool> update)
    {
        _current = current;
        _update = update;

        _root.Children.Add(BuildHeader());
        _root.Children.Add(_rows);
        _root.Children.Add(BuildNotice());

        Refresh(baseline);
    }

    /// <summary>按设置现算：行集合变了才重建，否则只把两个开关拨到设置说的那一档。</summary>
    public void Refresh(AppSettings settings)
    {
        var templates = PromptTemplates.All(settings);
        var shape = string.Join('\n', templates.Select(template => $"{template.Id}\t{template.Name}"));
        if (shape != _shape)
        {
            RebuildRows(templates);
            _shape = shape;
        }

        var defaultId = PromptTemplates.ResolveDefault(settings).Id;
        var inCycle = PromptTemplates.Cycle(settings).Select(template => template.Id).ToHashSet();

        foreach (var (id, row) in _controls)
        {
            var isDefault = id == defaultId;
            row.Default.IsChecked = isDefault;
            row.Default.Content = isDefault ? "默认" : "设为默认";
            row.Cycle.IsChecked = inCycle.Contains(id);
        }

        // 模板只在实际建出自备密钥后端时才生效（公共通道、免费引擎的 prompt 不在
        // 客户端）：与面板读同一个判断。开关仍可拨——设置先于生效。
        _notice.Visibility = settings.PromptTemplatesApply ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- building -------------------------------------------------------------------

    private static Grid ColumnsGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DefaultColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CycleColumnWidth) });
        return grid;
    }

    /// <summary>列头：两个开关各自是什么意思，行里就不必每行重复一遍。</summary>
    private static FrameworkElement BuildHeader()
    {
        var grid = ColumnsGrid();

        void Caption(string text, int column)
        {
            var label = new TextBlock
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
        }

        Caption("设为默认", 1);
        Caption("出现在切换里", 2);
        return grid;
    }

    private FrameworkElement BuildNotice()
    {
        _notice.Text = "当前翻译方式没有提示词这一层（公共通道与免费引擎的 prompt 不在客户端），模板暂不生效；改用自备密钥后启用。";
        _notice.TextWrapping = TextWrapping.Wrap;
        _notice.Margin = new Thickness(0, 8, 0, 0);
        _notice.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _notice.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        _notice.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
        _notice.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        return _notice;
    }

    private void RebuildRows(IReadOnlyList<PromptTemplate> templates)
    {
        _rows.Children.Clear();
        _controls.Clear();

        foreach (var (template, index) in templates.Select((template, index) => (template, index)))
        {
            if (index > 0)
            {
                var divider = new Border { Height = 1 };
                divider.SetResourceReference(Border.BackgroundProperty, "Brush.Divider");
                _rows.Children.Add(divider);
            }

            _rows.Children.Add(Row(template));
        }
    }

    private Grid Row(PromptTemplate template)
    {
        var grid = ColumnsGrid();
        grid.Margin = new Thickness(0, 8, 0, 8);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = template.Name, TextWrapping = TextWrapping.Wrap };
        name.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        words.Children.Add(name);

        var detail = new TextBlock
        {
            Text = Describe(template),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        detail.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        detail.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        words.Children.Add(detail);
        Grid.SetColumn(words, 0);
        grid.Children.Add(words);

        // 设为默认：一列里只有一个是默认，选中即 accent（设置窗是常规窗，不受
        // 浮层"静止态只有一处 accent"的约束）。点已是默认的那个不会取消它。
        var chip = new ToggleButton
        {
            Padding = new Thickness(10, 3, 10, 3),
            MinWidth = DefaultColumnWidth - 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        chip.SetResourceReference(FrameworkElement.StyleProperty, "SegmentChip");
        // 名字带上模板名：一列里有好几个"设为默认"，念出来要分得清是哪一个。
        AutomationProperties.SetName(chip, $"{template.Name}：设为默认");
        chip.Click += (_, _) => OnDefaultClicked(template.Id);
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);

        var cycle = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        cycle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        AutomationProperties.SetName(cycle, $"{template.Name}：出现在切换里");
        cycle.Click += (_, _) => OnCycleClicked(template.Id, cycle.IsChecked == true);
        Grid.SetColumn(cycle, 2);
        grid.Children.Add(cycle);

        _controls[template.Id] = new RowControls(chip, cycle);
        return grid;
    }

    /// <summary>一句话说清它是什么；内置的由我们写，自建的只有类别。</summary>
    private static string Describe(PromptTemplate template) => template.Id switch
    {
        PromptTemplate.StandardId => "翻译 · 与以往一字不差。",
        PromptTemplate.ColloquialId => "翻译 · 像母语者发消息的说法：意译优先，语气词有对应，网络用语译味道不译字面。",
        PromptTemplate.FormalId => "翻译 · 书面词汇、完整句，保留敬语与礼貌层级。",
        PromptTemplate.PromptOptimizeId => "改写 · 把随手写的需求整理成给 AI 编程助手的提示词，不替你补需求。",
        _ => template.Kind == PromptTemplateKind.Translate ? "翻译" : "改写",
    };

    // --- clicks ---------------------------------------------------------------------

    private void OnDefaultClicked(string id)
    {
        // 即改即生效（ADR-0012 #15）：写成功后 store 的广播会回到 Refresh；写失败
        // 宿主已经在卡片里说了话，这里把开关拨回事实。
        if (!_update(settings => PromptTemplates.SetDefault(settings, id)))
        {
            Refresh(_current());
        }
    }

    private void OnCycleClicked(string id, bool included)
    {
        if (!_update(settings => PromptTemplates.SetInCycle(settings, id, included)))
        {
            Refresh(_current());
        }
    }
}
