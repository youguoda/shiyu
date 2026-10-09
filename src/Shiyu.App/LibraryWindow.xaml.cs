using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// The library: the place for the weekly sort-through, as opposed to the quick
/// bar's dozens-of-times-a-day paste. It may be large and may linger.
///
/// 票 24 重设计（UI 报告 §6.4）：系统窗框 + Mica；头部 48 的 SearchBox 吃下
/// 全部筛选（token 化）；命令栏 40 取代底部按钮墙（危险操作收进「⋯」走
/// ContentDialog）；列表带日期分组头（今天/昨天/本周/更早，吸顶）；详情
/// 三态；撤销条浮在列表左下取代 StatusLabel。键盘模型见 <see cref="LibraryKeys"/>
/// （§5.2），详情三态与 Agent 结果区见 <see cref="LibraryDetail"/>，撤销/轻
/// 反馈见 <see cref="LibraryToast"/>，弹层与菜单见 <see cref="LibraryMenus"/>。
/// </summary>
public partial class LibraryWindow : Window
{
    /// <summary>
    /// Long enough that typing a word is one search rather than six, short
    /// enough that the list feels like it is keeping up.
    /// </summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(180);

    /// <summary>§6.4 窗口：宽度 ≥ 960 双栏，更窄单栏（Space 全屏预览兜底）。</summary>
    private const double DualPaneThreshold = 960;

    /// <summary>§6.4 列表：双栏时列表列 400，详情拿走其余。</summary>
    private const double ListColumnWidth = 400;

    private readonly EntryStore _store;
    private readonly WindowsClipboardWriter? _clipboard;
    private readonly ImageArchive _images;
    private readonly Func<IStreamingModel> _model;
    private readonly HistoryBrowser _browser;
    private readonly ObservableCollection<EntryItem> _items = [];
    private readonly AppIconCache _icons;
    private readonly DispatcherTimer _searchDebounce;

    private readonly Func<AppSettings> _settings;
    private readonly ClipboardPipeline? _pipeline;

    // --- 筛选状态（token 的数据面）---------------------------------------------
    // 四组各占一枚 token：类型 / 日期 / 子类型 / 标签。_tokenOrder 记录加入
    // 顺序，Esc 逐层从最新的弹起（§5.2）。
    private int _kindIndex;
    private int _subtypeIndex;
    private DateChoice _dateChoice = DateChoice.None;
    private DateTime? _customFrom;
    private DateTime? _customTo;
    private string? _tag;
    private readonly List<string> _tokenOrder = [];

    /// <summary>自己发起的写入不触发重载（写入路径自己刷新了视觉，票 12 的 SelfWrite 纪律）。</summary>
    private int _selfWrites;

    private enum DateChoice { None, Today, Days7, Days30, Custom }

    public LibraryWindow(
        EntryStore store,
        WindowsClipboardWriter clipboard,
        ImageArchive images,
        Func<IStreamingModel> model,
        AppIconCache icons,
        Func<AppSettings>? settings = null,
        ClipboardPipeline? pipeline = null)
    {
        InitializeComponent();

        _store = store;
        _clipboard = clipboard;
        _images = images;
        _model = model;
        _browser = new HistoryBrowser(store);
        _icons = icons;
        _settings = settings ?? (() => new AppSettings());
        _pipeline = pipeline;

        // 票 19 spike 配方（ADR-0012 §7，与票 23 设置窗同款）：非分层窗口透出
        // DWM 材质的前提是重定向面底色透明。材质没被系统接受时（旧系统），
        // 回退成不透明底色一档。
        SourceInitialized += (_, _) =>
        {
            if (System.Windows.PresentationSource.FromVisual(this)
                is System.Windows.Interop.HwndSource { CompositionTarget: { } target })
            {
                target.BackgroundColor = Colors.Transparent;
            }
        };
        Backdrop.Attach(this, () => BackdropKind.Mica, applied =>
        {
            if (!applied)
            {
                RootGrid.SetResourceReference(BackgroundProperty, "Brush.Background");
            }
        });

        _searchDebounce = new DispatcherTimer { Interval = SearchDelay };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter();
        };

        // An undo left open when the window goes away must not leak the
        // kept-back original files: the expiry commits them.
        Closed += (_, _) =>
        {
            CommitUndoExpiry();
            _store.Changed -= OnStoreChanged;
        };

        // 别处的写入（窄条粘贴/置顶、导入、留存清扫）即时反映到本窗；自己的
        // 写入路径已经刷新过视觉，跳过（票 12）。
        _store.Changed += OnStoreChanged;

        SetupList();
        BuildFilterFlyout();
        RefreshTagChoices();
        BuildMoreMenu();

        SearchBox.GotKeyboardFocus += (_, _) => SearchPill.SetResourceReference(
            Border.BorderBrushProperty, "Brush.Accent");
        SearchBox.LostKeyboardFocus += (_, _) => SearchPill.SetResourceReference(
            Border.BorderBrushProperty, "Brush.StrokeStrong");

        // 票 27 / U-29：图标钮与复合内容钮的自动化名。命令栏的名字从 KeyMap
        // 取（名称单源）——按住 Ctrl 原位换键帽时可见文字消失，这个名字是
        // 键帽教学期间屏幕阅读器唯一的真相；「筛选」「更多操作」没有 KeyMap
        // 行（复合内容 UIA 派生不出名字），就地一句。
        AutomationProperties.SetName(SearchBox, "搜索历史");
        AutomationProperties.SetName(FilterButton, "筛选");
        AutomationProperties.SetName(CopyButton, KeyMap.AutomationName("copy", KeySurface.Library));
        AutomationProperties.SetName(PinButton, KeyMap.AutomationName("pin", KeySurface.Library));
        AutomationProperties.SetName(FavoriteButton, KeyMap.AutomationName("favorite", KeySurface.Library));
        AutomationProperties.SetName(TagButton, KeyMap.AutomationName("tag", KeySurface.Library));
        AutomationProperties.SetName(DeleteButton, KeyMap.AutomationName("delete", KeySurface.Library));
        AutomationProperties.SetName(MoreButton, "更多操作");
        // 轻反馈/撤销条是动态文本（LibraryToast）：Polite 活区，说了不打断。
        AutomationProperties.SetLiveSetting(ToastText, AutomationLiveSetting.Polite);

        EntryList.ListScrolled += OnListScrolled;
        EntryList.ItemsSource = _items;

        ApplyDetailLayout(ActualWidth >= DualPaneThreshold);
        RefreshAgentActions();
        Reload();
        Loaded += (_, _) => EntryList.Focus();
    }

    /// <summary>
    /// 列表视图：分组（GroupKey）+ 组序（置顶→今天→昨天→本周→更早）。
    /// 分组头是不可选的 GroupItem，↑↓ / Ctrl+A 天然越过。
    /// </summary>
    private void SetupList()
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EntryItem.GroupKey)));
        view.CustomSort = EntryRowOrder.Instance;

        var template = new DataTemplate();
        var factory = new System.Windows.FrameworkElementFactory(typeof(GroupHeaderView));
        template.VisualTree = factory;
        EntryList.GroupStyle.Add(new GroupStyle { HeaderTemplate = template });
    }

    /// <summary>外部的库变化：重载并尽量按 id 保住选择（用户正看着的条目不该消失）。</summary>
    private void OnStoreChanged()
    {
        if (_selfWrites > 0)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            ReloadPreservingSelection();
        }
        else
        {
            Dispatcher.BeginInvoke(ReloadPreservingSelection);
        }
    }

    /// <summary>Runs one of this window's own writes, whose Changed event it will ignore.</summary>
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

    /// <summary>
    /// Activated 时刷新一次 Agent 动作可用性：设置可能在窗外被改好
    /// （引导完成、导入备份），回到本窗就该跟着亮起来。
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RefreshAgentActions();
    }

    /// <summary>Reads the first page again, e.g. after the history changed underneath.</summary>
    public void Reload()
    {
        _browser.Reset();
        Rebuild();
    }

    /// <summary>窗口被再次呼出：两页都可能在它藏着时变过，当前页那一本重读。</summary>
    public void ReloadOnReturn()
    {
        Reload();
        if (_logPageActive)
        {
            ReloadLog();
        }
    }

    private void ReloadPreservingSelection()
    {
        var chosen = EntryList.SelectedItems.OfType<EntryItem>().Select(item => item.Id).ToHashSet();
        Reload();
        foreach (var item in _items.Where(item => chosen.Contains(item.Id)))
        {
            EntryList.SelectedItems.Add(item);
        }

        RefreshTagChoices();
    }

    private void Rebuild()
    {
        _items.Clear();
        Append(_browser.Loaded);
        UpdateChrome();
        UpdateSticky();
    }

    private void Append(IEnumerable<Entry> entries)
    {
        var batch = entries.ToList();

        // One payload read per page, for the image rows alone — list rows are
        // narrow by design and the thumbnail is the only payload a row shows
        // (O-22).
        var blobs = _store.BlobsOf(
            [.. batch.Where(entry => entry.Kind == EntryKind.Image).Select(entry => entry.Id)]);

        foreach (var entry in batch)
        {
            _items.Add(EntryItem.From(entry, blobs.GetValueOrDefault(entry.Id), _icons.For));
        }
    }

    private void UpdateChrome()
    {
        // "筛选数 / 总数"（§6.4 头部）：与窄条同口径——筛完不变的那个
        // "共 N 条"是评审点名的谎话。
        CountLabel.Text = $"{_store.CountMatching(_browser.Filter)} / {_store.Count()}";

        ShowListEmptyState(_items.Count == 0
            ? EmptyStates.For(_browser.Filter, null, _store.Count())
            : null);
    }

    // --- 空态（列表侧；详情侧的空态见 LibraryDetail）------------------------------

    private EmptyState? _listEmpty;

    private void ShowListEmptyState(EmptyStateCopy? copy)
    {
        if (copy is null)
        {
            if (_listEmpty is not null)
            {
                _listEmpty.Visibility = Visibility.Collapsed;
            }

            return;
        }

        if (_listEmpty is null)
        {
            _listEmpty = new EmptyState
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(24, 0, 24, 0),
            };
            (EntryList.Parent as Panel)?.Children.Add(_listEmpty);
        }

        _listEmpty.Title = copy.Headline;
        _listEmpty.Description = copy.Hint;
        _listEmpty.Content = copy.OfferClear ? BuildClearFiltersButton() : null;
        _listEmpty.Visibility = Visibility.Visible;
    }

    private Button BuildClearFiltersButton()
    {
        var clear = new Button { Content = "清除筛选", Padding = new Thickness(16, 5, 16, 5), Cursor = Cursors.Hand };
        clear.Click += (_, _) => ClearAllFilters();
        return clear;
    }

    // --- 筛选（数据面 → token 视觉 → HistoryFilter）--------------------------------

    private HistoryFilter ComposeFilter()
    {
        var now = DateTimeOffset.Now;
        DateTimeOffset? from = _dateChoice switch
        {
            DateChoice.Today => new DateTimeOffset(now.Date, now.Offset),
            DateChoice.Days7 => now.AddDays(-7),
            DateChoice.Days30 => now.AddDays(-30),
            DateChoice.Custom when _customFrom is { } pick => new DateTimeOffset(pick.Date, now.Offset),
            _ => null,
        };

        // Whole days, inclusive: a user picking today means all of today, not
        // the instant midnight began. Shortcut ranges run to now.
        DateTimeOffset? to = _dateChoice == DateChoice.Custom && _customTo is { } end
            ? new DateTimeOffset(end.Date.AddDays(1).AddTicks(-1), now.Offset)
            : null;

        return new HistoryFilter
        {
            Query = SearchBox.Text,
            Tag = _tag,
            Kind = _kindIndex switch
            {
                1 => EntryKind.Text,
                2 => EntryKind.Image,
                3 => EntryKind.Files,
                _ => null,
            },
            Subtype = _subtypeIndex switch
            {
                1 => EntrySubtype.Link,
                2 => EntrySubtype.Email,
                3 => EntrySubtype.Color,
                4 => EntrySubtype.LocalPath,
                _ => null,
            },
            From = from,
            To = to,
        };
    }

    private void ApplyFilter()
    {
        _browser.Filter = ComposeFilter();
        Rebuild();
    }

    private void ClearAllFilters()
    {
        SearchBox.Text = string.Empty;
        _kindIndex = 0;
        _subtypeIndex = 0;
        _dateChoice = DateChoice.None;
        _customFrom = null;
        _customTo = null;
        _tag = null;
        _tokenOrder.Clear();
        RefreshTokens();
        SyncFilterFlyout();
        ApplyFilter();
    }

    /// <summary>token 的加入顺序归一入口：Esc 从这里弹最新的（§5.2 逐层）。</summary>
    private void TouchToken(string key)
    {
        _tokenOrder.Remove(key);
        _tokenOrder.Add(key);
    }

    /// <summary>把某一组筛选归零（token 弹出、芯片点击共用）。</summary>
    private void ResetToken(string key)
    {
        _tokenOrder.Remove(key);
        switch (key)
        {
            case "kind": _kindIndex = 0; break;
            case "subtype": _subtypeIndex = 0; break;
            case "date":
                _dateChoice = DateChoice.None;
                _customFrom = null;
                _customTo = null;
                break;
            case "tag": _tag = null; break;
        }
    }

    /// <summary>Esc 的"弹掉最新一枚 token"。</summary>
    private void PopNewestToken()
    {
        if (_tokenOrder.Count == 0)
        {
            return;
        }

        ResetToken(_tokenOrder[^1]);
        RefreshTokens();
        SyncFilterFlyout();
        ApplyFilter();
    }

    /// <summary>token 芯片视觉（§6.4 头部）：一枚 token = 一个生效的筛选。</summary>
    private void RefreshTokens()
    {
        TokenHost.Children.Clear();

        foreach (var (key, label) in ActiveTokens())
        {
            var tokenKey = key;
            var chip = new Button
            {
                Height = 22,
                Padding = new Thickness(8, 0, 6, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"点击移除筛选「{label}」",
            };
            chip.SetResourceReference(StyleProperty, "FlyoutButton");
            chip.SetResourceReference(Control.BackgroundProperty, "Brush.AccentSubtle");
            chip.Click += (_, _) =>
            {
                ResetToken(tokenKey);
                RefreshTokens();
                SyncFilterFlyout();
                ApplyFilter();
            };

            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
            var close = new TextBlock { Text = "\uE711", Margin = new Thickness(6, 0, 0, 0) };
            close.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
            close.SetResourceReference(TextBlock.FontSizeProperty, "Size.IconXs");
            close.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            close.VerticalAlignment = VerticalAlignment.Center;

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(text);
            panel.Children.Add(close);
            chip.Content = panel;
            TokenHost.Children.Add(chip);
        }
    }

    private IEnumerable<(string Key, string Label)> ActiveTokens()
    {
        foreach (var key in _tokenOrder)
        {
            var label = key switch
            {
                "kind" => _kindIndex switch
                {
                    1 => "类型：文本",
                    2 => "类型：图片",
                    3 => "类型：文件",
                    _ => null,
                },
                "subtype" => _subtypeIndex switch
                {
                    1 => "链接",
                    2 => "邮箱",
                    3 => "颜色",
                    4 => "路径",
                    _ => null,
                },
                "date" => _dateChoice switch
                {
                    DateChoice.Today => "今天",
                    DateChoice.Days7 => "近 7 天",
                    DateChoice.Days30 => "近 30 天",
                    DateChoice.Custom when _customFrom is { } f && _customTo is { } t =>
                        $"{f:M\\/d} ~ {t:M\\/d}",
                    _ => null,
                },
                "tag" => _tag is null ? null : $"#{_tag}",
                _ => null,
            };

            if (label is not null)
            {
                yield return (key, label);
            }
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Restarted on every keystroke, so only the pause at the end searches.
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>搜索框的键盘细节：空文本上的 Backspace 弹掉最后一枚 token（§6.4）。</summary>
    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back && SearchBox.Text.Length == 0 && _tokenOrder.Count > 0)
        {
            e.Handled = true;
            PopNewestToken();
        }
    }

    // --- 布局 -----------------------------------------------------------------------

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyDetailLayout(e.NewSize.Width >= DualPaneThreshold);

    /// <summary>
    /// Two columns only when there is room for both（§6.4）：≥960 时列表 400 +
    /// 详情，更窄时详情整个收起，Space 的全屏预览接住"看全文"。
    /// </summary>
    private void ApplyDetailLayout(bool wide)
    {
        ListColumn.Width = wide ? new GridLength(ListColumnWidth) : new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DetailPane.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetail();
    }

    // --- 选择 -----------------------------------------------------------------------

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_movingSelection)
        {
            _anchor = _lead = EntryList.SelectedIndex;
        }

        SyncCommandBarToSelection();
        SyncRowChecks();
        UpdateDetail();
        UpdateSticky();
    }

    /// <summary>命令栏对齐真实状态：置顶钮说真话，收藏星空心/实心（§6 北极星 3）。</summary>
    private void SyncCommandBarToSelection()
    {
        var primary = Primary;

        PinLabel.Text = primary is { IsPinned: true } ? "取消置顶" : "置顶";
        PinGlyphIcon.Text = primary is { IsPinned: true } ? "\uE77A" : "\uE718";
        FavoriteGlyph.Text = primary is { Favorite: true } ? "\uE735" : "\uE734";
    }

    /// <summary>多选时每行出现复选框（§6.4）：已实现的行立即生效，之后的行在容器准备时跟上。</summary>
    private void SyncRowChecks()
    {
        var multi = EntryList.SelectedItems.Count >= 2;
        EntryList.MultiCheckMode = multi;
        foreach (var item in EntryList.Items)
        {
            if (EntryList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row)
            {
                RowProps.SetShowCheck(row, multi);
            }
        }
    }

    /// <summary>当前的主选条目（命令栏与详情都作用于它）。</summary>
    private EntryItem? Primary => EntryList.SelectedItem as EntryItem;

    private List<EntryItem> SelectedItems() => [.. EntryList.SelectedItems.OfType<EntryItem>()];

    // --- 吸顶分组头 -------------------------------------------------------------------

    private void UpdateSticky()
    {
        if (EntryList.Scroller is not { } scroller || _items.Count == 0
            || scroller.VerticalOffset <= 2)
        {
            StickyHeader.Visibility = Visibility.Collapsed;
            return;
        }

        if (EntryList.GroupAtViewportTop(StickyHeader.Height) is { } key)
        {
            StickyTitle.Text = key;
            StickyHeader.Visibility = Visibility.Visible;
        }
        else
        {
            StickyHeader.Visibility = Visibility.Collapsed;
        }
    }

    private void OnListScrolled(object? sender, ScrollChangedEventArgs e)
    {
        UpdateSticky();

        if (e.VerticalChange <= 0 || !_browser.HasMore)
        {
            return;
        }

        // Fetch the next page slightly before the user reaches the bottom, so
        // scrolling does not stutter at the seam.
        var remaining = e.ExtentHeight - e.VerticalOffset - e.ViewportHeight;
        if (remaining > e.ViewportHeight)
        {
            return;
        }

        var before = _browser.Loaded.Count;
        if (_browser.LoadMore() > 0)
        {
            Append(_browser.Loaded.Skip(before));
            UpdateChrome();
        }
    }

    // --- 动作：复制 / 置顶 / 收藏 ------------------------------------------------------

    /// <summary>
    /// Copy what is selected. Single: the entry's text (an image goes out by
    /// dragging — clipboard interop for bitmaps is not this feature's job).
    /// Multiple: the texts joined by blank lines — this window has no paste
    /// target, so that is what"复制全部"means here（§5.2：Enter 复制）.
    /// </summary>
    private void CopySelection()
    {
        var selected = SelectedItems();
        if (selected.Count == 0)
        {
            ShowLightFeedback("先选中一条记录");
            return;
        }

        foreach (var chosen in selected)
        {
            // The entry came back into the world; that is what a use is.
            SelfWrite(() => _store.BumpUse(chosen.Id));
        }

        if (selected.Count > 1)
        {
            var texts = selected.Where(item => item.Kind != EntryKind.Image)
                .Select(item => item.Text)
                .ToList();
            if (texts.Count == 0)
            {
                ShowLightFeedback("所选都是图片——图片请逐条拖出另存");
                return;
            }

            if (_clipboard?.SetText(string.Join(Environment.NewLine + Environment.NewLine, texts)) != true)
            {
                ShowError("复制失败", "剪贴板被其他程序占用，稍后再试。");
                return;
            }

            ShowLightFeedback($"已复制 {texts.Count} 条文本");
            return;
        }

        var item = selected[0];
        if (item.Thumbnail is not null)
        {
            // The clipboard writer handles text; putting a bitmap back would be
            // a separate piece of interop this feature does not need. Dragging
            // the file out covers what the user actually wants to do with it.
            ShowLightFeedback("图片条目请直接拖出到文件夹另存");
            return;
        }

        if (_clipboard?.SetText(item.Text) != true)
        {
            ShowError("复制失败", "剪贴板被其他程序占用，稍后再试。");
            return;
        }

        // Shiyu suppresses its own clipboard writes, so re-copying would
        // otherwise leave the entry where it was. Moving it to the top is what
        // the user just expressed a preference for.
        SelfWrite(() => _store.Touch(item.Id, DateTimeOffset.UtcNow));
        ShowLightFeedback("已复制");
    }

    private void OnCopySelected(object sender, RoutedEventArgs e) => CopySelection();

    /// <summary>双击 = 复制（与窄条的双击粘贴同一块肌肉记忆）。</summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => CopySelection();

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        var selected = SelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        // A mixed selection reads as "make them all pinned" — 置顶是对一群的
        // 整理动作，不是开关翻转。
        var pin = selected.Any(item => !item.IsPinned);
        foreach (var item in selected)
        {
            SelfWrite(() => _store.SetPinned(item.Id, pin));
        }

        // Reloaded rather than patched in place: pinning changes where the
        // entry belongs in the list, not just how it looks.
        Reload();
        ShowLightFeedback(pin ? $"已置顶 {selected.Count} 条" : $"已取消置顶 {selected.Count} 条");
    }

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        var selected = SelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        var favorite = !selected[0].Favorite;
        foreach (var item in selected)
        {
            SelfWrite(() => _store.SetFavorite(item.Id, favorite));
        }

        Reload();
        ShowLightFeedback(favorite ? $"已收藏 {selected.Count} 条" : $"已取消收藏 {selected.Count} 条");
    }

    // --- 标签 -------------------------------------------------------------------------

    /// <summary>Refills the tag choices everywhere they appear (筛选弹层的标签芯片).</summary>
    private void RefreshTagChoices()
    {
        SyncFilterFlyout();
    }

    private void AddTagToSelection(string tag)
    {
        var selected = SelectedItems();
        if (selected.Count == 0 || tag.Trim().Length == 0)
        {
            return;
        }

        foreach (var item in selected)
        {
            SelfWrite(() => _store.AddTag(item.Id, tag.Trim()));
        }

        RefreshTagChoices();
        Reload();
        ShowLightFeedback($"已把「{tag.Trim()}」加到 {selected.Count} 条");
    }

    private void RemoveTagFromSelection(string tag)
    {
        var selected = SelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var item in selected)
        {
            SelfWrite(() => _store.RemoveTag(item.Id, tag));
        }

        RefreshTagChoices();
        Reload();
        ShowLightFeedback($"已从 {selected.Count} 条去掉「{tag}」");
    }

    // --- 删除 / 撤销 --------------------------------------------------------------------

    private void OnDeleteSelected(object sender, RoutedEventArgs e) => DeleteSelection();

    /// <summary>
    /// 删除所选：可撤销级（§6.5）不弹框，直接删 + 撤销条。快照先留，原图在
    /// 撤销窗口关闭后才真走——undo 拿不回图片就不是 undo。
    /// </summary>
    private void DeleteSelection()
    {
        var selected = SelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        var index = EntryList.SelectedIndex;

        var snapshots = new List<(Entry Entry, string? Group)>();
        foreach (var item in selected)
        {
            var snapshot = _store.Get(item.Id);
            var groupName = snapshot is null ? null : _store.GroupOf(snapshot)?.Name;
            SelfWrite(() => _store.Delete(item.Id));
            _browser.Forget(item.Id);
            _items.Remove(item);
            if (snapshot is not null)
            {
                snapshots.Add((snapshot, groupName));
            }
        }

        // Keep the user where they were rather than sending them to the top.
        EntryList.SelectedIndex = Math.Min(Math.Max(index, 0), _items.Count - 1);
        UpdateChrome();
        RefreshTagChoices();

        if (snapshots.Count == 0)
        {
            ShowLightFeedback($"已删除 {selected.Count} 条");
            return;
        }

        OfferUndo(snapshots);
        ShowUndoFeedback($"已删除 {snapshots.Count} 条");
    }

    private List<(Entry Entry, string? Group)>? _undoItems;

    /// <summary>
    /// Holds a deletion open for five seconds（撤销条的悬停暂停可以延长它——
    /// 暂停就是用户在说"等等"）。The kept-back original file is removed only
    /// when the window closes without an undo, so "撤销" restores everything
    /// the delete took away.
    /// </summary>
    private void OfferUndo(List<(Entry Entry, string? Group)> items) => _undoItems = items;

    private void CommitUndoExpiry()
    {
        // 翻译记录的删除早已落库，撤销窗口一关，留着的那份就不用了。
        _undoLog = null;

        if (_undoItems is { } items)
        {
            foreach (var (entry, _) in items)
            {
                if (entry.OriginalPath is { Length: > 0 } original)
                {
                    _images.Delete(original);
                }
            }

            _undoItems = null;
        }
    }

    private void OnUndoDelete(object sender, RoutedEventArgs e) => UndoDelete();

    private void UndoDelete()
    {
        if (_undoLog is { } records)
        {
            UndoLogDelete(records);
            return;
        }

        if (_undoItems is not { } items)
        {
            return;
        }

        _undoItems = null;
        foreach (var (entry, group) in items)
        {
            SelfWrite(() => _store.ImportEntry(entry, group));
        }

        Reload();
        RefreshTagChoices();
        HideToast();
        ShowLightFeedback($"已恢复 {items.Count} 条");
    }

    /// <summary>Removes the files behind image entries that are about to go.</summary>
    private void DeleteOriginalsOf(IEnumerable<Entry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.OriginalPath is { Length: > 0 } path)
            {
                _images.Delete(path);
            }
        }
    }

    // --- 危险操作（§6.5 分级确认）--------------------------------------------------------

    /// <summary>「清空全部历史」：不可撤销·全部级——按钮写明条数，先提醒可导出。</summary>
    private void AskClearAll()
    {
        var total = _store.Count();
        if (total == 0)
        {
            return;
        }

        // Protected entries stay, and the copy says so: a number the user can
        // check beats a surprise after the fact.
        var guard = _settings();
        var keepFavorites = guard.ProtectEntries && guard.ProtectFavorites;
        var keepPinned = guard.ProtectEntries && guard.ProtectPinned;
        var protectedCount = _store.CountProtected(keepFavorites, keepPinned);
        var message = protectedCount > 0
            ? $"将永久删除全部 {total} 条中未受保护的 {total - protectedCount} 条，无法撤销。"
                + $"受收藏/置顶保护的 {protectedCount} 条会保留。"
            : $"将永久删除全部 {total} 条历史记录，无法撤销。";

        // §6.5：按钮 = 动词+数量；默认焦点与 Enter 都落在取消。
        var answer = ContentDialog.Show(
            this,
            "清空全部历史",
            message + "\n想留底的话，可以先到 设置 › 常规 › 备份 导出一份。",
            new ContentDialogButton("取消", ContentDialogButtonStyle.Standard, IsCancelFocus: true),
            new ContentDialogButton($"清空 {total - protectedCount} 条", ContentDialogButtonStyle.Danger));

        if (answer != 1)
        {
            return;
        }

        DeleteOriginalsOf(_store.ImagesWithOriginals(keepFavorites, keepPinned));
        SelfWrite(() => _ = _store.DeleteAll(keepFavorites, keepPinned));
        Reload();
        RefreshTagChoices();
        ShowLightFeedback(protectedCount > 0
            ? $"已删除 {total - protectedCount} 条，保留 {protectedCount} 条受保护"
            : $"已清空 {total} 条");
    }

    /// <summary>
    /// 「按时间段删除…」（§6.5 不可撤销·有范围）：范围选择搬进对话框（评审
    /// 3.8 P1：页面上四枚一模一样的日期选择正是它挪走的理由），条数实时可见
    /// ——按之前就知道要按掉多少。
    /// </summary>
    private void AskDeleteRange()
    {
        var from = new DatePicker { Width = 132 };
        var to = new DatePicker { Width = 132, Margin = new Thickness(8, 0, 0, 0) };
        var live = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        live.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        var guard = _settings();
        var keepFavorites = guard.ProtectEntries && guard.ProtectFavorites;
        var keepPinned = guard.ProtectEntries && guard.ProtectPinned;

        (int InRange, int Protected)? RangeCounts()
        {
            if (RangeFromPickers(from, to) is not ({ } start, { } end))
            {
                return null;
            }

            return (
                _store.CountMatching(new HistoryFilter { From = start, To = end }),
                _store.CountProtectedBetween(start, end, keepFavorites, keepPinned));
        }

        void RefreshLive()
        {
            if (RangeCounts() is not { } counts || RangeFromPickers(from, to) is not ({ } rangeStart, { } rangeEnd))
            {
                live.Text = "选好起止日期后，这里会实时显示将删除的条数。";
                return;
            }

            live.Text = counts.Protected > 0
                ? $"{rangeStart:yyyy-MM-dd} 至 {rangeEnd:yyyy-MM-dd} 之间共 {counts.InRange} 条，"
                    + $"其中受收藏/置顶保护的 {counts.Protected} 条会保留。"
                : $"{rangeStart:yyyy-MM-dd} 至 {rangeEnd:yyyy-MM-dd} 之间共 {counts.InRange} 条。";
        }

        from.SelectedDateChanged += (_, _) => RefreshLive();
        to.SelectedDateChanged += (_, _) => RefreshLive();

        var pickers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        pickers.Children.Add(new TextBlock
        {
            Text = "起",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        pickers.Children.Add(from);
        pickers.Children.Add(new TextBlock
        {
            Text = "止",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0),
        });
        pickers.Children.Add(to);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "将永久删除所选时间段内的记录，无法撤销。",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(pickers);
        body.Children.Add(live);
        RefreshLive();

        // 取消拿默认焦点（§6.5）；按钮文案是动词，范围与条数在正文里实时说。
        var answer = ContentDialog.Show(
            this,
            "按时间段删除",
            body,
            new ContentDialogButton("取消", ContentDialogButtonStyle.Standard, IsCancelFocus: true),
            new ContentDialogButton("删除该时间段", ContentDialogButtonStyle.Danger));

        if (answer != 1 || RangeCounts() is not { } counts2 || RangeFromPickers(from, to) is not ({ } start2, { } end2))
        {
            return;
        }

        DeleteOriginalsOf(_store.ImagesCreatedBetween(start2, end2, keepFavorites, keepPinned));
        SelfWrite(() => _ = _store.DeleteCreatedBetween(start2, end2, keepFavorites, keepPinned));
        Reload();
        RefreshTagChoices();
        ShowLightFeedback(counts2.Protected > 0
            ? $"已删除该时间段，保留 {counts2.Protected} 条受保护"
            : "已删除该时间段");
    }

    private static (DateTimeOffset? Start, DateTimeOffset? End) RangeFromPickers(DatePicker from, DatePicker to)
    {
        if (from.SelectedDate is not { } f || to.SelectedDate is not { } t)
        {
            return (null, null);
        }

        if (t < f)
        {
            (f, t) = (t, f);
        }

        // Whole days, inclusive: a user picking the same date twice means that
        // day, not the single instant at midnight.
        var now = DateTimeOffset.Now;
        return (
            new DateTimeOffset(f.Date, now.Offset),
            new DateTimeOffset(t.Date.AddDays(1).AddTicks(-1), now.Offset));
    }

    // --- 拖出 ------------------------------------------------------------------------------

    /// <summary>
    /// Starts a file drag for an image entry, so it can be dropped straight
    /// into Explorer or another application. The original is already a real
    /// file on disk — an ordinary file drag.
    /// </summary>
    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Primary is not { } item)
        {
            return;
        }

        if (!item.CanDrag)
        {
            return;
        }

        var files = new System.Collections.Specialized.StringCollection { item.OriginalPath! };
        var payload = new DataObject();
        payload.SetFileDropList(files);

        DragDrop.DoDragDrop(EntryList, payload, DragDropEffects.Copy);
    }

    // --- 行视图模型 --------------------------------------------------------------------------

}

/// <summary>
/// A row's read-only view of an entry（命名空间级：列表控件与窗口都要按它分派
/// 行高/分组）。GroupKey drives the date grouping; the rest is what the row
/// template binds.
/// </summary>
internal sealed record EntryItem(
    long Id,
    string Text,
    string? SourceApp,
    string Preview,
    string Meta,
    ImageSource? Thumbnail,
    ImageSource? Icon,
    string? OriginalPath,
    bool IsPinned,
    EntryKind Kind = EntryKind.Text,
    long? TranslatedFrom = null)
{
    public bool Favorite { get; init; }

    public EntrySubtype Subtype { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>置顶条目先于一切日期（页游标先排 pinned）。</summary>
    public string GroupKey { get; init; } = HistoryGroups.Earlier;

    public Visibility PinVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StarVisibility => Favorite ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ThumbnailVisibility =>
        Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>An application with no findable icon gets Shiyu's own mark, never a hole.</summary>
    public Visibility IconVisibility =>
        Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FallbackIconVisibility =>
        Icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>True while the full-size image is still on disk.</summary>
    public bool CanDrag => OriginalPath is { Length: > 0 } path && File.Exists(path);

    public static EntryItem From(Entry entry, EntryBlobs? payload, Func<string?, ImageSource?> iconOf)
    {
        var collapsed = string.Join(' ', entry.Text.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var preview = collapsed.Length > 300 ? collapsed[..300] + "…" : collapsed;
        var source = string.IsNullOrEmpty(entry.SourceApp) ? "未知来源" : entry.SourceApp;

        // 元信息行（§6.4）：相对时间 · 来源 · 字数；图片"宽×高"、文件"N 个"。
        var now = DateTimeOffset.UtcNow;
        var when = RelativeTime.For(entry.CreatedAt, now);
        var tail = entry.Kind switch
        {
            EntryKind.Image when entry.ImageWidth > 0 => $"{entry.ImageWidth}×{entry.ImageHeight}",
            EntryKind.Files => $"{entry.Files.Count} 个",
            _ => $"{entry.Text.Length} 字",
        };

        return new EntryItem(
            entry.Id,
            entry.Text,
            entry.SourceApp,
            preview,
            $"{when}  ·  {source}  ·  {tail}",
            AppIconCache.Decode(payload?.ThumbnailPng, pixelWidth: 240),
            iconOf(entry.SourceApp),
            entry.OriginalPath,
            entry.IsPinned,
            entry.Kind,
            entry.TranslatedFrom)
        {
            Favorite = entry.Favorite,
            Subtype = entry.Subtype,
            CreatedAt = entry.CreatedAt,
            GroupKey = entry.IsPinned
                ? HistoryGroups.Pinned
                : HistoryGroups.DateKeyOf(entry.CreatedAt, now),
        };
    }
}
