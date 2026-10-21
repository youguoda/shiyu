using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 设置窗里的键位速查（§5.2 键位即数据）：整张卡从 <see cref="KeyMap"/> 渲染——这里不出现任何手写按键，
/// 改 Core 那张表，这卡、窄条键帽、托盘菜单、引导同步变。托盘菜单的「键位速查…」深链落在这。
/// </summary>
internal static class KeyMapCard
{
    public static FrameworkElement Build()
    {
        var list = new StackPanel();

        foreach (var row in KeyMap.Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (row.Glyph is { Length: > 0 })
            {
                var icon = new TextBlock
                {
                    Text = SettingsCardView.GlyphOf(row.Glyph),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                icon.SetResourceReference(TextElement.FontFamilyProperty, "Font.Icon");
                icon.SetResourceReference(TextElement.FontSizeProperty, "Size.IconS");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);
            }

            var name = new TextBlock
            {
                Text = row.Name,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            AppendSurfaceKeys(keys, "窄条", row.Bar);
            AppendSurfaceKeys(keys, "设置", row.Settings);
            Grid.SetColumn(keys, 2);
            grid.Children.Add(keys);

            if (row.Condition is { Length: > 0 })
            {
                var condition = new TextBlock
                {
                    Text = row.Condition,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                };
                condition.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
                condition.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                Grid.SetColumn(condition, 3);
                grid.Children.Add(condition);
            }

            list.Children.Add(grid);
        }

        return list;
    }

    /// <summary>一行速查的按键段：窗口名（窄条/设置）+ 该窗的键帽序列。</summary>
    private static void AppendSurfaceKeys(StackPanel host, string surface, string? keys)
    {
        if (keys is not { Length: > 0 })
        {
            return;
        }

        var label = new TextBlock
        {
            Text = surface,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        label.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        host.Children.Add(label);

        foreach (var chip in KeyMap.Chips(keys))
        {
            var cap = new ContentControl { Content = chip, VerticalAlignment = VerticalAlignment.Center };
            cap.SetResourceReference(FrameworkElement.StyleProperty, "KeyCap");
            cap.Margin = new Thickness(0, 0, 2, 0);
            host.Children.Add(cap);
        }

        // 与下一个窗口段留出呼吸。
        host.Children.Add(new Border { Width = 8 });
    }
}
