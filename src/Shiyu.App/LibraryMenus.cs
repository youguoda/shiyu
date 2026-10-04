using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 管理窗的弹层家族（票 24 / UI 报告 §6.4）：筛选弹层（类型分段 / 日期快捷
/// / 子类型 / 标签）、标签弹层、归组弹层、「⋯」菜单（备注 / 归组 / AI 处理 ›
/// / 全选 / 键位速查；底部危险组 Danger 色）与备注编辑器。弹层内容全部
/// code-behind 构建——它们是数据驱动的（标签列表、分组列表、选择数），XAML
/// 放不下这种动态。
/// </summary>
public partial class LibraryWindow
{
    private Popup? _filterPopup;
    private Popup? _tagPopup;
    private Popup? _groupPopup;
    private Popup? _morePopup;
    private Popup? _aiPopup;

    private ToggleButton[] _kindChips = [];
    private ToggleButton[] _dateChips = [];
    private DatePicker? _customFromPick;
    private DatePicker? _customToPick;
    private ToggleButton[] _subtypeChips = [];
    private readonly System.Windows.Controls.WrapPanel _tagFilterPanel = new();

    // --- 弹层公共件 ---------------------------------------------------------------

    /// <summary>弹层壳：Overlay 圆角、Surface 底、1 DIP 描边；Esc 在弹层内就地关闭。</summary>
    private static Popup MakePopup(FrameworkElement anchor, int width, UIElement content)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            PlacementRectangle = new Rect(0, 2, 0, 0),
            AllowsTransparency = true,
            StaysOpen = false,
        };

        var shell = new Border
        {
            Width = width,
            Padding = new Thickness(16, 12, 16, 14),
            Child = content,
        };
        shell.SetResourceReference(Border.CornerRadiusProperty, "Radius.Overlay");
        shell.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        shell.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        shell.BorderThickness = new Thickness(1);

        // Esc 在弹层里先关弹层（逐层的"层"从最里层剥起，§5.2）。
        shell.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                popup.IsOpen = false;
            }
        };

        popup.Child = shell;
        return popup;
    }

    private void CloseAllMenus()
    {
        foreach (var popup in new[] { _filterPopup, _tagPopup, _groupPopup, _morePopup, _aiPopup })
        {
            if (popup is { } open)
            {
                open.IsOpen = false;
            }
        }
    }

    private static TextBlock SectionLabel(string text)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 4) };
        label.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
        label.SetResourceReference(TextElement.FontWeightProperty, "Weight.Emphasis");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        return label;
    }

    private static ToggleButton FilterChip(string label)
    {
        var chip = new ToggleButton
        {
            Content = label,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
        };
        chip.SetResourceReference(StyleProperty, "SegmentChip");
        chip.SetResourceReference(System.Windows.Controls.Control.HeightProperty, "Control.HeightCompact");
        return chip;
    }

    /// <summary>菜单行：字形 + 文案 + 右端键帽（§5.2 L0：菜单里也教键位）；Danger 行红字。</summary>
    private static Button MenuRow(string label, string? glyph, Action onClick,
        bool danger = false, string? keyHint = null, string? toolTip = null)
    {
        var row = new Button
        {
            Height = 36,
            Padding = new Thickness(12, 0, 12, 0),
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        row.SetResourceReference(StyleProperty, "MenuRowButton");
        // 内容是面板（字形+文案+键帽），UIA 派生不出名字——就地按同一份
        // label/keyHint 合成（票 27 / U-29），格式与 KeyMap.AutomationName 一致。
        System.Windows.Automation.AutomationProperties.SetName(
            row, keyHint is { Length: > 0 } ? $"{label}（{keyHint}）" : label);
        if (danger)
        {
            row.SetResourceReference(Control.ForegroundProperty, "Brush.Danger");
        }

        var dock = new DockPanel { LastChildFill = false };

        if (glyph is not null)
        {
            var icon = new TextBlock { Text = glyph, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
            icon.SetResourceReference(TextBlock.FontSizeProperty, "Size.IconS");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
        }

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(glyph is null ? 0 : 10, 0, 0, 0),
        };
        text.SetResourceReference(TextBlock.FontSizeProperty, "Type.Body");
        DockPanel.SetDock(text, Dock.Left);
        dock.Children.Add(text);

        if (keyHint is not null)
        {
            var cap = new ContentControl { Content = keyHint, VerticalAlignment = VerticalAlignment.Center };
            cap.SetResourceReference(StyleProperty, "KeyCap");
            DockPanel.SetDock(cap, Dock.Right);
            dock.Children.Add(cap);
        }

        row.Content = dock;
        row.Click += (_, _) => onClick();
        return row;
    }

    // --- 筛选弹层（§6.4 头部）--------------------------------------------------------

    private void OnFilterButtonClick(object sender, RoutedEventArgs e)
    {
        if (_filterPopup is null)
        {
            return;
        }

        _filterPopup.IsOpen = !_filterPopup.IsOpen;
    }

    private void BuildFilterFlyout()
    {
        _kindChips =
        [
            FilterChip("全部"),
            FilterChip("文本"),
            FilterChip("图片"),
            FilterChip("文件"),
        ];
        _subtypeChips =
        [
            FilterChip("全部"),
            FilterChip("链接"),
            FilterChip("邮箱"),
            FilterChip("颜色"),
            FilterChip("路径"),
        ];
        _dateChips =
        [
            FilterChip("今天"),
            FilterChip("近 7 天"),
            FilterChip("近 30 天"),
        ];

        for (var i = 0; i < _kindChips.Length; i++)
        {
            var index = i;
            _kindChips[i].Click += (_, _) => SetKindFilter(index);
        }

        for (var i = 0; i < _subtypeChips.Length; i++)
        {
            var index = i;
            _subtypeChips[i].Click += (_, _) => SetSubtypeFilter(index);
        }

        for (var i = 0; i < _dateChips.Length; i++)
        {
            var choice = (DateChoice)(i + 1);
            _dateChips[i].Click += (_, _) => SetDateFilter(choice);
        }

        _customFromPick = new DatePicker { Width = 118 };
        _customToPick = new DatePicker { Width = 118, Margin = new Thickness(6, 0, 0, 0) };
        _customFromPick.SelectedDateChanged += OnCustomDateChanged;
        _customToPick.SelectedDateChanged += OnCustomDateChanged;

        var custom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        custom.Children.Add(new TextBlock { Text = "自定义", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        custom.Children.Add(_customFromPick);
        custom.Children.Add(new TextBlock { Text = "~", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
        custom.Children.Add(_customToPick);

        var body = new StackPanel();
        body.Children.Add(SectionLabel("类型"));
        var kinds = new System.Windows.Controls.WrapPanel();
        foreach (var chip in _kindChips)
        {
            kinds.Children.Add(chip);
        }

        body.Children.Add(kinds);
        body.Children.Add(SectionLabel("日期"));
        var dates = new StackPanel();
        var shortcuts = new System.Windows.Controls.WrapPanel();
        foreach (var chip in _dateChips)
        {
            shortcuts.Children.Add(chip);
        }

        dates.Children.Add(shortcuts);
        dates.Children.Add(custom);
        body.Children.Add(dates);
        body.Children.Add(SectionLabel("子类型"));
        var subtypes = new System.Windows.Controls.WrapPanel();
        foreach (var chip in _subtypeChips)
        {
            subtypes.Children.Add(chip);
        }

        body.Children.Add(subtypes);
        body.Children.Add(SectionLabel("标签"));
        body.Children.Add(_tagFilterPanel);

        _filterPopup = MakePopup(FilterButton, 300, body);
    }

    /// <summary>标签筛选芯片（数据驱动，标签列表会变）。</summary>
    private void SyncFilterFlyout()
    {
        _tagFilterPanel.Children.Clear();
        foreach (var tag in _store.AllTags())
        {
            var chosen = tag;
            var chip = FilterChip("#" + tag);
            chip.IsChecked = _tag == chosen;
            chip.Click += (_, _) => SetTagFilter(_tag == chosen ? null : chosen);
            _tagFilterPanel.Children.Add(chip);
        }

        if (_tagFilterPanel.Children.Count == 0)
        {
            var none = new TextBlock { Text = "还没有标签——在列表里选中条目后按 T 添加。" };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
            none.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
            none.TextWrapping = TextWrapping.Wrap;
            _tagFilterPanel.Children.Add(none);
        }

        for (var i = 0; i < _kindChips.Length; i++)
        {
            _kindChips[i].IsChecked = _kindIndex == i;
        }

        for (var i = 0; i < _subtypeChips.Length; i++)
        {
            _subtypeChips[i].IsChecked = _subtypeIndex == i;
        }

        for (var i = 0; i < _dateChips.Length; i++)
        {
            _dateChips[i].IsChecked = (int)_dateChoice == i + 1;
        }
    }

    private void SetKindFilter(int index)
    {
        _kindIndex = index == _kindIndex ? 0 : index;
        if (_kindIndex == 0)
        {
            _tokenOrder.Remove("kind");
        }
        else
        {
            TouchToken("kind");
        }

        RefreshTokens();
        SyncFilterFlyout();
        ApplyFilter();
    }

    private void SetSubtypeFilter(int index)
    {
        _subtypeIndex = index == _subtypeIndex ? 0 : index;
        if (_subtypeIndex == 0)
        {
            _tokenOrder.Remove("subtype");
        }
        else
        {
            TouchToken("subtype");
        }

        RefreshTokens();
        SyncFilterFlyout();
        ApplyFilter();
    }

    private void SetDateFilter(DateChoice choice)
    {
        _dateChoice = choice;
        TouchToken("date");
        RefreshTokens();
        SyncFilterFlyout();
        ApplyFilter();
    }

    private void SetTagFilter(string? tag)
    {
        _tag = tag;
        if (tag is null)
        {
            _tokenOrder.Remove("tag");
        }
        else
        {
            TouchToken("tag");
        }

        RefreshTokens();
        SyncFilterFlyout();
        ApplyFilter();
    }

    /// <summary>自定义日期：两端都选好才生效为一枚 token。</summary>
    private void OnCustomDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_customFromPick?.SelectedDate is not { } || _customToPick?.SelectedDate is not { })
        {
            return;
        }

        _dateChoice = DateChoice.Custom;
        TouchToken("date");
        RefreshTokens();
        SyncFilterFlyout();
        ApplyFilter();
    }

    // --- 标签弹层（命令栏「标签 ▾」/ T / 详情属性区）-----------------------------------

    private TextBox? _tagBox;

    private void OnTagButtonClick(object sender, RoutedEventArgs e) => OpenTagMenu();

    private void OpenTagMenu()
    {
        CloseAllMenus();
        _tagPopup = BuildTagFlyout();
        _tagPopup.IsOpen = true;
        _tagBox?.Focus();
    }

    private Popup BuildTagFlyout()
    {
        var selected = SelectedItems();
        var primaryTags = Primary is { } primary && _store.Get(primary.Id) is { } entry
            ? entry.Tags
            : [];

        var body = new StackPanel();

        var title = new TextBlock
        {
            Text = selected.Count > 1 ? $"给已选 {selected.Count} 条加标签" : "加标签",
        };
        title.SetResourceReference(TextElement.FontWeightProperty, "Weight.Emphasis");
        body.Children.Add(title);

        _tagBox = new TextBox { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
        InputProps.SetPlaceholder(_tagBox, "新标签名，回车添加");
        _tagBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                AddTagToSelection(_tagBox.Text);
                _tagBox.Clear();
                if (_tagPopup is { } open)
                {
                    open.IsOpen = false;
                }
            }
        };
        body.Children.Add(_tagBox);

        var add = new Button
        {
            Content = "加到所选",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 8, 0, 0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        add.SetResourceReference(StyleProperty, "FlyoutButton");
        add.Click += (_, _) =>
        {
            if (_tagBox is null)
            {
                return;
            }

            AddTagToSelection(_tagBox.Text);
            _tagBox.Clear();
            if (_tagPopup is { } popup)
            {
                popup.IsOpen = false;
            }
        };
        body.Children.Add(add);

        if (primaryTags.Count > 0)
        {
            body.Children.Add(SectionLabel("这条记录的标签（点掉即移除）"));
            foreach (var tag in primaryTags)
            {
                var remove = tag;
                body.Children.Add(MenuRow("#" + remove, "\uE8EC", () =>
                {
                    RemoveTagFromSelection(remove);
                    if (_tagPopup is { } popup)
                    {
                        popup.IsOpen = false;
                    }
                }));
            }
        }

        _tagPopup?.SetValue(Popup.IsOpenProperty, false);
        return MakePopup(TagButton, 260, body);
    }
    // --- 归组弹层（G / ⋯ 归组 / 批量面板）----------------------------------------------

    private void OpenGroupMenu()
    {
        CloseAllMenus();
        var primary = Primary;
        var currentGroup = primary is not null && _store.Get(primary.Id) is { } entry
            ? _store.GroupOf(entry)?.Id
            : null;

        var body = new StackPanel();

        var title = new TextBlock { Text = "把所选移进哪组" };
        title.SetResourceReference(TextElement.FontWeightProperty, "Weight.Emphasis");
        body.Children.Add(title);

        body.Children.Add(MenuRow("未分组", null, () => AssignGroup(null),
            keyHint: KeyMap.HintKey("group", KeySurface.Library),
            toolTip: "移出所在分组"));
        foreach (var group in _store.Groups())
        {
            var target = group.Id;
            var row = MenuRow($"{group.Icon ?? "🗂"} {group.Name}", null,
                () => AssignGroup(target), keyHint: null);
            if (group.Id == currentGroup)
            {
                row.SetResourceReference(Control.FontWeightProperty, "Weight.Emphasis");
            }

            body.Children.Add(row);
        }

        body.Children.Add(new System.Windows.Controls.Border
        {
            Height = 1,
            Margin = new Thickness(0, 6, 0, 6),
        }.WithResource(System.Windows.Controls.Border.BackgroundProperty, "Brush.Divider"));
        body.Children.Add(MenuRow("管理分组…", "\uE8EC", () =>
        {
            var manager = new GroupManagerWindow(_store) { Owner = this };
            manager.Show();
        }));

        _groupPopup = MakePopup(TagButton, 240, body);
        _groupPopup.PlacementTarget = MoreButton;
        _groupPopup.IsOpen = true;
    }

    private void AssignGroup(long? groupId)
    {
        var selected = SelectedItems();
        foreach (var item in selected)
        {
            SelfWrite(() => _store.SetEntryGroup(item.Id, groupId));
        }

        Reload();
        ShowLightFeedback(groupId is null
            ? $"已把 {selected.Count} 条移出分组"
            : $"已把 {selected.Count} 条归入「{_store.Groups().FirstOrDefault(g => g.Id == groupId)?.Name}」");
    }

    // --- ⋯ 菜单（§6.4 命令栏右端）-----------------------------------------------------

    private readonly List<Button> _aiRows = [];

    private void OnMoreButtonClick(object sender, RoutedEventArgs e)
    {
        CloseAllMenus();
        if (_morePopup is { } popup)
        {
            popup.IsOpen = true;
        }
    }

    private void BuildMoreMenu()
    {
        var body = new StackPanel();

        // 加速键列从 KeyMap 解析（键位即数据）：菜单、速查、键帽、自动化名
        // 四处同源，改键只改 Core 那张表。HintKey 取第一等价键——菜单列
        // 与键帽一样只装一个短形。
        string? Key(string id) => KeyMap.HintKey(id, KeySurface.Library);

        body.Children.Add(MenuRow("备注", "\uE70B", EditNoteSelection, keyHint: Key("note")));
        body.Children.Add(MenuRow("归组", "\uE8EC", OpenGroupMenu, keyHint: Key("group")));

        var aiRow = MenuRow("AI 处理", "\uE945", OpenAiMenu);
        body.Children.Add(aiRow);

        body.Children.Add(MenuRow("全选", "\uE8FD", () => EntryList.SelectAll(), keyHint: Key("select-all")));
        body.Children.Add(MenuRow("键位速查", "\uE8FD", ShowCheatSheet, keyHint: Key("cheatsheet")));

        // 危险组（§6.4）：距离 + Danger 色 + 对话框，三重警告的第一重。
        body.Children.Add(new System.Windows.Controls.Border
        {
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6),
        }.WithResource(System.Windows.Controls.Border.BackgroundProperty, "Brush.Divider"));
        body.Children.Add(MenuRow("按时间段删除…", "\uE74D", AskDeleteRange, danger: true,
            toolTip: "不可撤销——会先弹确认对话框"));
        body.Children.Add(MenuRow("清空全部历史…", "\uE74D", AskClearAll, danger: true,
            toolTip: "不可撤销——会先弹确认对话框，可先导出备份"));

        _morePopup = MakePopup(MoreButton, 250, body);
    }

    private void OpenAiMenu()
    {
        if (_aiPopup is { } open)
        {
            open.IsOpen = false;
        }

        var selected = SelectedItems();
        var texts = selected.Count(item => item.Kind == EntryKind.Text);

        var body = new StackPanel();
        var rows = new List<(string Label, AgentActionKind Kind)>
        {
            ($"总结（{selected.Count} 条）", AgentActionKind.Summarise),
            ("合并成笔记", AgentActionKind.MergeIntoNote),
            ("改写", AgentActionKind.Rewrite),
            ("建议标签", AgentActionKind.SuggestTags),
        };

        _aiRows.Clear();
        foreach (var (label, kind) in rows)
        {
            var row = MenuRow(label, null, () =>
            {
                CloseAllMenus();
                RunAgent(kind);
            });
            _aiRows.Add(row);
            body.Children.Add(row);
        }

        var translate = MenuRow($"批量翻译存入历史（{texts} 条文本）", null, () =>
        {
            CloseAllMenus();
            RunTranslateBatch();
        }, toolTip: "逐条翻译成设置里的译文语言，结果存为带「译」标的关联条目");
        _aiRows.Add(translate);
        body.Children.Add(translate);

        _aiPopup = MakePopup(MoreButton, 280, body);
        _aiPopup.Placement = PlacementMode.Right;
        _aiPopup.PlacementTarget = _morePopup?.Child ?? MoreButton;
        _aiPopup.IsOpen = true;
        SyncMoreMenuActions();
    }

    /// <summary>AI 入口随配置亮起（ADR-0009：自备密钥才供得起）。</summary>
    private void SyncMoreMenuActions()
    {
        var ready = _settings().Backend.IsConfigured;
        foreach (var row in _aiRows)
        {
            row.IsEnabled = ready;
            row.ToolTip = ready ? null : "需要自备密钥 · 去 设置 → 翻译";
        }
    }

    // --- 备注编辑器（N；与窄条同款：一个框，三个出口）------------------------------------

    private void EditNoteSelection()
    {
        var selected = SelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        var existing = _store.Get(selected[0].Id)?.Note ?? string.Empty;

        var editor = new Window
        {
            Title = selected.Count > 1 ? $"备注 · 已选 {selected.Count} 条" : "备注",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        editor.SetResourceReference(Window.BackgroundProperty, "Brush.Background");

        var box = new TextBox
        {
            Text = existing,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            Margin = new Thickness(12),
            Padding = new Thickness(6, 4, 6, 4),
        };
        box.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Brush.SurfaceInput");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 12),
        };

        var save = new Button { Content = "保存", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand };
        var remove = new Button { Content = "删除备注", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand };
        var cancel = new Button { Content = "取消", Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand };

        save.Click += (_, _) =>
        {
            foreach (var item in selected)
            {
                var text = box.Text;
                SelfWrite(() => _store.SetNote(item.Id, text));
            }

            editor.Close();
            Reload();
            ShowLightFeedback($"已保存备注（{selected.Count} 条）");
        };

        remove.Click += (_, _) =>
        {
            foreach (var item in selected)
            {
                SelfWrite(() => _store.SetNote(item.Id, null));
            }

            editor.Close();
            Reload();
            ShowLightFeedback("已删除备注");
        };

        cancel.Click += (_, _) => editor.Close();

        buttons.Children.Add(cancel);
        buttons.Children.Add(remove);
        buttons.Children.Add(save);

        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        editor.Content = panel;

        box.Focus();
        box.SelectAll();
        editor.ShowDialog();
    }
}

/// <summary>给 code-behind 建的元素挂资源引用的小工具（链式）。</summary>
internal static class ElementResource
{
    public static T WithResource<T>(this T element, DependencyProperty property, string key)
        where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
