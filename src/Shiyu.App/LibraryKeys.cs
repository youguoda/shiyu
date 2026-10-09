using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 管理窗的键盘模型（票 24 / UI 报告 §5.2）：与窄条一套动词、跨窗同键，只有
/// 两处有意不同——常规窗没有粘贴目标，Enter 是"复制"；Tab 留给焦点导航
/// （可访问性底线），标签改用 T。Esc 逐层的决策住在 Core 的
/// <see cref="LibraryKeyboard"/>；本文件接线到窗口，并管键帽态（按住 Ctrl
/// 命令栏原位换键帽）与键位速查。
/// </summary>
public partial class LibraryWindow
{
    /// <summary>锚点（非扩展选择的落点）与游标（Shift 扩选的移动端）。</summary>
    private int _anchor = -1;

    private int _lead = -1;

    /// <summary>程序化选择期间不让 OnSelectionChanged 重置锚点。</summary>
    private bool _movingSelection;

    private bool _keyHintsOn;

    /// <summary>Focus sits in a text field: letters belong to it, arrows move its caret.</summary>
    private static bool IsTyping => Keyboard.FocusedElement is TextBox;

    // --- 键位速查表（§5.2）-----------------------------------------------------------
    //
    // 全部行从 Core 的 KeyMap 渲染（键位即数据，票 25 收编）：管理窗速查 =
    // ForSurface(Library)，键 = EffectiveKey。改键只改 KeyMap 一处，窄条/
    // 设置/托盘/引导的速查同步变——这里不再有自己的静态表。

    private void ShowCheatSheet()
    {
        var sheet = new Window
        {
            Title = "键位速查 · 管理窗",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        sheet.SetResourceReference(Window.BackgroundProperty, "Brush.Background");

        var list = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };

        var head = new TextBlock { Text = "与窄条同一套动词", Margin = new Thickness(0, 0, 0, 10) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        list.Children.Add(head);

        foreach (var row in KeyMap.ForSurface(KeySurface.Library))
        {
            var rowPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = false };

            var label = new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);
            rowPanel.Children.Add(label);

            var cap = new TextBlock
            {
                Text = KeyMap.EffectiveKey(row, KeySurface.Library),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 2, 8, 2),
            };
            cap.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
            cap.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            cap.SetResourceReference(TextBlock.BackgroundProperty, "Brush.SurfaceSubtle");
            DockPanel.SetDock(cap, Dock.Right);
            rowPanel.Children.Add(cap);

            list.Children.Add(rowPanel);
        }

        var footnote = new TextBlock
        {
            Text = "管理窗没有粘贴目标，Enter 是复制；Esc 剥到最后一层为止，不关窗。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        footnote.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
        footnote.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        list.Children.Add(footnote);

        sheet.Content = list;
        sheet.ShowDialog();
    }

    // --- 键帽态（§5.2：按住 Ctrl，命令栏原位换键帽）----------------------------------

    private (Button Button, string Key, object? Content, bool Saved)[]? _hintButtons;

    private static ContentControl MakeKeyCap(string key)
    {
        var cap = new ContentControl { Content = key };
        cap.SetResourceReference(StyleProperty, "KeyCap");
        return cap;
    }

    /// <summary>
    /// 按住 Ctrl：每个有快捷键的命令栏按钮原位把内容换成键帽——换内容不动
    /// 布局（按钮尺寸固定），松手还原，零位移教学。键从 KeyMap 取（回落
    /// 口径与速查/自动化名同一处），改键只改 Core 那张表。
    /// </summary>
    private void SetKeyHints(bool on)
    {
        if (_keyHintsOn == on)
        {
            return;
        }

        _keyHintsOn = on;

        // (按钮, 动作 Id)：键位运行时从 KeyMap 解析；无键的动作（理论上
        // 不出现在命令栏）直接跳过，不硬编码字母。
        (Button, string)[] slots =
        [
            (CopyButton, "copy"),
            (PinButton, "pin"),
            (FavoriteButton, "favorite"),
            (TagButton, "tag"),
            (DeleteButton, "delete"),
        ];

        _hintButtons ??= slots
            .Select(slot => (slot.Item1, Key: KeyMap.HintKey(slot.Item2, KeySurface.Library) ?? string.Empty,
                Content: (object?)null, Saved: false))
            .Where(slot => slot.Key.Length > 0)
            .ToArray();

        for (var i = 0; i < _hintButtons.Length; i++)
        {
            var slot = _hintButtons[i];
            if (on && !slot.Saved)
            {
                slot.Content = slot.Button.Content;
                slot.Saved = true;
                _hintButtons[i] = slot;
            }

            slot.Button.Content = on ? MakeKeyCap(slot.Key) : slot.Content;
        }
    }

    // --- 主键处理 ----------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            SetKeyHints(true);
            return;
        }

        // 中文 IME 打开时，字母键以 Key.ImeProcessed 报达（真实键在
        // ImeProcessedKey 里）——键位模型认"用户按了哪个键"，不认输入法
        // 替他转的这一手。母语用户是这个窗的主力，字母动作不能只在英文
        // 输入法下活着。（窄条同形问题记给票 25 的 KeyMap 收编。）
        // 带 Alt 的组合同形归一：e.Key 报 Key.System，真实键在 SystemKey。
        var key = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key,
        };
        var none = Keyboard.Modifiers == ModifierKeys.None;

        // Ctrl+Tab 换页（KeyMap "switch-page"）：页签上的 Enter / Space 在历史页先被下面接走
        // （复制、预览），键盘用户靠它在两本账之间来回。
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SwitchPage(log: !_logPageActive);
            return;
        }

        // 翻译记录页有自己的一小套键（LibraryTranslationLog）：历史页的键在那里没有对象，
        // 让它们越界就是对着看不见的列表置顶、删除。
        if (_logPageActive)
        {
            OnLogPageKeyDown(e, key);
            return;
        }

        switch (key)
        {
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;

            case Key.W when Keyboard.Modifiers == ModifierKeys.Control:
                // 关窗是明确动作（Esc 到最后一层为止，§5.2）。
                e.Handled = true;
                CommitUndoExpiry();
                Close();
                break;

            case Key.A when Keyboard.Modifiers == ModifierKeys.Control && !IsTyping:
                e.Handled = true;
                EntryList.SelectAll();
                break;

            case Key.F6:
                e.Handled = true;
                CycleFocus();
                break;

            case Key.F1:
                e.Handled = true;
                ShowCheatSheet();
                break;

            case Key.OemQuestion when !IsTyping:
                e.Handled = true;
                ShowCheatSheet();
                break;

            case Key.Escape:
                e.Handled = true;
                StepEscape();
                break;

            // ↑↓ 走列表，即便焦点还在搜索框（呼出后它就在那）——单词行没有
            // 自己的行间导航可让。
            case Key.Up when Keyboard.Modifiers is not ModifierKeys.Alt:
                e.Handled = true;
                MoveSelection(-1, Keyboard.Modifiers == ModifierKeys.Shift);
                break;

            case Key.Down when Keyboard.Modifiers is not ModifierKeys.Alt:
                e.Handled = true;
                MoveSelection(+1, Keyboard.Modifiers == ModifierKeys.Shift);
                break;

            case Key.Enter:
                if (Keyboard.FocusedElement == SearchBox)
                {
                    // 搜索中回车：落进列表，下一发 Enter 才是复制。
                    e.Handled = true;
                    EntryList.Focus();
                }
                else if (!IsTyping)
                {
                    e.Handled = true;
                    CopySelection();
                }

                break;

            // 按住 Space 预览（§5.2 与窄条同键；松开即关）。搜索框里有词时
            // Space 还是空格。
            case Key.Space when none && (!IsTyping || SearchBox.Text.Length == 0):
                e.Handled = true;
                OpenPreview();
                break;

            case Key.D or Key.Delete when !IsTyping && none:
                e.Handled = true;
                DeleteSelection();
                break;

            case Key.Z when !IsTyping
                && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control:
                e.Handled = true;
                UndoDelete();
                break;

            case Key.C when !IsTyping && none:
                e.Handled = true;
                CopySelection();
                break;

            case Key.P when !IsTyping && none:
                e.Handled = true;
                OnTogglePin(this, new RoutedEventArgs());
                break;

            case Key.S when !IsTyping && none:
                e.Handled = true;
                OnToggleFavorite(this, new RoutedEventArgs());
                break;

            case Key.N when !IsTyping && none:
                e.Handled = true;
                EditNoteSelection();
                break;

            case Key.G when !IsTyping && none:
                e.Handled = true;
                OpenGroupMenu();
                break;

            case Key.T when !IsTyping && none:
                e.Handled = true;
                OpenTagMenu();
                break;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // 松开 Space 只收 Space 打开的东西；Esc 也能关，同层。
        if (e.Key is Key.Space)
        {
            ClosePreview();
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl && _keyHintsOn
            && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            SetKeyHints(false);
        }
    }

    /// <summary>Esc 逐层（决策在 Core 的 LibraryKeyboard）：预览 → 搜索词 → 筛选 token 逐个弹；最后一层不关窗。</summary>
    private void StepEscape()
    {
        // 弹层各自收自己的 Esc（MakePopup 壳）；窗口只见"没有弹层开着"的键。
        if (_filterPopup is { IsOpen: true } || _tagPopup is { IsOpen: true }
            || _groupPopup is { IsOpen: true } || _morePopup is { IsOpen: true }
            || _aiPopup is { IsOpen: true })
        {
            return;
        }

        switch (LibraryKeyboard.NextEscape(
            previewOpen: _previewOpen,
            hasQuery: SearchBox.Text.Length > 0,
            filterCount: _tokenOrder.Count))
        {
            case LibraryEscapeAction.ClosePreview:
                ClosePreview();
                break;

            case LibraryEscapeAction.ClearQuery:
                SearchBox.Clear();
                break;

            case LibraryEscapeAction.PopNewestFilter:
                PopNewestToken();
                break;

            case LibraryEscapeAction.Stay:
                // 最后一层：到此为止，窗不关（关窗是 Ctrl+W 的明确动作）。
                break;
        }
    }

    /// <summary>F6 在 头部搜索 → 列表 → 详情 之间循环（§5.2）。</summary>
    private void CycleFocus()
    {
        if (Keyboard.FocusedElement == SearchBox)
        {
            EntryList.Focus();
        }
        else if (DetailPane.Visibility == Visibility.Visible
            && Keyboard.FocusedElement != DetailPane)
        {
            DetailPane.Focus();
        }
        else
        {
            SearchBox.Focus();
        }
    }

    /// <summary>
    /// ↑↓ 的程序化选择：非扩展 = 单选移动（锚点随行）；Shift = 锚点不动、
    /// 游标走，选中 = 锚点..游标。比 ListBox 原生导航多做的事：焦点不在
    /// 列表里（搜索框）时也走同一套。
    /// </summary>
    private void MoveSelection(int delta, bool extend)
    {
        if (_items.Count == 0)
        {
            return;
        }

        if (_anchor < 0 || _lead < 0)
        {
            _anchor = _lead = Math.Max(EntryList.SelectedIndex, 0);
        }

        _movingSelection = true;
        try
        {
            if (!extend)
            {
                _anchor = _lead = Math.Clamp(_lead + delta, 0, _items.Count - 1);
                EntryList.SelectedItems.Clear();
                EntryList.SelectedItems.Add(_items[_lead]);
            }
            else
            {
                _lead = Math.Clamp(_lead + delta, 0, _items.Count - 1);
                EntryList.SelectedItems.Clear();
                var low = Math.Min(_anchor, _lead);
                var high = Math.Max(_anchor, _lead);
                for (var i = low; i <= high; i++)
                {
                    EntryList.SelectedItems.Add(_items[i]);
                }
            }

            EntryList.ScrollIntoView(_items[_lead]);
        }
        finally
        {
            _movingSelection = false;
        }
    }
}
