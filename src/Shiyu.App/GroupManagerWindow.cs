using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// The groups manager: create, rename, re-icon, reorder, hide, delete.
///
/// Deleting says here what it does — orphans nothing, because the promise
/// "条目回到未分组" is the one thing a user needs to know before pressing it.
/// Built in code like the note editor: one small owned window, nothing to
/// style beyond the theme brushes.
/// </summary>
internal sealed class GroupManagerWindow : Window
{
    private readonly EntryStore _store;
    private readonly StackPanel _list = new();

    /// <summary>Raised when the user asks for the related settings — a deep link.</summary>
    public event Action? DataSettingsRequested;

    public GroupManagerWindow(EntryStore store)
    {
        _store = store;

        Title = "管理分组";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        MinWidth = 380;
        Background = (Brush)Application.Current.FindResource("Brush.Background");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui");
        FontSize = (double)Application.Current.FindResource("Type.Body");

        // R1（票 18）：隐式 TextBlock 样式已删，文字默认值由窗口根继承下去。
        SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");

        var root = new StackPanel { Margin = new Thickness(12) };

        // The one-sentence mind model (票 39): the head manages the piles,
        // the card manages where the card lives.
        var mind = new TextBlock
        {
            Text = "头部管分组本身，卡片管卡片的归属。",
            Margin = new Thickness(0, 0, 0, 6),
        };
        mind.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        mind.SetResourceReference(TextBlock.FontWeightProperty, "Weight.Emphasis");
        root.Children.Add(mind);

        var intro = new TextBlock
        {
            Text = "分组是抽屉：切到哪个就只看哪摊。标签是横标签，用来叠加筛选——两者在这里各管各的。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        root.Children.Add(intro);

        var scroll = new ScrollViewer
        {
            Content = _list,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Margin = new Thickness(0, 0, 0, 10),
        };
        root.Children.Add(scroll);

        var createRow = new StackPanel { Orientation = Orientation.Horizontal };
        var iconBox = new TextBox { Width = 36, Padding = new Thickness(4), MaxLength = 2, ToolTip = "图标：一两个字" };
        iconBox.SetResourceReference(BackgroundProperty, "Brush.SurfaceInput");
        var nameBox = new TextBox { Padding = new Thickness(4), MinWidth = 160, ToolTip = "新分组名称" };
        nameBox.SetResourceReference(BackgroundProperty, "Brush.SurfaceInput");
        var create = new Button { Content = "新建", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand };
        create.Click += (_, _) =>
        {
            var label = nameBox.Text.Trim();
            if (label.Length == 0)
            {
                return;
            }

            _store.CreateGroup(label, string.IsNullOrWhiteSpace(iconBox.Text) ? null : iconBox.Text.Trim());
            nameBox.Clear();
            iconBox.Clear();
            Reload();
        };
        createRow.Children.Add(iconBox);
        createRow.Children.Add(nameBox);
        createRow.Children.Add(create);
        root.Children.Add(createRow);

        var hint = new TextBlock
        {
            Text = "删除分组不会删除其中的条目——它们只是回到未分组。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        root.Children.Add(hint);

        var link = new Button
        {
            Content = "删除保护与备份在设置中调整…",
            Padding = new Thickness(0, 3, 0, 3),
            Margin = new Thickness(0, 8, 0, 0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        link.Click += (_, _) => DataSettingsRequested?.Invoke();
        root.Children.Add(link);

        Content = root;
        Reload();
    }

    private void Reload()
    {
        _list.Children.Clear();
        var groups = _store.Groups();

        if (groups.Count == 0)
        {
            var empty = new TextBlock { Text = "还没有分组。复制后在卡片上点「组」即可归组。", Margin = new Thickness(0, 4, 0, 4) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            _list.Children.Add(empty);
            return;
        }

        foreach (var group in groups)
        {
            _list.Children.Add(RowFor(group));
        }
    }

    private Border RowFor(EntryGroup group)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        var icon = new TextBox { Text = group.Icon ?? "", Width = 32, Padding = new Thickness(3), MaxLength = 2, VerticalAlignment = VerticalAlignment.Center, ToolTip = "图标" };
        icon.SetResourceReference(BackgroundProperty, "Brush.SurfaceInput");
        icon.LostFocus += (_, _) =>
        {
            var value = icon.Text.Trim();
            _store.SetGroupIcon(group.Id, value.Length == 0 ? null : value);
        };

        var name = new TextBox { Text = group.Name, Padding = new Thickness(3), MinWidth = 110, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        name.SetResourceReference(BackgroundProperty, "Brush.SurfaceInput");
        name.LostFocus += (_, _) =>
        {
            var value = name.Text.Trim();
            if (value.Length > 0)
            {
                _store.RenameGroup(group.Id, value);
            }
        };

        var up = SmallButton("↑", "上移");
        up.Click += (_, _) => { _store.MoveGroup(group.Id, -1); Reload(); };

        var down = SmallButton("↓", "下移");
        down.Click += (_, _) => { _store.MoveGroup(group.Id, +1); Reload(); };

        var hide = new CheckBox
        {
            Content = "藏",
            IsChecked = group.Hidden,
            ToolTip = "从切换行隐藏；条目仍在，也仍可归入",
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        hide.Checked += (_, _) => _store.SetGroupHidden(group.Id, true);
        hide.Unchecked += (_, _) => _store.SetGroupHidden(group.Id, false);

        var delete = SmallButton("删", "删除分组（条目不删，回到未分组）", danger: true);
        delete.Click += (_, _) =>
        {
            _store.DeleteGroup(group.Id);
            Reload();
        };

        row.Children.Add(icon);
        row.Children.Add(name);
        row.Children.Add(hide);
        row.Children.Add(up);
        row.Children.Add(down);
        row.Children.Add(delete);

        var border = new Border { Padding = new Thickness(4, 2, 4, 2), Child = row };
        border.SetResourceReference(BackgroundProperty, "Brush.Surface");
        return border;
    }

    private static Button SmallButton(string label, string tip, bool danger = false)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = tip,
            Cursor = Cursors.Hand,
        };
        if (danger)
        {
            button.SetResourceReference(ForegroundProperty, "Brush.Danger");
        }
        return button;
    }
}
