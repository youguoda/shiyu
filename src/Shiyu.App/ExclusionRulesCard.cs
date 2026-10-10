using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 排除规则三层（§5.1「记录与隐私」）：内置清单一只读 chip（密码管理器，
/// 始终生效）；「我的排除」从正在运行的应用添加、点 ✕ 移除；re: 正则
/// 高级规则折叠收起。设置与引导的能力从此对等，而设置给出的是全部三层。
///
/// 数据仍是 <see cref="AppSettings.ExclusionRules"/> 的同一份（应用名 +
/// 内容正则），编辑即落盘——每一步增删都走即改即生效的提交回调。
/// </summary>
internal sealed class ExclusionRulesCard
{
    private readonly Action<string> _commit;
    private readonly StackPanel _root = new();

    private readonly StackPanel _builtInHost = new() { Visibility = Visibility.Collapsed };
    private readonly WrapPanel _mineHost = new();
    private readonly TextBox _patterns = new();

    private List<StoredExclusionRule> _mine;

    public FrameworkElement Element => _root;

    public ExclusionRulesCard(AppSettings baseline, Action<string> commit)
    {
        _commit = commit;

        // 我的排除 = 用户自己加的（应用名与正则都算；正则只显示在高级层）。
        var builtIn = ExclusionPolicy.Presets
            .Select(rule => rule.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _mine = baseline.ExclusionRules
            .Where(rule => !builtIn.Contains(rule.Value) || rule.Kind == ExclusionRuleKind.ContentPattern)
            .ToList();

        BuildBuiltIn(builtIn.Count);
        BuildMine();
        BuildAdvanced();
        RefreshMine();
    }

    // --- layer 1: the built-in list, read-only chips --------------------------

    private void BuildBuiltIn(int count)
    {
        var chevron = new TextBlock { Text = "\uE70D", Margin = new Thickness(6, 0, 0, 0) };
        chevron.SetResourceReference(TextElement.FontFamilyProperty, "Font.Icon");
        chevron.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");

        var toggle = new ToggleButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = $"已内置 {count} 个" },
                    chevron,
                },
            },
            Padding = new Thickness(10, 3, 8, 3),
            Cursor = Cursors.Hand,
            ToolTip = "密码管理器等敏感来源始终排除，无需重复填写",
        };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "SegmentChip");

        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var name in ExclusionPolicy.Presets.Select(rule => rule.Value))
        {
            chips.Children.Add(Chip(name, removable: false));
        }

        _builtInHost.Children.Add(chips);
        toggle.Checked += (_, _) => _builtInHost.Visibility = Visibility.Visible;
        toggle.Unchecked += (_, _) => _builtInHost.Visibility = Visibility.Collapsed;

        _root.Children.Add(toggle);
        _root.Children.Add(_builtInHost);
    }

    // --- layer 2: my exclusions, chips removable, added from running apps -----

    private void BuildMine()
    {
        var heading = new TextBlock
        {
            Text = "我的排除",
            Margin = new Thickness(0, 12, 0, 0),
        };
        heading.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _root.Children.Add(heading);

        _mineHost.Margin = new Thickness(0, 6, 0, 0);
        _root.Children.Add(_mineHost);

        var add = new Button
        {
            Content = "从正在运行的应用添加…",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 8, 0, 0),
            Cursor = Cursors.Hand,
        };
        add.Click += (_, _) => ShowRunningAppsPicker(add);
        _root.Children.Add(add);
    }

    private void ShowRunningAppsPicker(Button anchor)
    {
        var list = new StackPanel();

        var search = new TextBox { Padding = new Thickness(8, 4, 8, 4) };
        search.SetResourceReference(TextBox.BackgroundProperty, "Brush.SurfaceInput");
        InputProps.SetPlaceholder(search, "搜索应用名");
        list.Children.Add(search);

        var known = _mine
            .Where(rule => rule.Kind == ExclusionRuleKind.SourceApp)
            .Select(rule => rule.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builtIn = ExclusionPolicy.Presets
            .Select(rule => rule.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] names = [];
        try
        {
            names = Process.GetProcesses()
                .Select(process => process.ProcessName)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !name.Equals("Shiyu.App", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            // expected: 枚举进程失败（权限）——列表为空，手填仍可用；这不
            // 是故障，弹层自己说"没有匹配的进程"。
        }

        var picks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new StackPanel();
        var scroller = new ScrollViewer
        {
            Content = rows,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Margin = new Thickness(0, 8, 0, 0),
        };

        void Rebuild(string filter)
        {
            rows.Children.Clear();
            foreach (var name in names.Where(name => filter.Length == 0
                         || name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(30))
            {
                var captured = name;
                var already = known.Contains(name) || builtIn.Contains(name);
                var box = new CheckBox
                {
                    Content = name,
                    IsChecked = false,
                    IsEnabled = !already,
                    Margin = new Thickness(0, 1, 12, 1),
                    Cursor = Cursors.Hand,
                    ToolTip = already ? "已在排除清单里" : "勾选后不再记录来自它的复制",
                };
                box.Checked += (_, _) => picks.Add(captured);
                box.Unchecked += (_, _) => picks.Remove(captured);
                rows.Children.Add(box);
            }

            if (rows.Children.Count == 0)
            {
                var none = new TextBlock { Text = "没有匹配的进程——用下面的输入框手填。", Margin = new Thickness(0, 4, 0, 4) };
                none.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
                none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                rows.Children.Add(none);
            }
        }

        Rebuild(string.Empty);
        search.TextChanged += (_, _) => Rebuild(search.Text.Trim());

        list.Children.Add(scroller);

        var manual = new TextBox { Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 8, 0, 0) };
        manual.SetResourceReference(TextBox.BackgroundProperty, "Brush.SurfaceInput");
        InputProps.SetPlaceholder(manual, "或手填应用名，回车加入");
        list.Children.Add(manual);

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            MinWidth = 280,
        };

        void AddNames(IEnumerable<string> namesToAdd)
        {
            var added = false;
            foreach (var name in namesToAdd)
            {
                var trimmed = name.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (_mine.Any(rule => rule.Kind == ExclusionRuleKind.SourceApp
                        && rule.Value.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _mine.Add(new StoredExclusionRule(ExclusionRuleKind.SourceApp, trimmed));
                added = true;
            }

            if (added)
            {
                Save();
            }
        }

        manual.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddNames([manual.Text]);
                manual.Clear();
                e.Handled = true;
            }
        };

        var done = new Button
        {
            Content = "完成",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand,
        };
        done.Click += (_, _) =>
        {
            AddNames(picks);
            popup.IsOpen = false;
        };
        list.Children.Add(done);

        var surface = new Border
        {
            Child = list,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 4, 8, 8),
        };
        surface.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        surface.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        surface.BorderThickness = new Thickness(1);
        surface.SetResourceReference(Border.CornerRadiusProperty, "Radius.Overlay");

        // 投影走共享的浮层阴影（§4.7 浮层家族）。
        surface.SetResourceReference(Border.EffectProperty, "Shadow.Floating");
        popup.Child = surface;
        popup.IsOpen = true;
        search.Focus();
    }

    private void RefreshMine()
    {
        _mineHost.Children.Clear();

        var apps = _mine.Where(rule => rule.Kind == ExclusionRuleKind.SourceApp).ToList();
        if (apps.Count == 0)
        {
            var none = new TextBlock { Text = "还没有自己的排除。" };
            none.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
            none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            _mineHost.Children.Add(none);
            return;
        }

        foreach (var rule in apps)
        {
            var captured = rule;
            var chip = Chip(captured.Value, removable: true);
            if (chip.Child is StackPanel row)
            {
                var remove = new Button
                {
                    Content = "\uE711",
                    Padding = new Thickness(2),
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "移除这条排除",
                };
                remove.SetResourceReference(FrameworkElement.StyleProperty, "FlyoutButton");
                remove.SetResourceReference(TextElement.FontFamilyProperty, "Font.Icon");
                remove.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
                remove.Click += (_, _) =>
                {
                    _mine.Remove(captured);
                    Save();
                };
                row.Children.Add(remove);
            }

            _mineHost.Children.Add(chip);
        }
    }

    // --- layer 3: re: patterns, folded away ------------------------------------

    private void BuildAdvanced()
    {
        var host = new StackPanel();

        var toggle = new ToggleButton
        {
            Content = "高级规则（re: 内容正则）",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 12, 0, 0),
            Cursor = Cursors.Hand,
        };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "SegmentChip");

        var panel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };

        var patterns = string.Join(
            Environment.NewLine,
            _mine.Where(rule => rule.Kind == ExclusionRuleKind.ContentPattern).Select(rule => rule.Value));
        _patterns.Text = patterns;
        _patterns.AcceptsReturn = true;
        _patterns.TextWrapping = TextWrapping.Wrap;
        _patterns.Height = 96;
        _patterns.VerticalContentAlignment = VerticalAlignment.Top;
        _patterns.SetResourceReference(TextBox.BackgroundProperty, "Brush.SurfaceInput");

        var note = new TextBlock
        {
            Text = "一行一条正则，匹配复制的内容文本（大小写不敏感）。无法求值的规则不会生效。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        note.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        panel.Children.Add(_patterns);
        panel.Children.Add(note);

        void SavePatterns()
        {
            var lines = _patterns.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0)
                .Select(line => line.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? line : "re:" + line)
                .ToList();

            _mine.RemoveAll(rule => rule.Kind == ExclusionRuleKind.ContentPattern);
            _mine.AddRange(lines.Select(value => new StoredExclusionRule(ExclusionRuleKind.ContentPattern, value["re:".Length..])));
            Save();
        }

        _patterns.LostFocus += (_, _) => SavePatterns();
        _patterns.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SavePatterns();
                e.Handled = true;
            }
        };

        toggle.Checked += (_, _) => panel.Visibility = Visibility.Visible;
        toggle.Unchecked += (_, _) =>
        {
            SavePatterns();
            panel.Visibility = Visibility.Collapsed;
        };

        host.Children.Add(toggle);
        host.Children.Add(panel);
        _root.Children.Add(host);
    }

    private void Save()
    {
        RefreshMine();
        _commit(string.Join(
            Environment.NewLine,
            _mine.Select(rule => rule.Kind == ExclusionRuleKind.ContentPattern ? "re:" + rule.Value : rule.Value)));
    }

    private static Border Chip(string text, bool removable)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = text });

        var chip = new Border
        {
            Child = row,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 6),
        };
        chip.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        chip.SetResourceReference(Border.BackgroundProperty, removable ? "Brush.AccentSubtle" : "Brush.SurfaceSubtle");
        chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        chip.BorderThickness = new Thickness(1);
        return chip;
    }
}
