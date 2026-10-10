using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// The settings surface, rendered from the schema tree: five pages by user
/// intent plus About (§5.1), every editor built from its declaration. The
/// window hardcodes no setting — it knows control shapes, not settings.
/// Adding one is a schema leaf plus a value binding; this file does not
/// change.
///
/// 票 23：系统窗框 + Mica；左导航 240（窄于 760 收成图标条）；设置卡
/// 标签与说明同列、控件右对齐；即改即生效（去保存/关闭底栏，只有数据
/// 位置、导入备份、自备密钥保留显式操作）；错误显示在出错的卡片里。
/// </summary>
public partial class SettingsWindow : Window
{
    private const double NavWide = 240;
    private const double NavIcons = 48;
    private const double NavCollapseWidth = 760;

    private readonly SettingsStore _store;
    private readonly BackupUi? _backup;
    private readonly Action<Window>? _checkForUpdate;

    /// <summary>
    /// 「重新运行引导」要用它接试一试的信号；没有就退化为自查（票 25）。
    /// 构造后的内部注入——构造器是 public 的，而壳是 internal 的。
    /// </summary>
    internal AppShell? Shell { private get; set; }

    /// <summary>开窗（或上次跟随）时的设置基线：跟随与"改没改"的对照（O-20）。</summary>
    private AppSettings _baseline;

    private readonly Dictionary<string, ItemState> _edited = [];
    private readonly Dictionary<string, FrameworkElement> _rows = [];
    private readonly Dictionary<string, SettingsCardView> _cards = [];
    private readonly Dictionary<string, TextBox> _numberBoxes = [];
    private readonly Dictionary<string, StackPanel> _pageBodies = [];
    private readonly List<(string PageId, ToggleButton Button, TextBlock Label, TextBlock Glyph)> _nav = [];

    /// <summary>导航项右缘的 1–6 键帽，与 <see cref="_nav"/> 同序（含关于）——按住 Ctrl 浮出（§5.2 L1）。</summary>
    private readonly List<ContentControl> _navCaps = [];

    private bool _navHintsOn;

    // 外部变化的"跟随"要把新值推进控件，建行时把每种形状的控件记下来。
    private readonly Dictionary<string, TextBox> _textBoxes = [];
    private readonly Dictionary<string, CheckBox> _toggles = [];
    private readonly Dictionary<string, ToggleButton[]> _segmented = [];
    private readonly Dictionary<string, ComboBox> _choices = [];

    private TextBox? _directoryBox;
    private TextBlock? _directoryWarning;
    private PasswordBox? _secretBox;
    private CredentialEditor? _credential;
    private StackPanel? _savedProviders;
    private ServicePresetRow? _presetRow;
    private ExclusionRulesCard? _exclusions;
    private PromptTemplatesCard? _templates;
    private ActionsListCard? _actionsList;
    private TextBlock? _linkValue;

    private TextBox _searchBox = new();
    private int _searchCursor;
    private List<SettingsHit> _searchHits = [];
    private readonly List<Button> _resultRows = [];

    /// <summary>The page the user last had open, kept per session.</summary>
    private static string _lastPage = "general";

    public SettingsWindow(
        SettingsStore store,
        BackupUi? backup = null,
        Action<Window>? checkForUpdate = null)
    {
        InitializeComponent();

        _store = store;
        _baseline = store.Current;
        _backup = backup;
        _checkForUpdate = checkForUpdate;

        // 别处的写入（图钉、导入、引导完成）即时反映到本窗：没动过的
        // 编辑器跟随最新值，动过的保留用户手里的值（S1/S3/S4）。
        store.Changed += OnSettingsChanged;
        Closed += (_, _) => store.Changed -= OnSettingsChanged;

        // 票 19 spike 配方（ADR-0012 §7）：非分层窗口透出 DWM 材质的前提
        // 是重定向面底色透明。材质没被系统接受时（旧系统），回退成
        // Mica 官方回退色一档的不透明底。
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source
                && source.CompositionTarget is { } target)
            {
                target.BackgroundColor = Colors.Transparent;
            }
        };
        Backdrop.Attach(this, () => BackdropKind.Mica, applied =>
        {
            if (!applied)
            {
                ContentHost.SetResourceReference(BackgroundProperty, "Brush.Background");
                NavPane.SetResourceReference(BackgroundProperty, "Brush.Background");
            }
        });

        BuildNav();
        BuildPages();
        ApplyParentVisibility();
        HookPresetDemotion();

        // 打开窗时就可能已经对不上（比如导入的备份换了服务地址）：先判一次。
        _presetRow?.RefreshKeyHint();

        Loaded += (_, _) => ReflowNav();
        SizeChanged += (_, _) => ReflowNav();

        // 按住 Ctrl 时窗口失焦，KeyUp 不会再来：帽在这里收走，否则永远亮着。
        Deactivated += (_, _) => SetNavKeyHints(false);
        LostKeyboardFocus += (_, _) => SetNavKeyHints(false);

        SelectPage(SettingsSchema.ResolvePage(_lastPage)?.Id ?? "general");
    }

    // --- building: navigation ---------------------------------------------------

    private void BuildNav()
    {
        var pane = new DockPanel { LastChildFill = true };

        _searchBox = new TextBox { Height = 32 };
        _searchBox.SetResourceReference(StyleProperty, "SearchBox");
        InputProps.SetPlaceholder(_searchBox, "搜索设置");
        InputProps.SetRightGutter(_searchBox, 56);
        _searchBox.ToolTip = "搜索设置——支持你自己的说法，比如\u201c多久删\u201d";
        // 票 27 / U-29：搜索框有占位符而占位符不是值，屏幕阅读器要一个不变的名字。
        System.Windows.Automation.AutomationProperties.SetName(_searchBox, "搜索设置");
        _searchBox.TextChanged += OnSearchChanged;
        _searchBox.PreviewKeyDown += OnSearchKeyDown;

        // Ctrl+F 键帽浮在搜索框右缘（§6.3：搜索框带键帽）。
        var hint = new ContentControl
        {
            Content = "Ctrl F",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible = false,
        };
        hint.SetResourceReference(StyleProperty, "KeyCap");
        hint.Margin = new Thickness(0, 0, 8, 0);

        var searchWrap = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        searchWrap.Children.Add(_searchBox);
        searchWrap.Children.Add(hint);
        DockPanel.SetDock(searchWrap, Dock.Top);
        pane.Children.Add(searchWrap);

        var items = new StackPanel();
        foreach (var page in SettingsSchema.Tree.Where(page => page.Id != "about"))
        {
            items.Children.Add(NavItem(page));
        }

        var foot = new StackPanel();
        foot.Children.Add(NavItem(SettingsSchema.Tree.Single(p => p.Id == "about")));

        var list = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(foot, Dock.Bottom);
        list.Children.Add(foot);
        list.Children.Add(items);
        pane.Children.Add(list);

        NavPane.Child = pane;
    }

    private FrameworkElement NavItem(SettingsPage page)
    {
        var glyph = new TextBlock
        {
            Text = SettingsCardView.GlyphOf(page.Icon),
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextElement.FontFamilyProperty, "Font.Icon");
        glyph.SetResourceReference(TextElement.FontSizeProperty, "Size.IconS");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        glyph.Margin = new Thickness(12, 0, 12, 0);

        var label = new TextBlock
        {
            Text = page.Title,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");

        // 选中 = Selected 底 + 左缘 3×16 指示条 + 文字加粗（§6.3）。
        // token-ok: 指示条圆角 1.5 = 宽 3 的一半（胶囊按高度/宽度的半推导），
        // 不是 Radius.* 的任何一档；KindTab 下划线同款。
        var indicator = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        indicator.SetResourceReference(BackgroundProperty, "Brush.Accent");

        var row = new Grid();
        row.Children.Add(glyph);
        row.Children.Add(label);
        label.Margin = new Thickness(40, 0, 0, 0);

        // 键位即数据（§5.2 L1）：按住 Ctrl，导航项右缘浮出页号键帽——页序
        // 就是 Ctrl+1–6 的落点，帽与键同源同序。
        var cap = new ContentControl
        {
            Content = (_nav.Count + 1).ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        cap.SetResourceReference(StyleProperty, "KeyCap");
        row.Children.Add(cap);

        var button = new ToggleButton
        {
            Content = row,
            Height = 36,
            Cursor = Cursors.Hand,
            IsChecked = false,
            ToolTip = page.Title,
        };
        button.SetResourceReference(StyleProperty, "NavToggle");
        // 内容是面板派生不出名字；导航收窄成图标条时文字隐去，名字还在
        // （票 27 / U-29）——与 tooltip 同一个来源（page.Title），不另写。
        System.Windows.Automation.AutomationProperties.SetName(button, page.Title);

        var host = new Grid();
        host.Children.Add(button);
        host.Children.Add(indicator);
        indicator.IsHitTestVisible = false;

        _nav.Add((page.Id, button, label, glyph));
        _navCaps.Add(cap);
        button.Click += (_, _) => SelectPage(page.Id);
        return host;
    }

    // --- L1 按需教学（§5.2：按住 Ctrl，或按 F1 打开速查） ------------------------

    /// <summary>按住 Ctrl：导航项就地浮出 1–6；松开或失焦即收。窄导航条上不浮（盖不住图标）。</summary>
    private void SetNavKeyHints(bool on)
    {
        if (_navHintsOn == on)
        {
            return;
        }

        _navHintsOn = on;
        var roomForCaps = ActualWidth >= NavCollapseWidth;
        foreach (var cap in _navCaps)
        {
            cap.Visibility = on && roomForCaps ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        // Ctrl 的释放必须接住：失去焦点时收帽由 Deactivated 兜底，这里管
        // 正常的松手。
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            SetNavKeyHints(false);
        }
    }

    private void SelectPage(string pageId)
    {
        foreach (var (id, button, _, _) in _nav)
        {
            button.IsChecked = id == pageId;
        }

        UpdateNavSelectionVisuals();

        if (_pageBodies.TryGetValue(pageId, out var body))
        {
            PageScroller.Content = body;
        }

        _lastPage = pageId;
    }

    private void UpdateNavSelectionVisuals()
    {
        foreach (var (_, button, label, _) in _nav)
        {
            var selected = button.IsChecked == true;
            label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Regular;
            label.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Brush.Text" : "Brush.TextSecondary");

            if (button.Parent is Grid host
                && host.Children.Count > 1
                && host.Children[1] is Border indicator)
            {
                indicator.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>宽 &lt; 760 时导航收成 48 宽的图标条：藏文字、留图标与 tooltip。</summary>
    private void ReflowNav()
    {
        var collapsed = ActualWidth < NavCollapseWidth;
        NavColumn.Width = new GridLength(collapsed ? NavIcons : NavWide);

        foreach (var (_, button, label, glyph) in _nav)
        {
            label.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

            // 收成图标条时 12+12 的字形边距会把 16 宽的字形挤出只剩 24 宽
            // 的按钮（视觉走查 2026-10-03：整条图标一个都没画出来）：去边距、
            // 居中；展开时恢复原几何（字形墨左起 + 40 处文字）。
            glyph.Margin = collapsed ? new Thickness(0) : new Thickness(12, 0, 12, 0);
            glyph.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        }

        // 收起时搜索框跟着收（Ctrl+F 会先把导航展开再聚焦）。
        if (_searchBox.Parent is FrameworkElement wrap)
        {
            wrap.Visibility = collapsed && ActualWidth > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // --- building: pages ----------------------------------------------------------

    private void BuildPages()
    {
        foreach (var page in SettingsSchema.Tree)
        {
            var body = new StackPanel { Margin = new Thickness(32, 20, 32, 32) };

            var title = new TextBlock { Text = page.Title };
            title.SetResourceReference(TextElement.FontSizeProperty, "Type.Subtitle");
            title.FontWeight = FontWeights.SemiBold;
            title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            body.Children.Add(title);

            if (page.Description is { Length: > 0 } description)
            {
                var caption = new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                };
                caption.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
                caption.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
                caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                body.Children.Add(caption);
            }

            var firstSection = true;
            foreach (var section in page.Sections)
            {
                var heading = new TextBlock
                {
                    Text = section.Title,
                    Margin = new Thickness(0, firstSection ? 16 : 24, 0, 8),
                };
                heading.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
                heading.FontWeight = FontWeights.SemiBold;
                heading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                body.Children.Add(heading);
                firstSection = false;

                foreach (var item in section.Items.Where(item => item.Parent is null))
                {
                    body.Children.Add(RowFor(item, section));
                }
            }

            _pageBodies[page.Id] = body;
        }
    }

    /// <summary>
    /// 一项 → 一张卡；带子项的父项与子项折进同一张组卡（§6.3 Expander：
    /// 缩进即从属）。子项与父项都登记进 <see cref="_rows"/> 与
    /// <see cref="_cards"/>——深链、跟随与错误位都按 item Id 找。
    /// </summary>
    private FrameworkElement RowFor(SettingsItem item, SettingsSection section)
    {
        var children = section.Items.Where(child => child.Parent == item.Id).ToList();
        if (children.Count == 0)
        {
            var card = SettingsCardView.Create(item, EditorFor(item));
            _rows[item.Id] = card.Root;
            _cards[item.Id] = card;
            return card.Root;
        }

        var parent = SettingsCardView.Create(item, EditorFor(item));
        parent.Margin = new Thickness(0);
        _rows[item.Id] = parent.Root;
        _cards[item.Id] = parent;

        var rows = new List<FrameworkElement> { parent.Root };
        foreach (var child in children)
        {
            var sub = SettingsCardView.CreateSub(child.Label, EditorFor(child));
            _rows[child.Id] = sub.Root;
            _cards[child.Id] = sub;
            rows.Add(sub.Root);
        }

        return SettingsGroup.Card(rows.ToArray());
    }

    // --- editors ------------------------------------------------------------------

    private FrameworkElement EditorFor(SettingsItem item)
    {
        var state = new ItemState();
        _edited[item.Id] = state;

        return item.Control switch
        {
            SettingsControl.Segmented => SegmentedFor(item, state),
            SettingsControl.Toggle => ToggleFor(item, state),
            SettingsControl.Number => NumberFor(item, state),
            SettingsControl.Password => SecretFor(item, state),
            SettingsControl.Directory => DirectoryFor(item, state),
            SettingsControl.Choice => ChoiceFor(item, state),
            SettingsControl.Hotkey => HotkeyRecorder(item, state),
            SettingsControl.Text => TextFor(item, state),
            SettingsControl.ReadOnly => ReadOnlyFor(item),
            SettingsControl.Link => LinkFor(item),
            SettingsControl.Custom => CustomFor(item, state),
            SettingsControl.Actions => CustomFor(item, state),
            _ => new TextBlock(),
        };
    }

    private FrameworkElement ChoiceFor(SettingsItem item, ItemState state)
    {
        var editor = ItemEditors.Choice(
            item, _baseline, state,
            toDisplay: LanguageOptions.ToDisplay,
            toValue: LanguageOptions.ToValue,
            changed: () => Commit(item, state));

        if (editor is ComboBox picker)
        {
            _choices[item.Id] = picker;
        }

        return editor;
    }

    private FrameworkElement SegmentedFor(SettingsItem item, ItemState state)
    {
        var editor = ItemEditors.Segmented(
            item, _baseline, state,
            changed: _ =>
            {
                ApplyParentVisibility();
                Commit(item, state);
            },

            // 公共通道未上线（票 08/ADR-0009）：看得见、点不动。
            choiceEnabled: item.Id == "service.backend-kind"
                ? choice => choice != (int)TranslationBackendKind.Relay
                    || RelayChannel.Available
                : null);

        _segmented[item.Id] = editor is StackPanel panel
            ? panel.Children.OfType<ToggleButton>().ToArray()
            : [];
        return editor;
    }

    private FrameworkElement ToggleFor(SettingsItem item, ItemState state)
    {
        var editor = ItemEditors.Toggle(
            item, _baseline, state,
            changed: () =>
            {
                ApplyParentVisibility();

                // 开机自启的事实在 Windows 而不在文件：写完文件再把注册表
                // 对上，箱子显示 Windows 说了算的那一档。
                if (item.Id == "store.start-with-windows")
                {
                    CommitStartup(state);
                    return;
                }

                Commit(item, state);
            },

            // 设置卡的说明列由卡片自己渲染（§6.3）：编辑器里再带一份就重复。
            withHint: false);

        if (editor is CheckBox box)
        {
            _toggles[item.Id] = box;
        }

        return editor;
    }

    private FrameworkElement NumberFor(SettingsItem item, ItemState state)
    {
        var box = new NumberBox
        {
            Text = SettingsBindings.ReadText(item.Id, _baseline) ?? string.Empty,
            Width = 110,
            Unit = item.Unit ?? string.Empty,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        state.Text = box.Text;
        _textBoxes[item.Id] = box;
        _numberBoxes[item.Id] = box;

        box.Committed += (_, _) =>
        {
            if (!int.TryParse(box.Text.Trim(), out var value)
                || value < item.Min
                || value > item.Max)
            {
                CardOf(item)?.ShowError(
                    $"{item.Label}需要是 {(int)item.Min} 到 {(int)item.Max} 之间的整数。");
                return;
            }

            CardOf(item)?.ClearError();
            state.Text = value.ToString();
            Commit(item, state);
        };

        return box;
    }

    private FrameworkElement TextFor(SettingsItem item, ItemState state)
    {
        var editor = ItemEditors.Text(item, _baseline, state, withHint: false);
        var box = (TextBox)editor;
        box.MinWidth = 240;
        _textBoxes[item.Id] = box;

        void CommitText()
        {
            var trimmed = box.Text.Trim();

            // 译文语言不能为空：清空不是"没有语言"，是还没选。
            if (item.Id == "service.target-language" && trimmed.Length == 0)
            {
                CardOf(item)?.ShowError("译文语言不能为空——从下拉里选一个。");
                return;
            }

            CardOf(item)?.ClearError();
            state.Text = box.Text;
            Commit(item, state);
        }

        box.LostFocus += (_, _) => CommitText();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitText();
                e.Handled = true;
            }
        };

        return box;
    }

    /// <summary>
    /// 键帽录制器（§5.1 快捷键页）：点一下，按下组合键，即录即校验——
    /// 与其余四键（含未在此编辑的那个）的撞车在录制时就点名，而不是等
    /// 注册失败后托盘里冒一句。Esc 取消；「清除」把键位拿掉（打开管理窗
    /// 默认就是不设）。
    /// </summary>
    private FrameworkElement HotkeyRecorder(SettingsItem item, ItemState state)
    {
        var box = new TextBox
        {
            Text = SettingsBindings.ReadText(item.Id, _baseline) ?? string.Empty,
            Width = 150,
            IsReadOnly = true,
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        InputProps.SetPlaceholder(box, "未设置");
        box.SetResourceReference(BackgroundProperty, "Brush.SurfaceInput");
        // 只读的录制框：名字说出它是干什么的（票 27 / U-29），值（组合键
        // 文本）仍由 UIA 的 Value 通道自己念。
        System.Windows.Automation.AutomationProperties.SetName(box, item.Label + "快捷键");
        state.Text = box.Text;
        _textBoxes[item.Id] = box;

        var clear = new Button
        {
            Content = "清除",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "拿掉这个快捷键（不注册）",
        };
        clear.SetResourceReference(StyleProperty, "FlyoutButton");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(clear);

        box.PreviewMouseDown += (_, e) =>
        {
            e.Handled = true;
            CaptureNextCombination(item, state, box);
        };

        clear.Click += (_, _) =>
        {
            state.Text = string.Empty;
            box.Text = string.Empty;
            ApplyParentVisibility();
            Commit(item, state);
        };

        return row;
    }

    private void CaptureNextCombination(SettingsItem item, ItemState state, TextBox box)
    {
        var listening = true;
        box.SetResourceReference(BorderBrushProperty, "Brush.Accent");
        InputProps.SetPlaceholder(box, "按下组合键…");

        // 键盘钩子在窗口层面接一次：焦点不用真给输入框（它是只读的），
        // 组合键直接落在窗口消息上。
        PreviewKeyDown += OnKey;

        void OnKey(object sender, KeyEventArgs e)
        {
            if (!listening)
            {
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System
                or Key.Tab)
            {
                e.Handled = true;
                return;
            }

            listening = false;
            PreviewKeyDown -= OnKey;
            box.SetResourceReference(BorderBrushProperty, "Brush.Border");
            InputProps.SetPlaceholder(box, "未设置");
            e.Handled = true;

            if (key == Key.Escape)
            {
                return;
            }

            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None)
            {
                CardOf(item)?.ShowError("全局快捷键至少要带一个修饰键（Ctrl/Shift/Alt/Win）。");
                return;
            }

            char? letter = key switch
            {
                >= Key.A and <= Key.Z => (char)('A' + (key - Key.A)),
                >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
                >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
                _ => null,
            };

            if (letter is not { } digit)
            {
                CardOf(item)?.ShowError("只支持字母/数字键加修饰键（F 键与标点注册不了）。");
                return;
            }

            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(digit.ToString());
            var combination = string.Join("+", parts);

            // 即时校验：候选方案里有任何问题（多半是撞车）就点名不落盘。
            var candidate = SettingsBindings.Apply(item.Id, _store.Current, combination, 0);
            var problems = HotkeyPlan.Build(candidate).Problems;
            if (problems.Count > 0)
            {
                CardOf(item)?.ShowError(string.Join(" ", problems));
                return;
            }

            CardOf(item)?.ClearError();
            state.Text = combination;
            box.Text = combination;
            ApplyParentVisibility();
            Commit(item, state);
        }
    }

    private FrameworkElement SecretFor(SettingsItem item, ItemState state)
    {
        var (_, box) = ItemEditors.Password(item, _baseline, state, withHint: false);
        _secretBox = box;

        // 用户需求 2026-10-10：每家各存一份，打码显示、点开才看全。凭据卡自己按表单上的地址
        // 决定是打码的那一行，还是输入框。
        _credential = new CredentialEditor(
            box,
            formBaseUrl: FormBaseUrl,
            settings: () => _store.Current,
            save: () => SaveCredential(item, state, box),
            remove: AskRemoveProvider,
            copy: key => Shell?.Writer?.SetSecret(key) == true);

        // 这家还没有凭据、别家有时，凭据框下方说清楚（票 29）。话与「申请密钥」链接由预设行产出
        // ——它知道表单上是哪一家；这里只给它一个落脚处，并在框里的内容变化时叫它重判（正在填就
        // 不再提示）。
        if (_presetRow is null)
        {
            return _credential.Element;
        }

        box.PasswordChanged += (_, _) => _presetRow?.RefreshKeyHint();
        var stack = new StackPanel();
        stack.Children.Add(_credential.Element);
        stack.Children.Add(_presetRow.KeyHint);
        return stack;
    }

    /// <summary>表单上此刻的服务地址（可能还没落盘）；没有地址框时取已存的。</summary>
    private string FormBaseUrl()
        => _textBoxes.TryGetValue("service.base-url", out var box) ? box.Text : _store.Current.BackendBaseUrl;

    /// <summary>
    /// 「保存凭据」：自备密钥保留显式操作（ADR-0012 §15），凭据不是改了就发的开关，点了才离手。
    /// 记在表单上这家的名下，别家的一份不碰。存成了返回 true。
    /// </summary>
    private bool SaveCredential(SettingsItem item, ItemState state, PasswordBox box)
    {
        if (state.Text.Length == 0)
        {
            CardOf(item)?.ShowError("还没有输入任何凭据。");
            return false;
        }

        if (KeyOrigin.Of(FormBaseUrl()).Length == 0)
        {
            CardOf(item)?.ShowError("先填好服务地址，再保存凭据——凭据要记在这家服务商名下。");
            return false;
        }

        CardOf(item)?.ClearError();
        var typed = state.Text;
        Commit(item, state);
        if (_store.Current.KeyFor(FormBaseUrl()) != typed)
        {
            // Commit 已把失败的原因写在卡上。
            return false;
        }

        box.Clear();
        state.Text = string.Empty;
        return true;
    }

    /// <summary>删除一家已存的服务商：凭据删了就得重新填，先问一句（§6.5，取消拿默认焦点）。</summary>
    private void AskRemoveProvider(SavedProvider provider)
    {
        var answer = ContentDialog.Show(
            this,
            "删除凭据",
            $"删除「{provider.DisplayName}」保存的凭据？以后要用这家，得重新填写。",
            new ContentDialogButton("取消", ContentDialogButtonStyle.Standard, IsCancelFocus: true),
            new ContentDialogButton("删除", ContentDialogButtonStyle.Danger));
        if (answer != 1)
        {
            return;
        }

        try
        {
            _store.Update(latest => latest.WithoutProvider(provider.Origin), AppPaths.SettingsFile);
        }
        catch (SettingsSaveException failure)
        {
            CardOf(SettingsSchema.Find("service.saved-providers")!)?.ShowError(failure.Message + " 可以重试。");
        }
    }

    // --- 已保存的服务商（用户需求 2026-10-10：配过的一目了然，点一下就切过去） ----------------

    private FrameworkElement SavedProvidersRow()
    {
        _savedProviders = new StackPanel();
        RefreshSavedProviders(_baseline);
        return _savedProviders;
    }

    /// <summary>
    /// 每家一行：名字、模型与打码的凭据；正在用的那家标「当前使用」，别家给「切换」；都能删。
    /// 跟着设置走——存了、删了、切了，这里随之重画。
    /// </summary>
    private void RefreshSavedProviders(AppSettings settings)
    {
        if (_savedProviders is null)
        {
            return;
        }

        _savedProviders.Children.Clear();
        if (settings.SavedProviders.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "还没有保存过凭据。在上面选好服务商、填好凭据并保存后，会出现在这里。",
                TextWrapping = TextWrapping.Wrap,
            };
            empty.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            _savedProviders.Children.Add(empty);
            return;
        }

        foreach (var provider in settings.SavedProviders)
        {
            _savedProviders.Children.Add(SavedProviderLine(
                provider, current: KeyOrigin.Same(settings.BackendBaseUrl, provider.Origin)));
        }
    }

    private FrameworkElement SavedProviderLine(SavedProvider provider, bool current)
    {
        var line = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var marker = new TextBlock
        {
            Text = current ? "●" : "○",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        marker.SetResourceReference(TextBlock.ForegroundProperty, current ? "Brush.Accent" : "Brush.TextTertiary");
        line.Children.Add(marker);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = provider.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = new TextBlock
        {
            Text = provider.Model.Length > 0
                ? $"{provider.Model}  ·  {CredentialMask.Of(provider.ApiKey)}"
                : CredentialMask.Of(provider.ApiKey),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        detail.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        words.Children.Add(detail);
        Grid.SetColumn(words, 1);
        line.Children.Add(words);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (current)
        {
            var using_ = new TextBlock
            {
                Text = "当前使用",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            using_.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
            using_.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
            actions.Children.Add(using_);
        }
        else
        {
            var switchTo = new Button
            {
                Content = "切换",
                Padding = new Thickness(10, 3, 10, 3),
                Cursor = Cursors.Hand,
                ToolTip = $"改用 {provider.DisplayName}：地址、模型和凭据一起切过去",
            };
            switchTo.Click += (_, _) => SwitchToProvider(provider);
            actions.Children.Add(switchTo);
        }

        var remove = new Button
        {
            Content = "删除",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        remove.Click += (_, _) => AskRemoveProvider(provider);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 2);
        line.Children.Add(actions);

        System.Windows.Automation.AutomationProperties.SetName(
            line, current ? $"{provider.DisplayName}，当前使用" : provider.DisplayName);
        return line;
    }

    /// <summary>切到一家已存的服务商：地址、模型、预设一次落盘，表单与预设下拉随设置变化跟上。</summary>
    private void SwitchToProvider(SavedProvider provider)
    {
        try
        {
            _store.Update(latest => latest.SwitchedTo(provider), AppPaths.SettingsFile);
        }
        catch (SettingsSaveException failure)
        {
            CardOf(SettingsSchema.Find("service.saved-providers")!)?.ShowError(failure.Message + " 可以重试。");
        }
    }

    private FrameworkElement DirectoryFor(SettingsItem item, ItemState state)
    {
        var box = new TextBox
        {
            Text = SettingsBindings.ReadText(item.Id, _baseline) ?? string.Empty,
            MinWidth = 200,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.SetResourceReference(BackgroundProperty, "Brush.SurfaceInput");
        state.Text = box.Text;
        _textBoxes[item.Id] = box;
        box.TextChanged += (_, _) =>
        {
            state.Text = box.Text;
            UpdateSyncWarning();
        };

        var browse = new Button
        {
            Content = "浏览…",
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择拾语存放数据的位置",
                InitialDirectory = Directory.Exists(box.Text) ? box.Text : AppPaths.DataDirectory,
            };
            if (dialog.ShowDialog(this) == true)
            {
                box.Text = dialog.FolderName;
            }
        };

        // 数据位置保留显式操作（ADR-0012 §15）：这是一次搬迁，不是一次
        // 输入——点「应用」才走确认与落盘。
        var apply = new Button
        {
            Content = "应用",
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        apply.Click += (_, _) => ApplyDirectory(item, state, box);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(browse);
        row.Children.Add(apply);

        _directoryBox = box;
        _directoryWarning = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = (double)FindResource("Type.Caption"),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Visibility = Visibility.Collapsed,
        };
        _directoryWarning.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
        _directoryWarning.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(_directoryWarning);
        UpdateSyncWarning();

        return stack;
    }

    /// <summary>应用数据位置：云同步目录先过 ContentDialog（§6.5 可逆但影响大）。</summary>
    private void ApplyDirectory(SettingsItem item, ItemState state, TextBox box)
    {
        var directory = box.Text.Trim();
        if (CloudSyncedPaths.DetectSyncFolder(directory) is { } synced)
        {
            var answer = ContentDialog.Show(
                this,
                "更改数据位置",
                $"「{synced}」会被同步到云端，而历史记录并未加密——放在这里等于把明文的剪贴板内容交给同步服务。",
                new ContentDialogButton("取消", ContentDialogButtonStyle.Standard, IsCancelFocus: true),
                new ContentDialogButton("仍然放在这里", ContentDialogButtonStyle.Accent));

            if (answer != 1)
            {
                return;
            }
        }

        state.Text = directory;
        Commit(item, state);
        UpdateSyncWarning();
    }

    private FrameworkElement ReadOnlyFor(SettingsItem item)
        => new TextBlock
        {
            Text = SettingsBindings.ReadText(item.Id, _baseline) ?? string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>引用卡（§5.1）：只读 + 跳转——同一设置只有一个编辑处。</summary>
    private FrameworkElement LinkFor(SettingsItem item)
    {
        var value = new TextBlock
        {
            Text = LinkValueText(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _linkValue = value;

        var jump = new Button
        {
            Content = "去快捷键 ›",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        jump.SetResourceReference(StyleProperty, "FlyoutButton");
        jump.Click += (_, _) => JumpToItem("hotkey.capture");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(value);
        row.Children.Add(jump);
        return row;
    }

    private string LinkValueText()
        => SettingsBindings.ReadText("hotkey.capture", _store.Current) is { Length: > 0 } key
            ? key
            : "未设置";

    private FrameworkElement CustomFor(SettingsItem item, ItemState state) => item.Id switch
    {
        "store.backup" => BackupRow(),
        "store.usage" => StorageUsagePanel(),
        "about.onboarding" => OnboardingRow(),
        "about.brand" => BrandHeader(),
        "about.update-check" => UpdateCheckRow(),
        "about.logs" => LogsRow(),
        "about.privacy-note" => PrivacyNote(),
        "hotkeys.reset" => ResetHotkeysRow(),
        "hotkeys.cheatsheet" => KeyMapCard(),
        "service.preset" => PresetRow(state),
        "service.saved-providers" => SavedProvidersRow(),
        "exclusions" => ExclusionsRow(item, state),
        "translate.templates" => TemplatesRow(item),
        "translate.log-clear" => TranslationLogRow(),
        "bar.actions" => ActionsListRow(item, state),
        _ => new TextBlock(),
    };

    // --- 翻译记录（用户需求 2026-10-09） ---------------------------------------------

    /// <summary>
    /// 「立即清空」一行：现有几条、去管理窗口看、一键清空。清空不可撤销，走危险确认（§6.5，
    /// 取消拿默认焦点）；剪贴板历史一条不碰。条数跟着库变（两个框在用时也会涨）。
    /// </summary>
    private FrameworkElement TranslationLogRow()
    {
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        count.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        var open = new Button
        {
            Content = "查看翻译记录",
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        open.Click += (_, _) => Shell?.ShowTranslationLog?.Invoke();

        var clear = new Button
        {
            Content = "立即清空",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
        };

        void Refresh()
        {
            var total = Shell?.Store.CountTranslationLog() ?? 0;
            count.Text = total > 0 ? $"共 {total} 条" : "还没有记录";
            clear.IsEnabled = total > 0;
        }

        void OnLogChanged()
        {
            if (Dispatcher.CheckAccess())
            {
                Refresh();
            }
            else
            {
                Dispatcher.BeginInvoke(Refresh);
            }
        }

        clear.Click += (_, _) =>
        {
            if (Shell?.Store is not { } store)
            {
                return;
            }

            try
            {
                TranslationLogDialogs.AskClear(this, store);
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.TranslationLogFailed, failure, ("clear", 1));
                CardOf(SettingsSchema.Find("translate.log-clear")!)?.ShowError("没能清空翻译记录，可以重试。");
            }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(count);
        row.Children.Add(open);
        row.Children.Add(clear);

        // 页面在构造里就建好了，那时 Shell 还没接上（对象初始化器在构造之后）：上屏时才去读、去订，
        // 下屏时退订——翻页来回，订阅也一来一回（Loaded 可能连来两次，只订一份）。
        EntryStore? watched = null;
        row.Loaded += (_, _) =>
        {
            if (watched is null && Shell?.Store is { } store)
            {
                watched = store;
                store.TranslationLogChanged += OnLogChanged;
            }

            Refresh();
        };
        row.Unloaded += (_, _) =>
        {
            if (watched is not null)
            {
                watched.TranslationLogChanged -= OnLogChanged;
                watched = null;
            }
        };

        return row;
    }

    // --- 键位速查（§5.2 键位即数据） ---------------------------------------------

    /// <summary>
    /// 窗口内按键速查区：整张卡从 <see cref="KeyMap"/> 渲染——这里不出现任何
    /// 手写按键，改 Core 那张表，这卡、窄条键帽、托盘菜单、引导同步变。
    /// 托盘菜单的「键位速查…」深链落在这。
    /// </summary>
    private FrameworkElement KeyMapCard()
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
            cap.SetResourceReference(StyleProperty, "KeyCap");
            cap.Margin = new Thickness(0, 0, 2, 0);
            host.Children.Add(cap);
        }

        // 与下一个窗口段留出呼吸。
        host.Children.Add(new Border { Width = 8 });
    }

    private FrameworkElement ExclusionsRow(SettingsItem item, ItemState state)
    {
        _exclusions = new ExclusionRulesCard(
            _baseline,
            text =>
            {
                state.Text = text;
                Commit(item, state);
            });
        return _exclusions.Element;
    }

    /// <summary>
    /// 提示词模板（票 42）：每个开关单字段即改即生效。写走 SettingsStore——这是在设置
    /// 窗里写，属于常规路径，不涉及面板 Esc 那个问题（面板里点模板按钮才不写设置）。
    /// </summary>
    private FrameworkElement TemplatesRow(SettingsItem item)
    {
        _templates = new PromptTemplatesCard(
            _baseline,
            () => _store.Current,
            change =>
            {
                try
                {
                    _store.Update(change, AppPaths.SettingsFile);
                    CardOf(item)?.ClearError();
                    return true;
                }
                catch (SettingsSaveException failure)
                {
                    CardOf(item)?.ShowError(failure.Message + " 本次改动没有生效，可以重试。");
                    return false;
                }
            });
        return _templates.Element;
    }

    private FrameworkElement ActionsListRow(SettingsItem item, ItemState state)
    {
        _actionsList = new ActionsListCard(
            _baseline,
            text =>
            {
                state.Text = text;
                Commit(item, state);
            });
        return _actionsList.Element;
    }

    /// <summary>
    /// 服务商预设行（票 08）：与引导共用 <see cref="ServicePresetRow"/>。选中即
    /// 代填地址与模型并一并落盘（预设三字段是一件事）；手改地址/模型时
    /// 预设立即降级为「自定义」。
    /// </summary>
    private FrameworkElement PresetRow(ItemState state)
    {
        var presetState = state;
        _presetRow = new ServicePresetRow(
            _baseline,
            presetState,
            readForm: () =>
            {
                var url = _textBoxes.TryGetValue("service.base-url", out var urlBox)
                    ? urlBox.Text
                    : string.Empty;
                var model = _textBoxes.TryGetValue("service.model", out var modelBox)
                    ? modelBox.Text
                    : string.Empty;

                // 与保存语义一致：凭据留空 = 保留已存的那个——但只保留属于这个
                // 地址的那个（票 29）：ServiceForm 按来源取，别家的旧密钥不会
                // 随"测试连接"发往新地址。
                return new ServiceForm(
                    _baseline, url, model, _secretBox?.Password ?? string.Empty);
            },
            applyPreset: preset =>
            {
                // 选中预设 = 预设、地址、模型三字段一次落盘（即改即生效的
                // 一次提交），编辑框同步显示。存过这家（用户需求 2026-10-10）
                // 就回到上次用的地址与模型，凭据随来源自然找到。
                var switched = _store.Current.SwitchedToPreset(preset);
                if (_textBoxes.TryGetValue("service.base-url", out var urlBox))
                {
                    urlBox.Text = switched.BackendBaseUrl;
                }

                if (_textBoxes.TryGetValue("service.model", out var modelBox))
                {
                    modelBox.Text = switched.BackendModel;
                }

                presetState.Text = preset.Id;
                _edited["service.base-url"].Text = switched.BackendBaseUrl;
                _edited["service.model"].Text = switched.BackendModel;

                try
                {
                    _store.Update(latest => latest.SwitchedToPreset(preset), AppPaths.SettingsFile);
                }
                catch (SettingsSaveException failure)
                {
                    CardOf(SettingsSchema.Find("service.preset")!)
                        ?.ShowError(failure.Message + " 可以重试。");
                }
            });

        return _presetRow.Element;
    }

    /// <summary>地址或模型被手改时，预设行立即降级为「自定义」。</summary>
    private void HookPresetDemotion()
    {
        if (_presetRow is null)
        {
            return;
        }

        foreach (var id in new[] { "service.base-url", "service.model" })
        {
            if (_textBoxes.TryGetValue(id, out var box))
            {
                box.TextChanged += (_, _) => _presetRow?.NoteAddressEdited();
            }
        }

        // 地址一变，凭据卡要看的是另一家：存过就打码显示，没存过就是输入框。
        if (_textBoxes.TryGetValue("service.base-url", out var urlBox))
        {
            urlBox.TextChanged += (_, _) => _credential?.Refresh();
        }
    }

    // --- the About page pieces ------------------------------------------------

    /// <summary>
    /// 品牌头（§5.1 关于页）：卡标签即名称、说明即一句话，控件列放标识
    /// 与「复制版本信息」——不多占一行去重复一遍"拾语"。
    /// </summary>
    private FrameworkElement BrandHeader()
    {
        // 与任务栏图标同一个标志（大号母版，矢量）。
        var mark = new Image { Width = 40, Height = 40 };
        mark.SetResourceReference(Image.SourceProperty, "Brand.Mark.Large");

        var copy = new Button
        {
            Content = "复制版本信息",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "版本号与运行环境，不含任何个人数据",
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(
                    $"拾语 {SettingsBindings.VersionText} / {Environment.OSVersion.VersionString}");
                ((App)Application.Current).TellUser("版本信息已复制。");
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.OpenLinkFailed, failure, ("clipboard", 1));
            }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(mark);
        row.Children.Add(copy);
        return row;
    }

    private FrameworkElement UpdateCheckRow()
    {
        var check = new Button
        {
            Content = "检查更新",
            Padding = new Thickness(12, 3, 12, 3),
            Cursor = Cursors.Hand,
        };
        check.Click += (_, _) => _checkForUpdate?.Invoke(this);
        return check;
    }

    private FrameworkElement LogsRow()
    {
        var open = new Button
        {
            Content = "打开日志目录",
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        open.Click += (_, _) =>
        {
            try
            {
                using var opened = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Path.Combine(AppPaths.DataDirectory, "logs"))
                    {
                        UseShellExecute = true,
                    });
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.OpenLinkFailed, failure, ("folder", 2));
                ((App)Application.Current).TellUser("没能打开日志目录。");
            }
        };
        return open;
    }

    private FrameworkElement OnboardingRow()
    {
        var run = new Button
        {
            Content = "重新运行引导",
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        run.Click += (_, _) =>
        {
            // 重跑引导（§5.3）：不是首启——译文语言尊重已存值，试一试的
            // 实时检测经壳接上（shell 为空时引导自己说明退化）。
            var wizard = new OnboardingWindow(_store, firstRun: false, Shell)
            {
                Owner = this,
            };
            wizard.ShowDialog();
        };
        return run;
    }

    private FrameworkElement ResetHotkeysRow()
    {
        var reset = new Button
        {
            Content = "恢复全部默认",
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        reset.Click += (_, _) =>
        {
            try
            {
                var latest = _store.Update(
                    current => current with
                    {
                        CaptureHotkey = "Ctrl+Shift+Z",
                        ClipboardTranslateHotkey = "Ctrl+Shift+X",
                        QuickBarHotkey = "Ctrl+Shift+V",
                        LibraryHotkey = string.Empty,
                        ReverseInputHotkey = new AppSettings().ReverseInputHotkey,
                    },
                    AppPaths.SettingsFile);
                _baseline = latest;
            }
            catch (SettingsSaveException failure)
            {
                CardOf(SettingsSchema.Find("hotkeys.reset")!)
                    ?.ShowError(failure.Message + " 可以重试。");
            }
        };
        return reset;
    }

    /// <summary>隐私说明的正文就是卡片的说明列（schema Hint）；控件列为空。</summary>
    private FrameworkElement PrivacyNote()
        => new TextBlock();

    /// <summary>
    /// What Shiyu actually takes from the disk, measured where it lies —
    /// the number a user asking "这个小工具吃了我多少" is owed, with the
    /// folder one click away and a plain word when it grows past reason.
    /// </summary>
    private FrameworkElement StorageUsagePanel()
    {
        var panel = new StackPanel();

        TextBlock Row(string name, long bytes)
        {
            var row = new TextBlock
            {
                Text = $"{name}  {FormatBytes(bytes)}",
                Margin = new Thickness(0, 2, 0, 2),
            };
            row.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
            row.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            return row;
        }

        void Measure()
        {
            panel.Children.Clear();

            var database = SizeOfFile(AppPaths.DatabaseFile)
                + SizeOfFile(AppPaths.DatabaseFile + "-wal")
                + SizeOfFile(AppPaths.DatabaseFile + "-shm");
            var images = SizeOfDirectory(AppPaths.ImageDirectory);

            panel.Children.Add(Row("数据库", database));
            panel.Children.Add(Row("图片原图", images));
            panel.Children.Add(Row("合计", database + images));

            const long threshold = 500L * 1024 * 1024;
            if (database + images > threshold)
            {
                var warning = new TextBlock
                {
                    Text = "已超过 500 MB——考虑缩短图片保留天数，或导出备份后清空。",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                };
                warning.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
                warning.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
                warning.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
                panel.Children.Add(warning);
            }
        }

        Measure();

        var open = new Button
        {
            Content = "打开数据文件夹",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand,
        };
        open.Click += (_, _) =>
        {
            try
            {
                using var opened = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(AppPaths.DataDirectory)
                    {
                        UseShellExecute = true,
                    });
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.OpenLinkFailed, failure, ("folder", 1));
                ((App)Application.Current).TellUser("没能打开数据文件夹。");
            }
        };

        var refresh = new Button
        {
            Content = "刷新",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 6, 0, 0),
            Cursor = Cursors.Hand,
        };
        refresh.Click += (_, _) => Measure();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(open);
        row.Children.Add(refresh);
        panel.Children.Add(row);

        return panel;
    }

    private static long SizeOfFile(string path)
        => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long SizeOfDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
            : $"{bytes / 1024.0:0.#} KB";

    private FrameworkElement BackupRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        var export = new Button { Content = "导出备份…", MinWidth = 96, Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand };
        export.Click += (_, _) => _backup?.Export(this);

        var import = new Button { Content = "导入备份…", MinWidth = 96, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
        import.Click += (_, _) => _backup?.Import(this);

        row.Children.Add(export);
        row.Children.Add(import);
        return row;
    }

    private void UpdateSyncWarning()
    {
        if (_directoryBox is null || _directoryWarning is null)
        {
            return;
        }

        var folder = CloudSyncedPaths.DetectSyncFolder(_directoryBox.Text);
        _directoryWarning.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        _directoryWarning.Text = folder is null
            ? string.Empty
            : $"⚠ 这个位置在「{folder}」里，会被同步到云端。历史记录并未加密，"
              + "放在这里等于把明文的剪贴板内容交给同步服务。";
    }

    // --- instant apply (ADR-0012 §15) ---------------------------------------------

    private SettingsCardView? CardOf(SettingsItem item)
        => _cards.TryGetValue(item.Id, out var card) ? card : null;

    /// <summary>
    /// 单字段即改即生效：在最新设置上只应用这一项（O-20 的增量写），失败
    /// 把话说在出错的卡片里——没有底栏可推诿了。
    /// </summary>
    private void Commit(SettingsItem item, ItemState state)
    {
        try
        {
            _store.Update(
                latest => SettingsBindings.Apply(item.Id, latest, state.Text, state.Choice),
                AppPaths.SettingsFile);
        }
        catch (SettingsSaveException failure)
        {
            CardOf(item)?.ShowError(failure.Message + " 本次改动没有生效，可以重试。");
        }
    }

    private void CommitStartup(ItemState state)
    {
        var wanted = state.Toggle;
        var ok = StartupRegistration.Set(wanted, Environment.ProcessPath ?? string.Empty);
        var actual = StartupRegistration.IsEnabled();

        // Windows 的现状是唯一事实：文件与开关都对齐它；注册失败要说一句。
        state.Toggle = actual;
        state.Text = actual ? "1" : "0";
        if (_toggles.TryGetValue("store.start-with-windows", out var box) && box.IsChecked != actual)
        {
            box.IsChecked = actual;
        }

        if (_store.Current.StartWithWindows != actual)
        {
            Commit(SettingsSchema.Find("store.start-with-windows")!, state);
        }

        if (!ok)
        {
            CardOf(SettingsSchema.Find("store.start-with-windows")!)
                ?.ShowError("开机自启没能写入系统，请检查是否有安全软件拦截。");
        }
    }

    /// <summary>
    /// 父项的"开着"：开关看开关；热键看有没有键；分段看 <see cref="SegmentedParentOn"/>
    /// （翻译方式只有自备密钥才有下面四行）。子项随它露面或收起。
    /// </summary>
    private void ApplyParentVisibility()
    {
        var byId = AllItems().ToDictionary(item => item.Id);
        foreach (var child in AllItems().Where(item => item.Parent is not null))
        {
            if (!byId.TryGetValue(child.Parent!, out var parent)
                || !_edited.TryGetValue(parent.Id, out var parentState)
                || !_rows.TryGetValue(child.Id, out var row))
            {
                continue;
            }

            var on = parent.Control switch
            {
                SettingsControl.Toggle => parentState.Toggle,
                SettingsControl.Hotkey => parentState.Text.Trim().Length > 0,
                SettingsControl.Segmented => SegmentedParentOn(parent.Id, parentState.Choice),
                _ => true,
            };
            row.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 分段父项的"开着"。翻译方式只有选了自备密钥，下面预设/地址/模型/凭据四行才有
    /// 意义——公共通道（未上线）与免费引擎（零配置，票 41）都用不着它们，选中时收起，
    /// 免得"免费引擎"底下摆着一排要填的东西。其它分段父项沿用旧规则：不在第一位就开着。
    /// </summary>
    private static bool SegmentedParentOn(string parentId, int choice)
        => parentId == "service.backend-kind"
            ? choice == (int)TranslationBackendKind.OwnKey
            : choice != 0;

    // --- search and deep links ------------------------------------------------------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // 按住 Ctrl：导航项浮出页号键帽（§5.2 L1）。不吞事件——Ctrl 单按
        // 不属于任何动作。
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            SetNavKeyHints(true);
            return;
        }

        // Ctrl+F 聚焦搜索（§5.2：搜索的键位跨窗一致）；直接打字也进搜索。
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ExpandNav();
            _searchBox.Focus();
            _searchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Ctrl+1–6 直达页（§5.2 L1：导航项的键帽编号即落点）。
        if (Keyboard.Modifiers == ModifierKeys.Control
            && (e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9))
        {
            var digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key - Key.NumPad1;
            if (digit < _nav.Count)
            {
                SelectPage(_nav[digit].PageId);
                e.Handled = true;
            }

            return;
        }

        // 键位速查（§5.2：? 或 F1）。? 在输入框里是字符，只在没有编辑焦点
        // 时接管；F1 永远是帮助键。
        if (e.Key == Key.F1
            || (e.Key == Key.OemQuestion
                && FocusManager.GetFocusedElement(this) is not (TextBox or PasswordBox or ComboBox)))
        {
            JumpToItem("hotkeys.cheatsheet");
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ResultsHost.Visibility == Visibility.Visible)
        {
            CloseResults();
            e.Handled = true;
            return;
        }

        // Typing anywhere that is not an editor goes to the search box: a
        // settings window is mostly a search box with pages behind it.
        if (Keyboard.Modifiers is ModifierKeys.None
            && IsSearchableLetter(e.Key)
            && FocusManager.GetFocusedElement(this) is not (TextBox or PasswordBox or ComboBox))
        {
            ExpandNav();
            _searchBox.Focus();
            // 让这次按键落进搜索框，而不是被这里吞掉。
        }
    }

    private static bool IsSearchableLetter(Key key)
        => key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9;

    private void ExpandNav()
    {
        if (ActualWidth >= NavCollapseWidth)
        {
            return;
        }

        NavColumn.Width = new GridLength(NavWide);
        foreach (var (_, _, label, glyph) in _nav)
        {
            label.Visibility = Visibility.Visible;
            glyph.Margin = new Thickness(12, 0, 12, 0);
            glyph.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        if (_searchBox.Parent is FrameworkElement wrap)
        {
            wrap.Visibility = Visibility.Visible;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (ResultsHost.Visibility != Visibility.Visible)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                MoveSearchCursor(1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSearchCursor(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                if (_searchHits.Count > 0)
                {
                    var hit = _searchHits[Math.Clamp(_searchCursor, 0, _searchHits.Count - 1)];
                    JumpTo(hit.Item.Id, SettingsSchema.FindPageOf(hit.Item.Id)!.Id);
                    e.Handled = true;
                }

                break;

            case Key.Escape:
                _searchBox.Clear();
                CloseResults();
                e.Handled = true;
                break;
        }
    }

    private void MoveSearchCursor(int delta)
    {
        if (_resultRows.Count == 0)
        {
            return;
        }

        _searchCursor = Math.Clamp(_searchCursor + delta, 0, _resultRows.Count - 1);
        PaintSearchCursor();
    }

    private void PaintSearchCursor()
    {
        foreach (var (row, index) in _resultRows.Select((row, index) => (row, index)))
        {
            row.SetResourceReference(
                BackgroundProperty,
                index == _searchCursor ? "Brush.Selected" : "Brush.Surface");
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        ResultsList.Children.Clear();
        _resultRows.Clear();
        _searchHits = [];
        _searchCursor = 0;

        if (_searchBox.Text.Trim().Length == 0)
        {
            ResultsHost.Visibility = Visibility.Collapsed;
            return;
        }

        // 最多展示 8 行（§6.3）：再多是淹没，不是搜索。
        _searchHits = SettingsSearch.Find(_searchBox.Text).Take(8).ToList();
        ResultsHost.Visibility = Visibility.Visible;

        if (_searchHits.Count == 0)
        {
            var none = new TextBlock
            {
                Text = "没有匹配的设置项——换个说法试试？",
                Margin = new Thickness(8, 6, 8, 6),
            };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            ResultsList.Children.Add(none);
            return;
        }

        foreach (var hit in _searchHits)
        {
            var captured = hit;

            // 面包屑与主标签分层靠令牌色，不乘透明度（票 18/R6）。
            var breadcrumb = new TextBlock
            {
                Text = $"{hit.PageTitle} · {hit.SectionTitle}",
            };
            breadcrumb.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
            breadcrumb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

            var button = new Button
            {
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = hit.Item.Label },
                        breadcrumb,
                    },
                },
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
            };
            button.SetResourceReference(BackgroundProperty, "Brush.Surface");
            button.Click += (_, _) => JumpTo(captured.Item.Id, captured.PageId);
            ResultsList.Children.Add(button);
            _resultRows.Add(button);
        }

        // 键帽行（§6.3）：Enter 的去处在结果脚下说清楚。
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 4, 8, 4),
        };
        var enterKey = new ContentControl { Content = "Enter" };
        enterKey.SetResourceReference(StyleProperty, "KeyCap");
        var enterNote = new TextBlock
        {
            Text = " 跳到这一项",
            VerticalAlignment = VerticalAlignment.Center,
        };
        enterNote.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        enterNote.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        footer.Children.Add(enterKey);
        footer.Children.Add(enterNote);
        ResultsList.Children.Add(footer);

        PaintSearchCursor();
    }

    private void CloseResults()
    {
        ResultsHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The deep link: one id lands the user on that item — page selected, row
    /// scrolled to the middle of the view, and a decaying pulse saying
    /// "this one", because landing silently looks like not landing at all.
    /// </summary>
    public void JumpToItem(string itemId)
    {
        var page = SettingsSchema.FindPageOf(itemId);
        if (page is null)
        {
            return;
        }

        JumpTo(itemId, page.Id);
    }

    /// <summary>按页跳转（深链的页名走 <see cref="SettingsSchema.ResolvePage"/> 的别名兜底）。</summary>
    public void JumpToPage(string pageId)
    {
        if (SettingsSchema.ResolvePage(pageId) is { } page)
        {
            SelectPage(page.Id);
        }
    }

    private void JumpTo(string itemId, string pageId)
    {
        _searchBox.Clear();
        CloseResults();

        if (!_rows.TryGetValue(itemId, out var row))
        {
            SelectPage(SettingsSchema.ResolvePage(pageId)?.Id ?? "general");
            return;
        }

        SelectPage(pageId);

        // A child under a collapsed parent cannot be shown without flipping
        // the parent's value — not ours to do — so the pulse lands on the
        // deepest ancestor the user can actually see.
        var byId = AllItems().ToDictionary(entry => entry.Id);
        var target = row;
        var candidate = AllItems().FirstOrDefault(item => item.Id == itemId);
        while (candidate is { Parent: { } parentId }
               && byId.TryGetValue(parentId, out var parentItem)
               && _edited.TryGetValue(parentId, out var parentState)
               && !ParentOn(parentItem, parentState))
        {
            if (!_rows.TryGetValue(parentId, out var parentRow))
            {
                break;
            }

            target = parentRow;
            candidate = AllItems().FirstOrDefault(next => next.Id == parentId);
        }

        // The page has to lay out before there is anything to scroll.
        Dispatcher.BeginInvoke(() =>
        {
            var top = target.TranslatePoint(new Point(0, 0), (UIElement)PageScroller.Content).Y;
            var centre = top + target.ActualHeight / 2 - PageScroller.ViewportHeight / 2;
            PageScroller.ScrollToVerticalOffset(Math.Max(0, centre));

            Pulse(target);
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private bool ParentOn(SettingsItem? parent, ItemState state)
        => parent?.Control switch
        {
            SettingsControl.Toggle => state.Toggle,
            SettingsControl.Hotkey => state.Text.Trim().Length > 0,
            SettingsControl.Segmented => SegmentedParentOn(parent!.Id, state.Choice),
            _ => true,
        };

    /// <summary>
    /// Three decaying flashes rather than one steady glow: steady reads as
    /// "selected", decay reads as "look here". With animations reduced, a
    /// quiet static wash says the same thing without moving.
    /// </summary>
    private static void Pulse(FrameworkElement row)
    {
        if (row is not Grid grid)
        {
            return;
        }

        var wash = new Border
        {
            Background = (Brush)row.FindResource("Brush.Accent"),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        // 脉冲底块的圆角跟随行卡片的 Radius.Control（4）。
        wash.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        grid.Children.Add(wash);

        void Remove()
        {
            grid.Children.Remove(wash);
        }

        if (!UiAnimation.Allowed())
        {
            wash.Opacity = 0.16;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.8),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Remove();
            };
            timer.Start();
            return;
        }

        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.3) };
        foreach (var (at, peak) in new[]
                 {
                     (0.0, 0.0), (0.15, 0.38), (0.45, 0.0),
                     (0.55, 0.22), (0.85, 0.0), (0.95, 0.12), (1.3, 0.0),
                 })
        {
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at))));
        }

        pulse.Completed += (_, _) => Remove();
        wash.BeginAnimation(OpacityProperty, pulse);
    }

    // --- following outside changes (O-20) ---------------------------------------

    /// <summary>
    /// 别处改了设置（图钉、导入、引导、窄条几何）：本窗跟上去。用户已经
    /// 动过的编辑器保持他们手里的值；即改即生效之下，自己提交的那次广播
    /// 也会走到这里——值相同，推送是无害的同位刷新。
    /// </summary>
    private void OnSettingsChanged(AppSettings updated)
    {
        // 提示词模板卡自己按设置现算（Custom 控件没有通用的跟随）。
        _templates?.Refresh(updated);

        foreach (var item in AllItems())
        {
            if (_edited.TryGetValue(item.Id, out var state)
                && SettingsBindings.IsUnchanged(item.Id, _baseline, state))
            {
                PushValue(item, state, updated);
            }
        }

        _baseline = updated;

        // 凭据可能刚被存下、删掉、导入换掉，服务商也可能刚在「已保存的服务商」里切过（用户需求
        // 2026-10-10）：预设下拉的选中与「✓ 已保存」标记、凭据卡、已保存列表与提示都按新的基线重判。
        _presetRow?.Follow(updated);
        _credential?.Refresh();
        RefreshSavedProviders(updated);
    }

    /// <summary>把一项的最新值推进编辑器状态与控件。凭据除外：它永不回显。</summary>
    private void PushValue(SettingsItem item, ItemState state, AppSettings updated)
    {
        switch (item.Control)
        {
            case SettingsControl.Toggle:
            {
                var on = item.Id == "store.start-with-windows"

                    // This one's truth lives in Windows, not the file.
                    ? StartupRegistration.IsEnabled()
                    : SettingsBindings.ReadToggle(item.Id, updated) == true;
                state.Toggle = on;
                state.Text = on ? "1" : "0";
                if (_toggles.TryGetValue(item.Id, out var box) && box.IsChecked != on)
                {
                    box.IsChecked = on;
                }

                break;
            }

            case SettingsControl.Segmented:
            {
                state.Choice = SettingsBindings.ReadChoice(item.Id, updated);
                if (_segmented.TryGetValue(item.Id, out var buttons))
                {
                    foreach (var (button, index) in buttons.Select((button, index) => (button, index)))
                    {
                        button.IsChecked = index == state.Choice;
                    }
                }

                break;
            }

            case SettingsControl.Choice:
            {
                var text = SettingsBindings.ReadText(item.Id, updated) ?? string.Empty;
                state.Text = text;
                if (_choices.TryGetValue(item.Id, out var picker))
                {
                    var display = LanguageOptions.ToDisplay(text);
                    var match = picker.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(option => (string)option.Content == display);
                    if (match is not null)
                    {
                        picker.SelectedItem = match;
                    }
                }

                break;
            }

            case SettingsControl.Text or SettingsControl.Multiline or SettingsControl.Actions
                or SettingsControl.Number or SettingsControl.Hotkey or SettingsControl.Directory:
            {
                var text = SettingsBindings.ReadText(item.Id, updated) ?? string.Empty;
                state.Text = text;
                if (_textBoxes.TryGetValue(item.Id, out var box)
                    && !string.Equals(box.Text, text, StringComparison.Ordinal))
                {
                    box.Text = text;

                    if (item.Control == SettingsControl.Directory)
                    {
                        UpdateSyncWarning();
                    }
                }

                break;
            }

            case SettingsControl.Link:
            {
                // 引用卡读的是被引用项的现值：快捷键在别处改了，这里同步。
                if (_linkValue is not null && item.Id == "translate.hotkey-ref")
                {
                    _linkValue.Text = LinkValueText();
                }

                break;
            }
        }

        ApplyParentVisibility();
    }

    private static IEnumerable<SettingsItem> AllItems()
        => SettingsSchema.Tree.SelectMany(page => page.Sections).SelectMany(section => section.Items);
}
