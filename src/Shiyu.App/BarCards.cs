using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shiyu.Core;

namespace Shiyu.App;

internal partial class BarWindow
{
    // --- cards ----------------------------------------------------------------

    /// <summary>
    /// Work a freshly built card is waiting on: its thumbnail bytes, and the
    /// file preview's path. Drained into one background batch per append;
    /// results land only while the generation they were captured in still
    /// describes the list on screen.
    /// </summary>
    private readonly record struct CardBackfill(BarCard Card, byte[]? ThumbnailPng, string? PreviewPath);

    private readonly List<CardBackfill> _pendingBackfills = [];

    /// <summary>Drops every in-flight card backfill: the list they described is gone.</summary>
    private readonly BackfillGate _cardBackfills = new();

    private BarCard CardFor(Entry entry, IReadOnlyDictionary<long, EntryBlobs>? blobs = null)
    {
        var collapsed = string.Join(' ', entry.Text.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var shown = Math.Max(1, _settings.BarFileCount);

        // Existence and directory-ness come from the cache, never the disk
        // (O-36): an offline UNC path costs an SMB timeout per raw probe, and
        // the bar used to pay it on this thread for every card, every summon.
        // An unknown path renders as alive; the background probe corrects the
        // row in place when it answers.
        bool Exists(string path) => _fileProbe.Lookup(path)?.Exists ?? true;

        var fileRows = entry.Kind == EntryKind.Files
            ? entry.Files.Take(shown)
                .Select(path => new FileRow(
                    Path.GetFileName(path) is { Length: > 0 } name ? name : path,
                    path,
                    _fileIcons.For(path, _fileProbe.Lookup(path)?.IsDirectory ?? false),
                    !Exists(path)))
                .ToList()
            : [];

        // The thumbnail and the file preview backfill from background decodes
        // (O-36): the row is laid out with the template's colour-block
        // placeholder the moment the data is read, and the pixels arrive when
        // they arrive — the placeholder is the same element at the same size,
        // so nothing shifts when the image lands.
        var thumbnailPng = entry.Kind == EntryKind.Image
            ? blobs?.GetValueOrDefault(entry.Id)?.ThumbnailPng
            : null;

        var previewPath = entry.Kind == EntryKind.Files
            && FileEntries.PreviewImagePath(entry.Files) is { } candidate
            && Exists(candidate)
                ? candidate
                : null;

        var card = new BarCard
        {
            Id = entry.Id,
            Kind = entry.Kind,
            Text = entry.Text,
            Preview = collapsed.Length > 500 ? collapsed[..500] + "…" : collapsed,
            DragHint = "双击粘贴到原来的窗口；Enter 粘贴选中项；按住左键拖出：文本入编辑器（带格式）、图片入聊天窗、文件入资源管理器",
            ShowToolTip = _settings.BarCardTooltips,
            // 类型标签只带信息量（§6.1/U-16）：纯文本/无子类型的译文之外的
            // 空白留给空串——模板据 KindLabelVisibility 整组隐藏。
            KindText = entry.Kind switch
            {
                EntryKind.Image => entry.ImageWidth > 0
                    ? $"图片 · {entry.ImageWidth}×{entry.ImageHeight}"
                    : "图片",
                EntryKind.Files => $"{entry.Files.Count} 个文件",
                _ => entry.Subtype switch
                {
                    EntrySubtype.Link => "链接",
                    EntrySubtype.Email => "邮箱",
                    EntrySubtype.Color => "颜色",
                    EntrySubtype.LocalPath => "路径",
                    _ => entry.TranslatedFrom is null ? "" : "译文",
                },
            },
            KindGlyph = entry.Kind switch
            {
                EntryKind.Image => "\uE8B9",
                EntryKind.Files => "\uE8B7",
                _ => "\uE8D2",
            },
            UseCount = entry.UseCount,

            // Relative for the scan, absolute for the hover; the usage count
            // lives on the type badge's tooltip (票 39) — a reward read on
            // demand, not an identity worn on the face.
            WhenText = RelativeTime.For(entry.CreatedAt, DateTimeOffset.Now),
            WhenToolTip = $"{entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
            Icon = _icons.For(entry.SourceApp),
            Thumbnail = null,
            OriginalPath = entry.OriginalPath,
            HasOriginal = entry.HasOriginal,
            Subtype = entry.Subtype,
            Files = entry.Files,
            Favorite = entry.Favorite,
            Note = entry.Note,
            GroupBadge = entry.GroupId is { } filedInto && _groupsById.TryGetValue(filedInto, out var pile)
                ? pile.Name
                : null,
            IsTranslation = entry.TranslatedFrom is not null,
            FileRows = fileRows,
            FilePreviewSource = null,
            AllPathsDead = entry.Kind == EntryKind.Files
                && entry.Files.All(path => _fileProbe.Lookup(path) is { Exists: false }),
            FileCount = entry.Files.Count,
            SwatchBrush = entry.Subtype == EntrySubtype.Color
                && SubtypeColor.TryParse(entry.Text, out var colour)
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(colour.A, colour.R, colour.G, colour.B))
                    .FrozenBrush()
                : null,
            TextLines = Math.Max(1, _settings.BarTextLines),
            ImageHeight = Math.Max(24, _settings.BarImageHeight),
            PixelWidth = entry.ImageWidth,
            PixelHeight = entry.ImageHeight,
            IsPinned = entry.IsPinned,
            IsProtected = _settings.ProtectEntries
                && ((_settings.ProtectFavorites && entry.Favorite)
                    || (_settings.ProtectPinned && entry.IsPinned)),
        };

        // The note is the public face; the original waits behind a hover.
        card.Face = entry.Note is { Length: > 0 } ? entry.Note : card.Preview;

        if (thumbnailPng is { Length: > 0 } || previewPath is not null)
        {
            _pendingBackfills.Add(new CardBackfill(card, thumbnailPng, previewPath));
        }

        return card;
    }

    /// <summary>
    /// A file copy made entirely of images previews as pictures: names alone
    /// answer "which file", not "what was in it". Runs on the thread pool
    /// (O-36) — the file read is disk latency, and this used to happen on the
    /// thread that draws. A missing or unreadable file falls back to the
    /// rows — a struck-through name says more than nothing.
    /// </summary>
    private static ImageSource? DecodeFileImage(string path, int pixelWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Max(1, pixelWidth);
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException
            or NotSupportedException or System.IO.FileFormatException)
        {
            // expected: 扩展名长得像图片、内容不是可解码位图——行兜底。
            return null;
        }
    }

    private void Rebuild()
    {
        // First: anything still decoding describes a list that is about to be
        // replaced. The gate drops its results; the placeholder state is built
        // into every fresh card anyway.
        _cardBackfills.Invalidate();
        _pendingBackfills.Clear();

        _pinned.Clear();
        _cards.Clear();
        _selected = null;
        Append(_browser.Loaded);
        PinnedHost.Visibility = _pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
        UpdateEmptyState();
        EnsureActiveItem();
    }

    /// <summary>
    /// The empty panel says which conditions emptied the list — a filter that
    /// ate everything is recoverable, and the words are the recovery map.
    /// </summary>
    private void UpdateEmptyState()
    {
        var empty = _pinned.Count == 0 && _cards.Count == 0;
        EmptyHost.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (!empty)
        {
            return;
        }

        var groupName = _selectedGroup is { } picked && _groupsById.TryGetValue(picked, out var group)
            ? group.Name
            : null;

        var copy = EmptyStates.For(
            new HistoryFilter
            {
                Query = SearchBox.Text,
                Favorite = FavoriteOnly.IsChecked == true ? true : null,
                Kind = _kindIndex switch
                {
                    1 => EntryKind.Text,
                    2 => EntryKind.Image,
                    3 => EntryKind.Files,
                    _ => null,
                },
                Group = _selectedGroup,
            },
            groupName,
            _store.Count());

        Empty.Title = copy.Headline;
        Empty.Description = copy.Hint;
        EmptyClear.Visibility = copy.OfferClear ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Append(IEnumerable<Entry> entries)
    {
        var batch = entries.ToList();

        // The one payload read per page: only image cards want a thumbnail,
        // so the batch asks for those ids alone rather than dragging payloads
        // through the list query (O-22).
        var blobs = _store.BlobsOf(
            [.. batch.Where(entry => entry.Kind == EntryKind.Image).Select(entry => entry.Id)]);

        // The store orders pinned first, so the partition is one pass: once
        // the first unpinned entry arrives, everything after it is too.
        var pastPinned = _cards.Count > 0;

        foreach (var entry in batch)
        {
            if (!entry.IsPinned)
            {
                pastPinned = true;
            }

            var card = CardFor(entry, blobs);
            card.RowKeyText = BarKeys.RowKey(_pinned.Count + _cards.Count + 1);
            (pastPinned ? _cards : _pinned).Add(card);
        }

        // Pixels and probe verdicts are the slow half of the page; they run
        // behind the paint that already happened, not ahead of it (O-36).
        RunBackfills(batch);
    }

    /// <summary>
    /// Two background batches behind one paint: the decodes (thumbnails and
    /// file previews, frozen on the pool thread and delivered through the
    /// dispatcher) and the existence probes (paths the cache has no fresh
    /// verdict for, recorded back into the cache and patched into the rows).
    ///
    /// Both carry the generation they were born in; a rebuild anywhere in
    /// between drops everything still in flight, so a recycled row can never
    /// receive a stranger's pixels or a verdict for a list that is gone.
    /// </summary>
    private void RunBackfills(List<Entry> batch)
    {
        if (_pendingBackfills.Count > 0)
        {
            var work = _pendingBackfills.ToArray();
            _pendingBackfills.Clear();
            var generation = _cardBackfills.Epoch;

            Task.Run(() =>
            {
                foreach (var item in work)
                {
                    var thumbnail = AppIconCache.Decode(item.ThumbnailPng, 320);
                    var preview = item.PreviewPath is null ? null : DecodeFileImage(item.PreviewPath, 320);

                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!_cardBackfills.IsCurrent(generation))
                        {
                            return;
                        }

                        if (thumbnail is not null)
                        {
                            item.Card.Thumbnail = thumbnail;
                        }

                        if (preview is not null)
                        {
                            item.Card.FilePreviewSource = preview;
                        }
                    });
                }
            });
        }

        var stale = new HashSet<string>(
            batch.Where(entry => entry.Kind == EntryKind.Files)
                .SelectMany(entry => entry.Files)
                .Where(_fileProbe.WantsProbe),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in batch)
        {
            if (entry.Kind == EntryKind.Files
                && FileEntries.PreviewImagePath(entry.Files) is { } preview
                && _fileProbe.WantsProbe(preview))
            {
                stale.Add(preview);
            }
        }

        if (stale.Count == 0)
        {
            return;
        }

        {
            var generation = _cardBackfills.Epoch;
            var paths = stale.ToArray();

            Task.Run(() =>
            {
                var verdicts = new Dictionary<string, FileVerdict>(StringComparer.OrdinalIgnoreCase);

                foreach (var path in paths)
                {
                    // A directory is "there" too — File.Exists alone would
                    // strike a folder copy through as dead.
                    var file = File.Exists(path);
                    var directory = !file && Directory.Exists(path);

                    _fileProbe.Record(path, file || directory, directory);
                    verdicts[path] = new FileVerdict(file || directory, directory);
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (!_cardBackfills.IsCurrent(generation))
                    {
                        return;
                    }

                    PatchExistence(verdicts);
                });
            });
        }
    }

    /// <summary>
    /// Lands probe verdicts in the rows already on screen: each row's dead
    /// flag follows its path's verdict, and the card-level "everything is
    /// gone" flag re-reads the cache the probes just filled.
    /// </summary>
    private void PatchExistence(IReadOnlyDictionary<string, FileVerdict> verdicts)
    {
        if (verdicts.Count == 0)
        {
            return;
        }

        foreach (var card in _pinned.Concat(_cards))
        {
            if (card.Kind != EntryKind.Files)
            {
                continue;
            }

            foreach (var row in card.FileRows)
            {
                if (verdicts.TryGetValue(row.FullPath, out var verdict))
                {
                    row.Dead = !verdict.Exists;
                }
            }

            card.AllPathsDead = card.Files.All(path => _fileProbe.Lookup(path) is { Exists: false });
        }
    }

    /// <summary>
    /// The footer's three voices: the count (total, or "3 / 161" when a filter
    /// is on — a paged list cannot count itself), the active filters read out
    /// as tokens（§6.1：footer 读出全部生效筛选——哪些层在起作用，Esc 才
    /// 有去处）, and the Esc hint, which follows the escape stack instead of
    /// asserting one behaviour while another is in force.
    /// </summary>
    private void UpdateFooter()
    {
        if (_feedbackHostOpen || _hintShowing)
        {
            return;
        }

        var total = _store.Count();
        var filtered = _store.CountMatching(_browser.Filter);
        var escHint = _browser.Filter.IsEmpty ? "Esc 隐藏" : "Esc 清除筛选";
        var tokens = FilterTokens();

        CountLabel.Text = tokens.Count == 0
            ? (filtered == total
                ? $"共 {total} 条 · {escHint}"
                : $"{filtered} / {total} 条 · {escHint}")
            : $"{filtered} / {total} 条 · {string.Join(" · ", tokens)} · {escHint}";
    }

    /// <summary>Every filter layer in force, as footer tokens. The query stays in its box; only its presence is named.</summary>
    private List<string> FilterTokens()
    {
        var parts = new List<string>();

        if (_kindIndex > 0)
        {
            parts.Add(_kindIndex switch
            {
                1 => "类型：文本",
                2 => "类型：图片",
                3 => "类型：文件",
                _ => "类型",
            });
        }

        if (_subtypeIndex > 0)
        {
            parts.Add(_subtypeIndex switch
            {
                1 => "子类型：链接",
                2 => "子类型：邮箱",
                3 => "子类型：颜色",
                4 => "子类型：路径",
                _ => "子类型",
            });
        }

        if (_selectedTag != AnyTag)
        {
            parts.Add($"标签「{_selectedTag}」");
        }

        if (SearchBox.Text.Length > 0)
        {
            parts.Add("搜索");
        }

        if (FavoriteOnly.IsChecked == true)
        {
            parts.Add("只看收藏");
        }

        if (_selectedGroup is { } picked && _groupsById.TryGetValue(picked, out var pile))
        {
            parts.Add($"分组「{pile.Name}」");
        }

        return parts;
    }

    private void UpdateCount() => UpdateFooter();

    /// <summary>Whether a feedback row is on show (and owns the footer).</summary>
    private bool _feedbackHostOpen;

    private System.Windows.Threading.DispatcherTimer? _feedbackTimer;

    private IReadOnlyList<(Entry Entry, string? Group)>? _undoPending;

    /// <summary>
    /// One feedback row at a time; an undo keeps it on show for the full five
    /// seconds the user was promised, a plain confirmation leaves quickly.
    /// A pending L2 key hint rides the same row（§5.2：一句话教一个键）.
    /// </summary>
    private void ShowFeedback(string text, IReadOnlyList<(Entry Entry, string? Group)>? undo = null)
    {
        _feedbackTimer?.Stop();
        _feedbackHostOpen = true;
        _undoPending = undo;

        if (_pendingKeyHint is { } hint)
        {
            text = $"{text} · {hint}";
            _pendingKeyHint = null;
        }

        FeedbackLabel.Text = text;
        UndoButton.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;
        FeedbackHost.Visibility = Visibility.Visible;
        CountLabel.Visibility = Visibility.Collapsed;

        _feedbackTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = undo is null ? TimeSpan.FromSeconds(1.6) : TimeSpan.FromSeconds(5),
        };
        _feedbackTimer.Tick += (_, _) => ClearFeedback();
        _feedbackTimer.Start();
    }

    private void ClearFeedback()
    {
        _feedbackTimer?.Stop();
        _feedbackTimer = null;
        _feedbackHostOpen = false;
        _undoPending = null;
        FeedbackHost.Visibility = Visibility.Collapsed;
        CountLabel.Visibility = Visibility.Visible;
        UpdateFooter();
    }

    private void OnUndoDelete(object sender, RoutedEventArgs e) => RestoreUndo();

    private void RestoreUndo()
    {
        if (_undoPending is not { } items)
        {
            return;
        }

        foreach (var (entry, group) in items)
        {
            // Re-filing by group name recreates the group if it went away
            // mid-window, which is the same promise delete made about entries.
            SelfWrite(() => _store.ImportEntry(entry, group));
        }

        ClearFeedback();
        ApplyFilter();
        ShowFeedback($"已恢复 {items.Count} 条");
    }

    // --- first-use hints ---------------------------------------------------------

    /// <summary>Whether the footer is currently teaching instead of counting.</summary>
    private bool _hintShowing;

    /// <summary>L2 待说的一句（§5.2）：随动作自己的反馈一行说出，或由反馈行代说。</summary>
    private string? _pendingKeyHint;

    private void ShowFirstUseHint()
    {
        _hintShowing = true;
        FeedbackLabel.Text = FirstUseHints.Text();
        UndoButton.Visibility = Visibility.Collapsed;
        FeedbackHost.Visibility = Visibility.Visible;
        CountLabel.Visibility = Visibility.Collapsed;
    }

    private void DismissFirstUseHint()
    {
        if (!_hintShowing)
        {
            return;
        }

        _hintShowing = false;
        FeedbackHost.Visibility = Visibility.Collapsed;
        CountLabel.Visibility = Visibility.Visible;
        UpdateFooter();
    }

    /// <summary>
    /// The store changed underneath — a copy from anywhere, a retention sweep,
    /// an import, an edit in the library window. Whether that means a reload
    /// is the refresh policy's verdict (票 16 / O-27): visible and not this
    /// window's own write → reload; anything else is ignored, which is exactly
    /// the boundary the old two-second probe kept.
    /// </summary>
    private void OnStoreChanged()
    {
        // This window's own writes already produced exactly the visual change
        // they meant to — favourite and note update their card in place, pin
        // reloads itself — so a reload here would only rebuild the list under
        // the user (O-37). The depth is read on the writer's thread; a benign
        // race with a background write costs one extra reload, never a miss.
        // The verdict travels whole: besides the reload it may say where the
        // list should stand afterwards (back at the newest when the user is
        // elsewhere).
        var command = _refreshPolicy.StoreChanged(selfWrite: _selfWrites > 0);
        if (command == BarRefreshCommand.None)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            RunRefresh(command);
        }
        else
        {
            // External writes arrive on the pipeline's thread; the cards
            // belong to the dispatcher's.
            Dispatcher.BeginInvoke(() => RunRefresh(command));
        }
    }

    /// <summary>How many of this window's own writes are in flight.</summary>
    private int _selfWrites;

    /// <summary>
    /// Runs one of this window's own writes, whose Changed event it intends
    /// to ignore because it handles the visual itself.
    /// </summary>
    private void SelfWrite(Action write)
    {
        _selfWrites++;
        try
        {
            write();
        }
        finally
        {
            _selfWrites--;
        }
    }

    private void OnCardsScrolled(object sender, ScrollChangedEventArgs e)
    {
        // Scrolling says the user has moved on: a hover preview goes, a
        // held-space preview answers to its key alone.
        RunPreviewCommand(_previewPolicy.Scrolled());

        if (!_browser.HasMore
            || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 400)
        {
            return;
        }

        var added = _browser.LoadMore();
        if (added > 0)
        {
            Append(_browser.Loaded.Skip(_browser.Loaded.Count - added));
        }
    }

    private void ReloadData()
    {
        _browser.Reset();
        RefreshTagChoices();
        Rebuild();
    }

    /// <summary>
    /// The newest entry in view and active (the policy's ScrollToNewest).
    /// A rebuild alone leaves the virtualized list at its old pixel offset:
    /// a bar once scrolled to the bottom reloaded straight back there,
    /// paging every page in again on the way, and showed the oldest records
    /// (用户实录 2026-10-04).
    ///
    /// The offset is queued on the ScrollViewer, whose queue runs before the
    /// ScrollChanged that would have reported the stale bottom: no page is
    /// fetched for a position the list is about to leave (probe bar-newest
    /// holds it — one page loaded afterwards, not the whole history).
    /// </summary>
    private void ScrollToNewest()
    {
        // The list's own viewer is the first one down its template; the
        // cards' text boxes nest theirs deeper.
        Tree.FindDescendant<ScrollViewer>(Cards)?.ScrollToTop();
        Select(VisibleRows.FirstOrDefault());
    }

    private void RemoveCard(BarCard card)
    {
        // A preview of a card that no longer exists is a panel describing a
        // ghost; it comes down before the row does.
        if (_preview is { IsVisible: true, CardId: var shown } && shown == card.Id)
        {
            RunPreviewCommand(_previewPolicy.Escape());
        }

        var inRest = _cards.IndexOf(card);
        if (inRest >= 0)
        {
            _cards.RemoveAt(inRest);
        }
        else
        {
            _pinned.Remove(card);
        }

        if (_pinned.Count == 0)
        {
            PinnedHost.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The success feedback: the pressed button becomes a tick for a second,
    /// and — only if the user asked — a sound joins it.
    /// </summary>
    private void Confirm(Button? button, bool succeeded)
    {
        if (button is null)
        {
            return;
        }

        if (!succeeded)
        {
            button.Content = "✗";
            return;
        }

        if (_settings.ActionSound)
        {
            System.Media.SystemSounds.Asterisk.Play();
        }

        var glyph = button.Content;
        button.Content = "✓";

        var restore = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        restore.Tick += (_, _) =>
        {
            restore.Stop();
            button.Content = glyph;
        };
        restore.Start();
    }
}
