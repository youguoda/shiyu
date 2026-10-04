using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

internal partial class BarWindow
{
    // --- card context menu ----------------------------------------------------

    private Popup? _cardMenu;

    private Popup? _cardSubMenu;

    /// <summary>
    /// The right-click menu is a Popup the app renders itself, not a system
    /// ContextMenu: a system menu takes focus, and a bar that never activates
    /// can find itself hidden once the menu closes — the user's action would
    /// die half-done. A Popup never activates anything.
    /// </summary>
    private void OnCardRightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not BarCard card)
        {
            return;
        }

        e.Handled = true;
        Select(card);
        CloseCardMenu();
        OpenCardMenu(card, (FrameworkElement)sender, e.GetPosition((IInputElement)sender));
    }

    private void OpenCardMenu(BarCard card, FrameworkElement anchor, Point at)
    {
        var panel = new StackPanel { MinWidth = 172 };

        foreach (var action in ActionsFor(card))
        {
            // 归组 opens its own submenu here — the tray button opens the
            // chooser popup instead; a menu is where a submenu belongs.
            if (action == "group")
            {
                panel.Children.Add(GroupSubmenuItem(card));
                continue;
            }

            var captured = action;
            panel.Children.Add(MenuRow(
                HoverActions.Name(action),
                ShortcutFor(action),
                HoverActions.IsDestructive(captured),
                () =>
                {
                    CloseCardMenu();
                    ExecuteAction(captured, card, feedback: null);
                }));
        }

        _cardMenu = new Popup
        {
            Child = WithPopupFont(MenuSurface(panel)),
            PlacementTarget = anchor,
            Placement = PlacementMode.RelativePoint,
            PlacementRectangle = new Rect(at.X, at.Y, 0, 0),
            StaysOpen = false,
            AllowsTransparency = true,
        };
        _cardMenu.Opened += (_, _) => FlipIntoWorkArea(_cardMenu);
        _cardMenu.IsOpen = true;
    }

    private void CloseCardMenu()
    {
        if (_cardSubMenu is not null)
        {
            _cardSubMenu.IsOpen = false;
            _cardSubMenu = null;
        }

        if (_cardMenu is not null)
        {
            _cardMenu.IsOpen = false;
            _cardMenu = null;
        }
    }

    /// <summary>
    /// What the menu shows next to an action: the key that runs it. The whole
    /// column renders from KeyMap（键位即数据，§5.2）——贴（Enter）与字母行
    /// 同一张表，改表即改菜单列。
    /// </summary>
    private static string ShortcutFor(string action) => action switch
    {
        "paste" => KeyMap.Find("paste")?.Bar ?? string.Empty,
        _ => BarKeys.TrayKey(action) ?? string.Empty,
    };

    private UIElement MenuRow(string label, string shortcut, bool danger, Action run)
    {
        var name = new TextBlock { Text = label };
        if (danger)
        {
            name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        }

        var key = new TextBlock { Text = shortcut, MinWidth = 26, TextAlignment = TextAlignment.Right };
        key.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        DockPanel.SetDock(key, Dock.Right);

        var content = new DockPanel();
        content.Children.Add(key);
        content.Children.Add(name);

        var row = new Button
        {
            Content = content,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand,
        };
        row.Click += (_, _) => run();
        return row;
    }

    /// <summary>The one second-level menu: filing the card into a group.</summary>
    private UIElement GroupSubmenuItem(BarCard card)
    {
        var label = new TextBlock { Text = "归组" };
        var arrow = new TextBlock { Text = "▸", MinWidth = 26, TextAlignment = TextAlignment.Right };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        DockPanel.SetDock(arrow, Dock.Right);

        var content = new DockPanel();
        content.Children.Add(arrow);
        content.Children.Add(label);

        var row = new Button
        {
            Content = content,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand,
        };

        row.MouseEnter += (_, _) =>
        {
            if (_cardSubMenu is not null)
            {
                _cardSubMenu.IsOpen = false;
            }

            var list = new StackPanel { MinWidth = 140 };
            foreach (var group in _store.Groups())
            {
                var captured = group;
                var item = new Button
                {
                    Content = $"{group.Icon ?? "组"} {group.Name}",
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 0, 1),
                    Cursor = Cursors.Hand,
                };
                item.Click += (_, _) =>
                {
                    CloseCardMenu();
                    FileCardInto(card, captured.Id);
                };
                list.Children.Add(item);
            }

            var ungrouped = new Button
            {
                Content = "未分组",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
            };
            ungrouped.Click += (_, _) =>
            {
                CloseCardMenu();
                FileCardInto(card, null);
            };
            list.Children.Add(ungrouped);

            var manage = new Button { Content = "管理分组…", Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand };
            manage.Click += (_, _) =>
            {
                CloseCardMenu();
                OnManageGroups(this, new RoutedEventArgs());
            };
            list.Children.Add(manage);

            _cardSubMenu = new Popup
            {
                Child = WithPopupFont(MenuSurface(list)),
                PlacementTarget = row,
                Placement = PlacementMode.Right,
                StaysOpen = false,
                AllowsTransparency = true,
            };
            _cardSubMenu.IsOpen = true;
        };

        return row;
    }

    private static Border MenuSurface(StackPanel panel)
    {
        // The WithPopupFont wrapper carries the font: a popup lives in its own
        // HWND with no property inheritance from the bar, and its text would
        // otherwise fall back to the 12px system font.
        var border = new Border
        {
            Child = WithPopupFont(panel),
            Padding = new Thickness(4),
        };
        // 弹层外壳的圆角走 Radius.Overlay（UI 报告 §4.3：菜单/下拉/弹层 8）。
        border.SetResourceReference(Border.CornerRadiusProperty, "Radius.Overlay");
        border.SetResourceReference(BackgroundProperty, "Brush.Surface");
        border.SetResourceReference(BorderBrushProperty, "Brush.Border");
        border.BorderThickness = new Thickness(1);
        return border;
    }

    /// <summary>
    /// A popup lives in its own HWND with no property inheritance from the
    /// bar — its text would fall back to the 12px system font. Neither Popup
    /// nor Border nor StackPanel is a Control, so the font rides on a
    /// ContentControl wrapper, which every child inherits from.
    /// </summary>
    private static ContentControl WithPopupFont(FrameworkElement content)
    {
        return new ContentControl
        {
            Content = content,
            FontFamily = PopupFont,
            FontSize = PopupText,
        };
    }

    /// <summary>The popup font stack (tokens), shared by every self-built menu.</summary>
    private static FontFamily PopupFont
        => (FontFamily)Application.Current.FindResource("Font.Ui");

    private static double PopupText
        => (double)Application.Current.FindResource("Type.Body");

    /// <summary>
    /// A menu that would hang off the screen edge is nudged back in — measured
    /// in physical pixels against the work area of the monitor it is on, and
    /// shifted in device-independent units.
    /// </summary>
    private static void FlipIntoWorkArea(Popup popup)
    {
        if (popup.Child is not FrameworkElement content
            || PresentationSource.FromVisual(content) is not { } source
            || source.CompositionTarget is not { } transform)
        {
            return;
        }

        var scale = transform.TransformToDevice;
        var topLeft = content.PointToScreen(new Point(0, 0));
        var width = content.ActualWidth * scale.M11;
        var height = content.ActualHeight * scale.M22;
        var work = ScreenGeometry.WorkAreaAt(new ScreenPoint((int)topLeft.X, (int)topLeft.Y));

        if (topLeft.X + width > work.Right)
        {
            popup.HorizontalOffset -= (topLeft.X + width - work.Right) / scale.M11;
        }

        if (topLeft.Y + height > work.Bottom)
        {
            popup.VerticalOffset -= (topLeft.Y + height - work.Bottom) / scale.M22;
        }
    }

    // --- interaction ---------------------------------------------------------

    private void OnCardPressed(object sender, MouseButtonEventArgs e)
    {
        // Handled, so the press does not turn into a window drag.
        e.Handled = true;

        // The press origin arms the drag: only movement beyond the system's
        // minimum distance is a carry, everything shorter stays a click.
        _dragOrigin = e.GetPosition(null);

        if (((FrameworkElement)sender).DataContext is not BarCard card)
        {
            return;
        }

        // Ctrl-click on a link or email opens it — the same modifier whose
        // hints dress it as clickable, so the affordance and the act agree.
        if (card.IsOpenable && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OpenUri(card);
            return;
        }

        Select(card);

        // Border is not a Control and has no double-click of its own; the
        // count on the press is the same information.
        if (e.ClickCount == 2)
        {
            // 双击 = 粘贴（ADR-0012 #9）：窄条只有这一个"挑来用"的动词
            // （票 26 合并后快速粘贴同扇窗、同动词）；复制交给托盘的
            // C 钮与 C 键。
            PasteEntry(card);
        }
    }

    /// <summary>
    /// Any card dragged with the left button carries what it is out to
    /// wherever the user drops it — text (with formatting and a plain
    /// fallback), images as pictures, files as files. Dead paths stay behind:
    /// a drag that silently produces nothing is worse than one that visibly
    /// carries less, and an all-dead card says so through a toast.
    /// </summary>

    // Where the current button press began, so a jitter during a click never
    // turns into a drag: the system's own minimum drag distance decides.
    private Point? _dragOrigin;

    /// <summary>
    /// The thumbnail for a card whose background decode has not landed —
    /// fetched here, once, for a drag that would otherwise carry nothing
    /// (O-36's one synchronous exception: the user is holding the mouse
    /// button down, asking for this specific entry).
    /// </summary>
    private System.Windows.Media.Imaging.BitmapSource? DecodeThumbnailNow(long id)
    {
        var payload = _store.BlobsOf([id]).GetValueOrDefault(id);
        var decoded = AppIconCache.Decode(payload?.ThumbnailPng, 320);

        if (decoded is not null)
        {
            // What the drag carries, later cards show too.
            var card = _cards.FirstOrDefault(c => c.Id == id)
                ?? _pinned.FirstOrDefault(c => c.Id == id);

            if (card is not null)
            {
                card.Thumbnail = decoded;
            }
        }

        // AppIconCache.Decode builds BitmapImages; the narrower return type
        // keeps drag-out callers honest without a cast at every use.
        return decoded as System.Windows.Media.Imaging.BitmapSource;
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragOrigin = null;
            return;
        }

        if (_dragOrigin is not { } origin)
        {
            return;
        }

        var here = e.GetPosition(null);
        if (Math.Abs(here.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(here.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // One drag attempt per press: repeated moves must not re-enter the
        // modal loop the moment it closes.
        _dragOrigin = null;

        if (((FrameworkElement)sender).DataContext is not BarCard card)
        {
            return;
        }

        // A drag begins on movement with the button held — the user's intent
        // to carry, not to click. Everything the entry is rides along: text
        // with its formatting and a plain fallback, images as pictures,
        // files as files.
        var data = new DataObject();
        switch (card.Kind)
        {
            case EntryKind.Text:
                data.SetText(card.Text, TextDataFormat.UnicodeText);

                // Rich destinations get the RTF form; plain ones quietly use
                // the text above — one payload, both worlds. (HTML is left
                // out: WPF writes it header-less and targets mangle it.) The
                // formats are fetched here, at the moment of the drag: listed
                // cards are narrow by design (O-22).
                if (_store.Get(card.Id) is { Rtf: { Length: > 0 } rtf })
                {
                    data.SetText(rtf, TextDataFormat.Rtf);
                }

                break;

            case EntryKind.Image:
                // 拖出优先给原图（保留期内的存档文件）；原图被清理了才退到
                // 缩略图——320px 的缩略图拖进编辑器是半张图。附带文件格式
                // 让资源管理器/文件夹也能接（位图格式它们不收）。
                // 不再附带文本：图片条目的 Text 是「图片 W×H」这类标签串，
                // 落进搜索框就是一行废话（用户实录 2026-10-05）。
                BitmapSource? picture = null;
                string? originalFile = null;
                if (_store.Get(card.Id) is { } imageEntry
                    && imageEntry.OriginalPath is { Length: > 0 } path
                    && File.Exists(path))
                {
                    originalFile = path;
                    picture = LoadImageFile(path);
                }

                picture ??= (card.Thumbnail as BitmapSource) ?? DecodeThumbnailNow(card.Id);
                if (picture is null)
                {
                    return;
                }

                data.SetImage(picture);
                if (originalFile is not null)
                {
                    var drop = new System.Collections.Specialized.StringCollection();
                    drop.Add(originalFile);
                    data.SetFileDropList(drop);
                }

                break;

            default:
                var alive = card.Files.Where(File.Exists).ToList();
                if (alive.Count == 0)
                {
                    // Every path gone: a drag that silently produces nothing
                    // reads as breakage. One quiet toast says why not.
                    DeadDragNotice?.Invoke("原文件已不存在，无法拖出。");
                    return;
                }

                var dropList = new System.Collections.Specialized.StringCollection();
                dropList.AddRange([.. alive]);
                data.SetFileDropList(dropList);
                break;
        }

        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    /// <summary>Sent upward so the tray can say what the card cannot.</summary>
    public event Action<string>? DeadDragNotice;

    /// <summary>
    /// The archived original, decoded at full size for a drag-out. OnLoad +
    /// freeze so the payload never keeps the file open past the drop.
    /// </summary>
    private static BitmapSource? LoadImageFile(string path)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // expected: 原图文件在存在性检查与解码之间被保留清理删掉——
            // 拖出退回缩略图，不为一条正在过期的记录崩窗口。
            return null;
        }
    }

    private void OpenUri(BarCard card)
    {
        try
        {
            var target = card.Subtype == EntrySubtype.Email
                ? "mailto:" + card.Text.Trim()
                : card.Text.Trim();

            // 打开即弃（O-43）：.NET 9 的 Process 是 IDisposable，弃置不写
            // 句柄要等到终结器——浏览器自己会活。
            using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception failure)
        {
            // 用户点名要打开的东西没动静就是坏掉的样（O-24）：托盘说一句，
            // 日志留一条，窗口不碎。
            Log.Event(LogEvent.OpenLinkFailed, failure);
            DeadDragNotice?.Invoke("没能打开链接或邮箱地址。");
        }
    }

    // --- hover actions --------------------------------------------------------

    /// <summary>Which of the user's chosen actions this card can honour, in their order.</summary>
    public IReadOnlyList<string> ActionsFor(BarCard card)
        => HoverActions.AvailableFor(
            HoverActions.Sanitise(_settings.BarActions), card.Kind, card.HasOriginal,
            DeleteIsProtected(card));

    /// <summary>
    /// Favourites and pins, under their protection switches, have no delete
    /// entry point at all. The single clear-eyed delete — un-star first —
    /// stays available, so there is no "cannot delete at all" dead end.
    /// </summary>
    private bool DeleteIsProtected(BarCard card)
        => _settings.ProtectEntries
            && ((_settings.ProtectFavorites && card.Favorite)
                || (_settings.ProtectPinned && card.IsPinned));

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        // The preview hears about the card even when the tray visuals are not
        // reachable: policy first, presentation second.
        if (((FrameworkElement)sender).DataContext is BarCard entered)
        {
            RunPreviewCommand(_previewPolicy.HoverEnter(entered.Id));
        }

        if (Tree.FindDescendant<ActionTray>((DependencyObject)sender) is not { } tray
            || Tree.FindDescendant<HandoverText>((DependencyObject)sender) is not { } handover)
        {
            return;
        }

        handover.Yield();
        tray.Open();

        // Hover reveals the original behind a note: the note is the face the
        // user wrote, the content is what they come back for.
        if (((FrameworkElement)sender).DataContext is BarCard { Note.Length: > 0 } noted)
        {
            ApplyFace(noted, hovered: true);
        }
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is BarCard)
        {
            RunPreviewCommand(_previewPolicy.HoverLeave());
        }

        if (Tree.FindDescendant<ActionTray>((DependencyObject)sender) is not { } tray
            || Tree.FindDescendant<HandoverText>((DependencyObject)sender) is not { } handover)
        {
            return;
        }

        tray.Close();
        handover.Return();

        if (((FrameworkElement)sender).DataContext is BarCard { Note.Length: > 0 } noted)
        {
            ApplyFace(noted, hovered: false);
        }
    }

    /// <summary>Runs one hover action. Invoked from any card's tray via the container's subscription.</summary>
    public void RunHoverAction(string id, BarCard card, Button button)
        => ExecuteAction(id, card, button);

    /// <summary>
    /// The tray's ⋯（§6.1）：the folded low-frequency pair — 打开/定位 —
    /// listed in a popup beside the button that collected them. The window
    /// owns the chrome; the tray only knows where it sits.
    /// </summary>
    private Popup? _trayMorePopup;

    public void OpenTrayMore(BarCard card, IReadOnlyList<string> folded, Button anchor)
    {
        if (_trayMorePopup is { IsOpen: true })
        {
            _trayMorePopup.IsOpen = false;
            return;
        }

        var host = new StackPanel { MinWidth = 120 };

        foreach (var action in folded)
        {
            var captured = action;
            var key = BarKeys.TrayKey(action);
            host.Children.Add(MenuRow(
                HoverActions.Name(action),
                key ?? string.Empty,
                HoverActions.IsDestructive(captured),
                () =>
                {
                    _trayMorePopup!.IsOpen = false;
                    ExecuteAction(captured, card, feedback: null);
                }));
        }

        _trayMorePopup = new Popup
        {
            Child = WithPopupFont(MenuSurface(host)),
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        _trayMorePopup.Opened += (_, _) => FlipIntoWorkArea(_trayMorePopup);
        _trayMorePopup.IsOpen = true;
    }

    /// <summary>
    /// Executes one action. The keyboard uses this too, where there is no
    /// button to give feedback on.
    /// </summary>
    private void ExecuteAction(string id, BarCard card, Button? feedback)
    {
        // A real action is the strongest "user has found it" signal: the
        // teaching row steps aside and the counter ticks toward never-again.
        if (_hintShowing)
        {
            DismissFirstUseHint();
        }

        FirstUseHints.RegisterAction();

        // L2 适时教学（§5.2）：只数鼠标路径（feedback 带按钮即鼠标触发）；
        // 第 3 次且从未提示过时拿到那句话，随动作自己的反馈一行说出。
        _pendingKeyHint = feedback is not null ? MouseKeyHints.Note(id) : null;

        switch (id)
        {
            case "copy":
                SelfWrite(() => _store.BumpUse(card.Id));
                if (CopyCard(card))
                {
                    Confirm(feedback, true);
                    ShowFeedback("已复制");
                }
                else
                {
                    Confirm(feedback, false);
                }
                break;

            case "plain":
                // Strips every format: plain text and nothing else, so what
                // lands carries no styling from where it came. Never offered
                // for file entries — there is no plain form to strip.
                SelfWrite(() => _store.BumpUse(card.Id));
                if (_clipboard.SetText(card.Text))
                {
                    Confirm(feedback, true);
                    ShowFeedback("已按纯文本复制");
                }
                else
                {
                    Confirm(feedback, false);
                }
                break;

            case "paste":
                PasteEntry(card);
                break;

            case "open":
                OpenOriginal(card);
                break;

            case "locate":
                LocateOriginal(card);
                break;

            case "pin":
                SelfWrite(() => _store.SetPinned(card.Id, !card.IsPinned));
                ReloadData();
                break;

            case "favorite":
                SelfWrite(() => _store.SetFavorite(card.Id, !card.Favorite));

                // In place: a favourite joins a collection and never moves,
                // so the list around it must not so much as blink.
                card.Favorite = !card.Favorite;
                break;

            case "note":
                EditNote(card);
                break;

            case "group":
                OpenGroupChooser(card, feedback);
                break;

            case "delete":
                // The keyboard path has no tray to hide; the guard answers
                // for it what the hidden button answers for the mouse.
                if (DeleteIsProtected(card))
                {
                    _pendingKeyHint = null;
                    return;
                }

                // Snapshot before the delete: the undo re-inserts the whole
                // row — content, tags, group, note, pin — under a new id.
                var snapshot = _store.Get(card.Id);
                var groupName = snapshot is null ? null : _store.GroupOf(snapshot)?.Name;

                SelfWrite(() => _store.Delete(card.Id));
                _browser.Forget(card.Id);
                RemoveCard(card);
                UpdateFooter();
                EnsureActiveItem();

                if (snapshot is not null)
                {
                    ShowFeedback($"已删除「{TruncateFeedback(snapshot)}」",
                        [(snapshot, groupName)]);
                }
                break;
        }

        // 动作没有自己的反馈行时（置顶/收藏就地改卡），提示条自己占一行；
        // 已经随反馈说过的（ShowFeedback 消费掉），这里自然为空。
        if (_pendingKeyHint is { } hint)
        {
            ShowFeedback(hint);
        }
    }

    /// <summary>The feedback row is one line: keep a deleted name honest to it.</summary>
    private static string TruncateFeedback(Entry entry)
    {
        var text = entry.Note is { Length: > 0 } note ? note : entry.Text;
        text = text.Split('\n')[0].Trim();
        return text.Length <= 12 ? text : text[..12] + "…";
    }

    /// <summary>
    /// Copies an entry as itself: plain text always, the HTML and RTF forms
    /// when the entry has them, so a rich destination receives the formatting
    /// and a plain one receives readable text. The formats come from the
    /// payload table on the spot — a listed card does not carry them (O-22).
    /// </summary>
    private bool CopyCard(BarCard card)
        => card.Files.Count > 0
            ? _clipboard.SetFiles(card.Files)
            : _store.Get(card.Id) is { } full && (full.Html is { Length: > 0 } || full.Rtf is { Length: > 0 })
                ? _clipboard.SetRich(card.Text, full.Html, full.Rtf)
                : _clipboard.SetText(card.Text);

    /// <summary>
    /// Pastes into the window the user was in before summoning the bar. The
    /// bar hides first — until it does, it is the thing in the way of the
    /// foreground the paste needs. A rich entry pastes as itself: formats
    /// written first, then the keystroke into the restored window.
    ///
    /// The hide is the full <see cref="Dismiss"/> (O-37): this path once
    /// called Hide directly, which left the preview clock armed and skipped
    /// the lightweight drop — a pasted-from bar kept its decoded thumbnails
    /// and its timers alive for as long as it sat hidden. The reason rides
    /// through to the refresh policy, which keeps that invariant tested.
    /// </summary>
    private void PasteEntry(BarCard card)
    {
        if (!_returnTo.IsSomething)
        {
            _returnTo = ForegroundWindow.Current();
        }

        Dismiss(BarHideReason.Pasted);
        Pasted?.Invoke();

        if (card.Files.Count > 0)
        {
            if (_clipboard.SetFiles(card.Files))
            {
                _capture.PasteCurrentClipboard();
            }
        }
        else if (_store.Get(card.Id) is { } full
            && (full.Html is { Length: > 0 } || full.Rtf is { Length: > 0 }))
        {
            // The formats join the paste at the moment it happens; the card
            // itself never carried them (O-22).
            if (_clipboard.SetRich(card.Text, full.Html, full.Rtf))
            {
                _capture.PasteCurrentClipboard();
            }
        }
        else
        {
            _capture.Paste(card.Text);
        }
    }

    private void OpenOriginal(BarCard card)
    {
        var target = card.Files.FirstOrDefault(File.Exists)
            ?? (card.OriginalPath is { Length: > 0 } path && File.Exists(path) ? path : null);

        if (target is null)
        {
            return;
        }

        try
        {
            // 同 OpenUri：打开即弃（O-43）。
            using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception failure)
        {
            // 同 OpenUri（O-24）：点了没动静要有说法。文件搬走/失效是清理
            // 后的正常事，托盘一句足矣。
            Log.Event(LogEvent.OpenLinkFailed, failure, ("file", 1));
            DeadDragNotice?.Invoke("没能打开原文件，它可能已被移动或删除。");
        }
    }

    private void LocateOriginal(BarCard card)
    {
        var target = card.Files.FirstOrDefault(File.Exists)
            ?? (card.OriginalPath is { Length: > 0 } path && File.Exists(path) ? path : null);

        if (target is null)
        {
            return;
        }

        try
        {
            using var opened = System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.OpenLinkFailed, failure, ("locate", 1));
            DeadDragNotice?.Invoke("没能定位原文件的位置。");
        }
    }

    /// <summary>
    /// The note editor: one box, three exits. Owned by the bar so it stays on
    /// top of it and follows it away.
    /// </summary>
    private void EditNote(BarCard card)
    {
        var editor = new Window
        {
            Title = "备注",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Brush.Background"),
        };

        var box = new TextBox
        {
            Text = card.Note ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            Margin = new Thickness(12),
            Padding = new Thickness(6, 4, 6, 4),
        };
        box.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceInput");
        box.SetResourceReference(Control.ForegroundProperty, "Brush.Text");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 12),
        };

        var save = new Button { Content = "保存", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0) };
        var remove = new Button { Content = "删除备注", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };

        save.Click += (_, _) =>
        {
            SelfWrite(() => _store.SetNote(card.Id, box.Text));
            card.Note = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
            ApplyFace(card, hovered: false);
            editor.Close();
        };

        remove.Click += (_, _) =>
        {
            SelfWrite(() => _store.SetNote(card.Id, null));
            card.Note = null;
            ApplyFace(card, hovered: false);
            editor.Close();
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

    /// <summary>
    /// Puts the right text in the face: the note when there is one, the
    /// original while hovered. The note is the entry's public face because
    /// what the user wrote is what the user remembers.
    /// </summary>
    private void ApplyFace(BarCard card, bool hovered)
    {
        card.Face = card.Note is { Length: > 0 } && !hovered ? card.Note : card.Preview;
    }
}
