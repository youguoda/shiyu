using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 设置·翻译下的「提示词模板」（票 42）。每个模板一行，两个开关——"设为默认"与
/// "出现在切换里"：前者决定面板启动时用哪个模板，后者决定它在面板的模板按钮（以及日后
/// 反向输入框的 Ctrl+E）循环里出不出现，两者互相独立。
///
/// 内置模板在前、只读，带「复制为自建」；之后是自建的，可以新建、编辑、删除。编辑框
/// 只有三项：名字、提示词、"出现在切换里"。不暴露温度和模型——CONTEXT.md 说了不做高级
/// 参数面板。同一个控件里做完，不另做一个静态 Choice 再替换掉。
///
/// 数据是 <see cref="AppSettings.DefaultPromptTemplateId"/>、<see cref="AppSettings.TemplateCycle"/>
/// 与 <see cref="AppSettings.PromptTemplates"/>；怎样落到设置上、怎样校验的逻辑在 Core 的
/// <see cref="PromptTemplates"/>（有测试），这里只负责摆控件、转达点击、把校验的话说给
/// 用户。写设置一律走 SettingsStore：这是在设置窗里写，属于常规路径，不涉及面板 Esc
/// 那个问题。
///
/// 控件按 <see cref="Refresh"/> 收到的设置现算：自己点一下写设置，store 的变更广播回来
/// 时同位刷新，别处（备份导入）改了设置也一样跟上。
/// </summary>
internal sealed class PromptTemplatesCard
{
    // 两列开关的宽度固定：每行是各自的 Grid，Auto 列对不齐。
    private const double DefaultColumnWidth = 92;
    private const double CycleColumnWidth = 100;

    /// <summary>自建模板在行里的预览长度：一眼认出是哪个，不占成一大段。</summary>
    private const int PreviewLength = 36;

    private sealed record RowControls(ToggleButton Default, CheckBox Cycle);

    private readonly Func<AppSettings> _current;
    private readonly Func<Func<AppSettings, AppSettings>, bool> _update;

    private readonly StackPanel _root = new();
    private readonly StackPanel _builtInRows = new();
    private readonly StackPanel _customRows = new();
    private readonly TextBlock _noCustom = new();
    private readonly TextBlock _notice = new();
    private readonly Dictionary<string, RowControls> _controls = [];

    // 编辑器：新建、编辑与复制出来的副本共用同一块。
    private readonly Border _editor = new();
    private readonly TextBlock _editorTitle = new();
    private readonly TextBox _name = new();
    private readonly TextBox _prompt = new();
    private readonly CheckBox _inCycle = new();
    private readonly TextBlock _editorError = new();

    /// <summary>正在编辑的模板 id；null 表示新建（保存时才发新 id）。</summary>
    private string? _editingId;

    /// <summary>当前行集合的指纹（id + 名字 + 预览）：集合没变就原地刷新状态，不重建控件。</summary>
    private string _shape = string.Empty;

    public FrameworkElement Element => _root;

    /// <param name="current">最新设置（写失败后据此把开关拨回事实、校验时取最新的模板列表）。</param>
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
        _root.Children.Add(_builtInRows);
        _root.Children.Add(BuildCustomSection());
        _root.Children.Add(BuildEditor());
        _root.Children.Add(BuildNotice());

        Refresh(baseline);
    }

    /// <summary>按设置现算：行集合变了才重建，否则只把两个开关拨到设置说的那一档。</summary>
    public void Refresh(AppSettings settings)
    {
        var templates = PromptTemplates.All(settings);
        var shape = string.Join('\n', templates.Select(template => $"{template.Id}\t{template.Name}\t{Preview(template)}"));
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

    private static TextBlock Caption(string text, Thickness? margin = null)
    {
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = margin ?? new Thickness(0),
        };
        label.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        label.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        return label;
    }

    /// <summary>列头：两个开关各自是什么意思，行里就不必每行重复一遍。</summary>
    private static FrameworkElement BuildHeader()
    {
        var grid = ColumnsGrid();

        void Add(string text, int column)
        {
            var label = Caption(text);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
        }

        Add("设为默认", 1);
        Add("出现在切换里", 2);
        return grid;
    }

    private FrameworkElement BuildCustomSection()
    {
        var section = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "Brush.Divider");
        section.Children.Add(divider);

        var heading = new TextBlock { Text = "我的模板", Margin = new Thickness(0, 12, 0, 0) };
        heading.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        section.Children.Add(heading);

        _noCustom.Text = "还没有自建模板。想加什么加什么：润色、文言文、代码注释……";
        _noCustom.TextWrapping = TextWrapping.Wrap;
        _noCustom.Margin = new Thickness(0, 6, 0, 0);
        _noCustom.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        _noCustom.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        section.Children.Add(_noCustom);
        section.Children.Add(_customRows);

        var add = new Button
        {
            Content = "新建模板",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
        };
        add.Click += (_, _) => OpenEditor(
            id: null, title: "新建模板", name: string.Empty, prompt: string.Empty, inCycle: true);
        section.Children.Add(add);

        return section;
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
        _builtInRows.Children.Clear();
        _customRows.Children.Clear();
        _controls.Clear();

        var builtIn = templates.Where(template => template.IsBuiltIn).ToList();
        var custom = templates.Where(template => !template.IsBuiltIn).ToList();

        AddRows(_builtInRows, builtIn);
        AddRows(_customRows, custom);
        _noCustom.Visibility = custom.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        void AddRows(StackPanel host, List<PromptTemplate> rows)
        {
            foreach (var (template, index) in rows.Select((template, index) => (template, index)))
            {
                if (index > 0)
                {
                    var divider = new Border { Height = 1 };
                    divider.SetResourceReference(Border.BackgroundProperty, "Brush.Divider");
                    host.Children.Add(divider);
                }

                host.Children.Add(Row(template));
            }
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
        words.Children.Add(Caption(Describe(template), new Thickness(0, 2, 0, 0)));

        // 行内动作放在说明下面，不另占一列：两列开关已经占了 192，再加一列文字会被挤没。
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-8, 2, 0, 0) };
        if (template.IsBuiltIn)
        {
            actions.Children.Add(ActionLink(
                "复制为自建", $"复制「{template.Name}」为自建模板", () => Duplicate(template)));
        }
        else
        {
            actions.Children.Add(ActionLink("编辑", $"编辑「{template.Name}」", () => Edit(template)));
            actions.Children.Add(ActionLink("删除", $"删除「{template.Name}」", () => Delete(template), danger: true));
        }

        words.Children.Add(actions);
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

    /// <summary>行内文字动作：次要、不抢眼；名字写全（"编辑「润色」"），一列里有好几个"编辑"。</summary>
    private static Button ActionLink(string text, string accessibleName, Action click, bool danger = false)
    {
        var label = new TextBlock { Text = text };
        label.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        label.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Brush.Danger" : "Brush.Accent");

        var button = new Button
        {
            Content = label,
            Padding = new Thickness(8, 4, 8, 4),
            MinHeight = 0,
            Cursor = Cursors.Hand,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "FlyoutButton");
        AutomationProperties.SetName(button, accessibleName);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>一句话说清它是什么；内置的由我们写，自建的带正文开头的一小段。</summary>
    private static string Describe(PromptTemplate template) => template.Id switch
    {
        PromptTemplate.StandardId => "翻译 · 与以往一字不差。",
        PromptTemplate.ColloquialId => "翻译 · 像母语者发消息的说法：意译优先，语气词有对应，网络用语译味道不译字面。",
        PromptTemplate.FormalId => "翻译 · 书面词汇、完整句，保留敬语与礼貌层级。",
        PromptTemplate.PromptOptimizeId => "改写 · 把随手写的需求整理成给 AI 编程助手的提示词，不替你补需求。",
        _ => $"改写 · {Preview(template)}",
    };

    /// <summary>自建模板正文开头的一小段（折成一行）；内置模板没有。</summary>
    private static string Preview(PromptTemplate template)
    {
        if (template.IsBuiltIn || template.Text is not { } text)
        {
            return string.Empty;
        }

        var oneLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return oneLine.Length <= PreviewLength ? oneLine : oneLine[..PreviewLength] + "…";
    }

    // --- the editor -----------------------------------------------------------------

    private FrameworkElement BuildEditor()
    {
        var stack = new StackPanel();

        _editorTitle.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        _editorTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        stack.Children.Add(_editorTitle);

        stack.Children.Add(Caption("名字", new Thickness(0, 10, 0, 4)));
        _name.Padding = new Thickness(8, 4, 8, 4);
        _name.SetResourceReference(TextBox.BackgroundProperty, "Brush.SurfaceInput");
        AutomationProperties.SetName(_name, "模板名字");
        stack.Children.Add(_name);
        stack.Children.Add(Caption(
            "建议不超过 6 个字：面板上的按钮放不下会截断，悬停可以看到全名。", new Thickness(0, 4, 0, 0)));

        stack.Children.Add(Caption("提示词", new Thickness(0, 10, 0, 4)));
        _prompt.AcceptsReturn = true;
        _prompt.TextWrapping = TextWrapping.Wrap;
        _prompt.Height = 140;
        _prompt.VerticalAlignment = VerticalAlignment.Top;
        _prompt.VerticalContentAlignment = VerticalAlignment.Top;
        _prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _prompt.Padding = new Thickness(8, 4, 8, 4);
        _prompt.SetResourceReference(TextBox.BackgroundProperty, "Brush.SurfaceInput");
        AutomationProperties.SetName(_prompt, "提示词");
        stack.Children.Add(_prompt);

        // 插入 {target}：说明写在按钮旁，把"写不写"的后果讲清楚。
        var insert = new Button
        {
            Content = "插入 {target}",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            ToolTip = "在光标处插入 {target}，运行时代入译文语言",
        };
        insert.Click += (_, _) => InsertTarget();
        stack.Children.Add(insert);
        stack.Children.Add(Caption(
            "写了 {target} 会代入译文语言，面板头部显示方向；不写则由提示词自己决定输出语言，头部只显示模板名。",
            new Thickness(0, 4, 0, 0)));

        var cycleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _inCycle.VerticalAlignment = VerticalAlignment.Center;
        _inCycle.Cursor = Cursors.Hand;
        _inCycle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        AutomationProperties.SetName(_inCycle, "出现在切换里");
        cycleRow.Children.Add(_inCycle);
        var cycleLabel = new TextBlock { Text = "出现在切换里", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        cycleLabel.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        cycleLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        cycleRow.Children.Add(cycleLabel);
        stack.Children.Add(cycleRow);

        // 校验的话长在编辑框里：错误离它出错的字段一步之遥（§3.9 P1）。
        _editorError.TextWrapping = TextWrapping.Wrap;
        _editorError.Margin = new Thickness(0, 8, 0, 0);
        _editorError.Visibility = Visibility.Collapsed;
        _editorError.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _editorError.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        _editorError.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
        _editorError.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        AutomationProperties.SetLiveSetting(_editorError, AutomationLiveSetting.Assertive);
        stack.Children.Add(_editorError);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button
        {
            Content = "保存",
            Padding = new Thickness(14, 4, 14, 4),
            Cursor = Cursors.Hand,
        };
        save.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        save.Click += (_, _) => SaveEditor();
        var cancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        cancel.Click += (_, _) => CloseEditor();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);

        _editor.Child = stack;
        _editor.Padding = new Thickness(12);
        _editor.Margin = new Thickness(0, 12, 0, 0);
        _editor.BorderThickness = new Thickness(1);
        _editor.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSubtle");
        _editor.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _editor.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        _editor.Visibility = Visibility.Collapsed;

        // Esc 收起编辑框，Ctrl+Enter 保存；其余按键照常进输入框。
        _editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseEditor();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveEditor();
                e.Handled = true;
            }
        };

        return _editor;
    }

    private void OpenEditor(string? id, string title, string name, string prompt, bool inCycle)
    {
        _editingId = id;
        _editorTitle.Text = title;
        _name.Text = name;
        _prompt.Text = prompt;
        _inCycle.IsChecked = inCycle;
        _editorError.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Visible;
        _name.Focus();
        _editor.BringIntoView();
    }

    private void CloseEditor()
    {
        _editingId = null;
        _editor.Visibility = Visibility.Collapsed;
    }

    private void ShowEditorError(string message)
    {
        _editorError.Text = message;
        _editorError.Visibility = Visibility.Visible;
    }

    /// <summary>在光标处插入 {target}；选中了文字就替换它。</summary>
    private void InsertTarget()
    {
        var start = _prompt.SelectionStart;
        _prompt.SelectedText = PromptTemplate.TargetPlaceholder;
        _prompt.SelectionStart = start + PromptTemplate.TargetPlaceholder.Length;
        _prompt.SelectionLength = 0;
        _prompt.Focus();
    }

    private void Edit(PromptTemplate template)
    {
        var inCycle = PromptTemplates.Cycle(_current()).Any(entry => entry.Id == template.Id);
        OpenEditor(template.Id, $"编辑「{template.Name}」", template.Name, template.Text ?? string.Empty, inCycle);
    }

    /// <summary>
    /// 复制为自建：副本立刻存下，再在编辑框里改（"复制为自建，再在副本上改"）。取消
    /// 编辑只是不改它，副本仍在，要不要留由用户删。
    /// </summary>
    private void Duplicate(PromptTemplate source)
    {
        var copy = PromptTemplates.Duplicate(_current(), source);
        if (_update(settings => PromptTemplates.AddCustom(settings, copy, inCycle: false)))
        {
            OpenEditor(copy.Id, $"编辑「{copy.Name}」", copy.Name, copy.Prompt, inCycle: false);
        }
    }

    private void SaveEditor()
    {
        var isNew = _editingId is null;
        var candidate = new StoredPromptTemplate(
            _editingId ?? PromptTemplates.NewId(), _name.Text.Trim(), _prompt.Text.Trim());

        if (PromptTemplates.Validate(_current(), candidate, isNew) is { } problem)
        {
            ShowEditorError(problem);
            return;
        }

        var inCycle = _inCycle.IsChecked == true;
        if (!_update(settings => isNew
                ? PromptTemplates.AddCustom(settings, candidate, inCycle)
                : PromptTemplates.UpdateCustom(settings, candidate, inCycle)))
        {
            // 写盘失败：宿主已经在卡片里说了话，编辑框留着，用户可以重试。
            return;
        }

        // 校验之后到落盘之前，别处（备份导入）可能又改了设置——存没存上，以设置为准。
        var saved = PromptTemplates.Custom(_current()).FirstOrDefault(template => template.Id == candidate.Id);
        if (saved is null || saved.Name != candidate.Name || saved.Text != candidate.Prompt)
        {
            ShowEditorError("没能保存：模板在别处被改动了，请再试一次。");
            return;
        }

        CloseEditor();
    }

    private void Delete(PromptTemplate template)
    {
        var settings = _current();
        var isDefault = PromptTemplates.ResolveDefault(settings).Id == template.Id;

        // 删除正被用作默认的模板时，确认框写明"将回落为标准"。自建的措辞归用户，
        // 删了就没了，所以一律确认；按钮写全动词与对象（§6.5），Enter 落在取消上。
        var body = isDefault
            ? $"「{template.Name}」正被用作默认模板，删除后将回落为标准。删除后无法恢复。"
            : $"删除「{template.Name}」？删除后无法恢复。";

        var answer = ContentDialog.Show(
            Window.GetWindow(_root) ?? Application.Current.MainWindow,
            "删除模板",
            body,
            new ContentDialogButton("取消", ContentDialogButtonStyle.Standard, IsCancelFocus: true),
            new ContentDialogButton($"删除「{template.Name}」", ContentDialogButtonStyle.Danger));

        if (answer != 1)
        {
            return;
        }

        if (_update(latest => PromptTemplates.DeleteCustom(latest, template.Id)) && _editingId == template.Id)
        {
            CloseEditor();
        }
    }

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
