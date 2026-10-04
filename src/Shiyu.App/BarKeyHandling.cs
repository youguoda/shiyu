using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shiyu.Core;

namespace Shiyu.App;

internal partial class BarWindow
{
    // --- keyboard model --------------------------------------------------------

    /// <summary>Rows as displayed: pinned first, then the rest. Number keys and navigation count these.</summary>
    private IEnumerable<BarCard> VisibleRows => _pinned.Concat(_cards);

    /// <summary>Focus sits in a text field: letters belong to it, arrows move its caret.</summary>
    private static bool IsTyping
        => Keyboard.FocusedElement is TextBox;

    /// <summary>
    /// There is always an active row while any row exists, so Enter and the
    /// letter actions always have something definite to act on.
    /// </summary>
    private void EnsureActiveItem()
    {
        if (_selected is { } card && (_cards.Contains(card) || _pinned.Contains(card)))
        {
            return;
        }

        Select(VisibleRows.FirstOrDefault());
    }

    private void Move(int delta)
    {
        var rows = VisibleRows.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var current = _selected is { } card ? rows.IndexOf(card) : -1;
        var next = Math.Clamp(current + delta, 0, rows.Count - 1);

        Select(rows[next]);
        Cards.ScrollIntoView(rows[next]);

        // Noted, not followed: the panel waits for the selection to settle so
        // it glides to a still target instead of chasing a scrolling one.
        RunPreviewCommand(_previewPolicy.SelectionMoved(rows[next].Id));
    }

    // --- key hints (ticket 14) -------------------------------------------------

    private bool _keyHintsOn;

    /// <summary>
    /// Hold Ctrl and every actionable icon swaps in place for the key that
    /// drives it — the whole keyboard model taught at the place it applies,
    /// for exactly as long as the user asks.
    /// </summary>
    private void SetKeyHints(bool on)
    {
        if (_keyHintsOn == on)
        {
            return;
        }

        _keyHintsOn = on;
        SearchKeyBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateKindCaps();
        FavoriteKeyBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TagKeyBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // 帽替内容（见 BarWindow.xaml 的同名注释）：标签下拉的复合脚印
        // （图标+名称+箭头）比键帽宽，帽盖上去会露碎片——帽在时收起内容，
        // 按钮收缩成帽本身的大小。
        TagContent.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        foreach (var container in RealizedContainers())
        {
            ApplyKeyHintsTo(
                Tree.FindDescendant<RowKeyBadge>(container),
                Tree.FindDescendant<ActionTray>(container));

            // Links and emails dress as clickable for exactly as long as the
            // modifier that opens them is held. On release the resource
            // reference is restored rather than a local colour set, so theme
            // changes keep reaching these texts.
            if (Tree.FindDescendant<EntryBodyText>(container) is { } body)
            {
                var clickable = on
                    && ((FrameworkElement)container).DataContext is BarCard { IsOpenable: true };

                if (clickable)
                {
                    body.Foreground = (Brush)FindResource("Brush.Accent");
                    body.TextDecorations = System.Windows.TextDecorations.Underline;
                }
                else
                {
                    body.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                    body.TextDecorations = null;
                }
            }
        }
    }

    /// <summary>
    /// One row's hint state — also called for rows realized while Ctrl is
    /// already down, so recycled rows arrive pre-badged rather than blank.
    /// </summary>
    internal void ApplyKeyHintsTo(RowKeyBadge? badge, ActionTray? tray)
    {
        if (badge is not null)
        {
            badge.Visibility = _keyHintsOn ? Visibility.Visible : Visibility.Collapsed;
        }

        tray?.ShowHints(_keyHintsOn);
    }

    /// <summary>
    /// The ←→ cap sits ON the checked chip, replacing its glyph（§6.1 键帽：
    /// 追加到行尾正是它被裁成横线的原因，也推动第 2 行其余控件位移）。
    /// Selection moves（←→ cycling while Ctrl is held）move the cap with it.
    /// </summary>
    private void UpdateKindCaps()
    {
        for (var i = 0; i < _kindChips.Length && i < _kindCaps.Length; i++)
        {
            _kindCaps[i].Visibility =
                _keyHintsOn && _kindChips[i].IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private IEnumerable<BarCardContainer> RealizedContainers()
    {
        foreach (var item in _pinned.Concat(_cards))
        {
            if (PinnedList.ItemContainerGenerator.ContainerFromItem(item) is BarCardContainer pinned)
            {
                yield return pinned;
            }

            if (Cards.ItemContainerGenerator.ContainerFromItem(item) is BarCardContainer rest)
            {
                yield return rest;
            }
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // Releasing Space takes down only what Space opened — a hover preview
        // under the pointer keeps its own rules. The guard is loose on
        // purpose: focus may have wandered between press and release.
        if (e.Key is Key.Space)
        {
            RunPreviewCommand(_previewPolicy.SpaceUp());
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl && _keyHintsOn
            && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            SetKeyHints(false);
        }
    }

    /// <summary>
    /// Losing focus while Ctrl is still down — Ctrl+Tab, a notification
    /// stealing the click — means the release event never arrives. The
    /// badges come in now, or they stay forever.
    ///
    /// 粘贴模式另有含义（票 26）：失焦即隐——点别处就是"用完了"。只隐藏，
    /// 不还原前台：焦点此刻正落在用户点下去的窗口上，Restore 只会把它
    /// 抢回来（UI 报告 §3.7 问题 1）。收尾规格与其余隐藏原因相同（轻量
    /// 开就进轻量），差异只在这一个"不拽回"。
    /// </summary>
    private void OnLostFocus(object sender, EventArgs e)
    {
        if (_pasteMode && IsVisible)
        {
            HideAfterFocusLost();
            return;
        }

        SetKeyHints(false);
    }

    /// <summary>
    /// The paste mode's focus-loss exit: the full hide teardown (preview down,
    /// teaching row down, refresh policy's Hidden — reason FocusLost so the
    /// "every reason pays the same" invariant stays testable), minus the
    /// foreground restore the deliberate exits do.
    /// </summary>
    private void HideAfterFocusLost()
    {
        // Dismiss clears the flag itself; doing it here first keeps this path
        // terminal even if a re-entrant Deactivated arrives mid-hide.
        _pasteMode = false;

        RunPreviewCommand(_previewPolicy.BarHidden());
        DismissFirstUseHint();
        Hide();
        RunRefresh(_refreshPolicy.Hidden(BarHideReason.FocusLost));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            SetKeyHints(true);
            return;
        }

        // 中文 IME 打开时，字母键以 Key.ImeProcessed 报达（真实键在
        // ImeProcessedKey 里）——键位模型认"用户按了哪个键"，不认输入法
        // 替他转的这一手。票 24 在管理窗（LibraryKeys）做了同一归一，代码
        // 里留了指给窄条的档；这里是那一笔的兑现：字母动作在中文输入法
        // 下也得活着。
        // 带 Alt 的组合走 WM_SYSKEYDOWN：e.Key 报 Key.System，真实键在
        // SystemKey——不归一，Alt+S（只看收藏）这类 Alt 动作永远匹配不上
        // （用户实录 2026-10-05"按 Alt+S 没反应"）。
        var key = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key,
        };

        switch (key)
        {
            case Key.Escape:
                e.Handled = true;

                // The menu peels off first: closing it must not cost the user
                // their place — the bar stays, the selection stays.
                if (_cardMenu is { IsOpen: true } || _cardSubMenu is { IsOpen: true })
                {
                    CloseCardMenu();
                    return;
                }

                // The tray's ⋯ popup is a layer the same way.
                if (_trayMorePopup is { IsOpen: true })
                {
                    _trayMorePopup.IsOpen = false;
                    return;
                }

                // The header's group menu is a layer the same way (票 39): it
                // goes before any filter the stack would clear underneath it.
                if (_groupMenu is { IsOpen: true })
                {
                    _groupMenu.IsOpen = false;
                    return;
                }

                StepEscape();
                break;

            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;

            // Tab trades focus navigation for filter cycling: Ctrl+F is the
            // way back to the search box, so nothing is lost.
            case Key.Tab:
                e.Handled = true;
                CycleTag(+1);
                break;

            // ←→ cycle kinds. The old !IsTyping guard never fired where it
            // mattered: summon aims the search box, so the box holds focus
            // with an EMPTY query and the arrows did nothing at all (user
            // report 2026-10-05). Same shape as Space below: an empty query
            // hands the key to the window, mid-query it stays with the caret.
            case Key.Left when SearchBox.Text.Length == 0 || !IsTyping:
                e.Handled = true;
                CycleKind(-1);
                break;

            case Key.Right when SearchBox.Text.Length == 0 || !IsTyping:
                e.Handled = true;
                CycleKind(+1);
                break;

            // ↑↓ walk the list even while the search box holds focus (it
            // does by design, right after summon) and mid-query: a
            // single-line box has no caret rows of its own. The old guard
            // existed only to keep the tag ComboBox's own arrows — that
            // control is gone (U-04), so the guard went with it.
            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;

            case Key.Down:
                e.Handled = true;
                Move(+1);
                break;

            // Held Space previews the active card in full (ticket 17). The
            // search box is focused right after summoning, so the rule has to
            // tell an empty box from a query being typed: with nothing typed,
            // Space is free to mean "show me"; mid-query it stays a space.
            case Key.Space when Keyboard.Modifiers == ModifierKeys.None
                && (SearchBox.Text.Length == 0 || !IsTyping):
                e.Handled = true;
                if (_selected is { } card)
                {
                    RunPreviewCommand(_previewPolicy.SpaceDown(card.Id));
                }
                break;

            case Key.Enter:
                e.Handled = true;
                if (_selected is { } enter)
                {
                    // Modifier+Enter pastes as plain text; for stored text the
                    // two coincide until formatted entries exist (ticket 07).
                    PasteEntry(enter);
                }
                break;

            case Key.D1 or Key.NumPad1: NumberedRow(1, e); break;
            case Key.D2 or Key.NumPad2: NumberedRow(2, e); break;
            case Key.D3 or Key.NumPad3: NumberedRow(3, e); break;
            case Key.D4 or Key.NumPad4: NumberedRow(4, e); break;
            case Key.D5 or Key.NumPad5: NumberedRow(5, e); break;
            case Key.D6 or Key.NumPad6: NumberedRow(6, e); break;
            case Key.D7 or Key.NumPad7: NumberedRow(7, e); break;
            case Key.D8 or Key.NumPad8: NumberedRow(8, e); break;
            case Key.D9 or Key.NumPad9: NumberedRow(9, e); break;
            case Key.D0 or Key.NumPad0: NumberedRow(10, e); break;

            // Letter actions only outside text fields — inside one, letters
            // are the search the user is typing.
            case Key.C when !IsTyping && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                if (_selected is { } copy) ExecuteAction("copy", copy, feedback: null);
                break;

            case Key.O when !IsTyping && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                if (_selected is { } open) ExecuteAction("open", open, feedback: null);
                break;

            case Key.P when !IsTyping && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                if (_selected is { } pin) ExecuteAction("pin", pin, feedback: null);
                break;

            // Alt+S 只看收藏（ADR-0012 #10）：与托盘"收藏当前卡"的 S 分工，
            // 修饰键令它对搜索框里的打字无害。键帽为 FavoriteKeyBadge。
            case Key.S when Keyboard.Modifiers == ModifierKeys.Alt:
                e.Handled = true;
                FavoriteOnly.IsChecked = FavoriteOnly.IsChecked != true;
                break;

            case Key.S when !IsTyping && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                if (_selected is { } favourite) ExecuteAction("favorite", favourite, feedback: null);
                break;

            case Key.N when !IsTyping && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                if (_selected is { } note) ExecuteAction("note", note, feedback: null);
                break;

            case Key.G when !IsTyping && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                if (_selected is { } group) ExecuteAction("group", group, feedback: null);
                break;

            case Key.D when !IsTyping && Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                if (_selected is { } del) ExecuteAction("delete", del, feedback: null);
                break;

            // The undo window is narrow on purpose: it answers only while the
            // footer is still showing the deletion it would reverse.
            case Key.Z when !IsTyping && Keyboard.Modifiers == ModifierKeys.None && _undoPending is not null:
                e.Handled = true;
                RestoreUndo();
                break;
        }
    }

    private void NumberedRow(int oneBased, KeyEventArgs e)
    {
        var rows = VisibleRows.ToList();

        if (oneBased <= rows.Count)
        {
            e.Handled = true;
            PasteEntry(rows[oneBased - 1]);
        }
    }

    /// <summary>Escape peels the most recent layer; only an empty stack hides the window.</summary>
    private void StepEscape()
    {
        var hasQuery = SearchBox.Text.Length > 0;
        var hasSubtype = _subtypeIndex != 0;
        var hasTag = _selectedTag != AnyTag;
        var hasKind = _kindIndex != 0;
        var hasFavorite = FavoriteOnly.IsChecked == true;
        var hasGroup = _selectedGroup is not null;

        switch (BarKeyboard.NextEscape(
            previewOpen: _preview is { IsVisible: true },
            hasQuery,
            hasSubtype,
            hasTag,
            hasKind,
            hasFavorite,
            hasGroup))
        {
            case BarKeyboard.EscapeAction.ClosePreview:
                // The panel's level in the stack, ready since the keyboard
                // model was written; this is its wiring.
                RunPreviewCommand(_previewPolicy.Escape());
                break;

            case BarKeyboard.EscapeAction.ClearQuery:
                SearchBox.Clear();
                break;

            case BarKeyboard.EscapeAction.ClearSubtypeFilter:
                SetSubtype(0);
                ApplyFilter();
                break;

            case BarKeyboard.EscapeAction.ClearTagFilter:
                SetTag(AnyTag);
                ApplyFilter();
                break;

            case BarKeyboard.EscapeAction.ClearTypeFilter:
                SetKindIndex(0);
                ApplyFilter();
                break;

            case BarKeyboard.EscapeAction.ClearFavoriteFilter:
                FavoriteOnly.IsChecked = false;
                break;

            case BarKeyboard.EscapeAction.ClearGroupFilter:
                SelectGroup(null);
                break;

            case BarKeyboard.EscapeAction.HideWindow:
                Dismiss();
                break;
        }
    }

    private void CycleKind(int delta)
    {
        SetKindIndex(BarKeyboard.Cycle(_kindIndex, delta, _kindChips.Length));
        ApplyFilter();
    }

    private void CycleTag(int delta)
    {
        var index = _tagChoices.IndexOf(_selectedTag);
        SetTag(_tagChoices[BarKeyboard.Cycle(index < 0 ? 0 : index, delta, _tagChoices.Count)]);
        ApplyFilter();
    }
}
