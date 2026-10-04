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
/// 首用引导（§5.3 五屏重做，票 25）：欢迎 → 记录与隐私 → 呼出 → 翻译 →
/// 试一试。不再是"第一屏就问快捷键"的功能巡礼——先告诉用户这是什么、承诺
/// 什么，再问只有用户知道答案的几件事，最后陪他真的呼出一次窄条。
///
/// 规则（§5.3）：每一步离开时即生效（store 单字段增量写，不是最后统一
/// 落盘）；关闭和跳过都记 <see cref="AppSettings.OnboardingCompleted"/>；
/// Enter 下一步、Alt+← 上一步、Esc 跳过；练习不是关卡，完成始终可点。
/// 规格 560×自适应（上限 600），系统窗框 + Mica（票 19 spike 配方，与设置
/// 窗同一副）。
/// </summary>
internal sealed class OnboardingWindow : Window
{
    private readonly SettingsStore _store;

    /// <summary>开窗时的设置基线：编辑器初值，也是「没改就不写」的对照。</summary>
    private readonly AppSettings _baseline;

    /// <summary>真·首启（App 的入口）才用系统语言预选译文语言；重跑引导尊重已存的选择。</summary>
    private readonly bool _firstRun;

    private readonly List<FrameworkElement> _steps = [];
    private readonly List<Action> _stepCommits = [];
    private readonly List<KeyCapRecorder> _recorders = [];
    private int _step;
    private bool _finished;
    private bool _completedMarked;

    private readonly ScrollViewer _body = new();
    private readonly TextBlock _stepLabel = new();
    private readonly TextBlock _saveError = new();
    private readonly Button _back = new() { Content = "上一步", Padding = new Thickness(12, 4, 12, 4), Cursor = Cursors.Hand };
    private readonly Button _next = new() { Content = "下一步", Padding = new Thickness(16, 5, 16, 5), Cursor = Cursors.Hand };
    private readonly Button _skip = new() { Content = "跳过", Padding = new Thickness(10, 3, 10, 3), Cursor = Cursors.Hand };

    // --- step editors' state ------------------------------------------------------

    private CheckBox? _recordImages;
    private CheckBox? _recordFiles;
    private readonly HashSet<string> _excludedApps = new(StringComparer.OrdinalIgnoreCase);
    private KeyCapRecorder? _barRecorder;
    private KeyCapRecorder? _quickRecorder;
    private KeyCapRecorder? _captureRecorder;
    private CheckBox? _winVSwitch;
    private ItemState? _presetState;
    private PasswordBox? _keyBox;
    private ItemState? _languageState;

    // --- the trial checklist (step 4) ----------------------------------------------

    private FrameworkElement? _trial1;
    private FrameworkElement? _trial2;
    private FrameworkElement? _trial3;
    private AppShell? _shell;

    /// <param name="store">写设置的唯一入口（O-20）：每步离开时增量生效。</param>
    /// <param name="firstRun">真首启：译文语言预选系统语言；重跑尊重已存值。</param>
    /// <param name="shell">「试一试」的信号源（复制/呼出/粘贴）；重跑自设置窗时可空。</param>
    public OnboardingWindow(SettingsStore store, bool firstRun = true, AppShell? shell = null)
    {
        _store = store;
        _baseline = store.Current;
        _firstRun = firstRun;
        _shell = shell;

        Title = "欢迎使用拾语";
        Width = 560;
        MaxHeight = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui");
        FontSize = (double)Application.Current.FindResource("Type.Body");
        SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");

        // 票 19 spike 配方（与设置窗同一副）：系统窗框 + 客户区透明 + DWM
        // Mica；材质没被接受时回退到 Background 一档的不透明底。
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this)
                is System.Windows.Interop.HwndSource { CompositionTarget: { } target })
            {
                target.BackgroundColor = Colors.Transparent;
            }
        };        Backdrop.Attach(this, () => BackdropKind.Mica, applied =>
        {
            if (!applied)
            {
                SetResourceReference(BackgroundProperty, "Brush.Background");
            }
        });

        BuildSteps();
        BuildChrome();

        // 关闭（X）也是完成：引导绝不该因为它没跑完而再来一次（§5.3）。
        Closing += (_, _) => MarkCompleted();
        PreviewKeyDown += OnWindowKeyDown;

        Show(0);
    }

    // --- chrome ----------------------------------------------------------------------

    private void BuildChrome()
    {
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _body.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _body.Margin = new Thickness(24, 18, 24, 0);

        _stepLabel.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        _stepLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");

        _saveError.TextWrapping = TextWrapping.Wrap;
        _saveError.Visibility = Visibility.Collapsed;
        _saveError.Margin = new Thickness(0, 6, 0, 0);
        _saveError.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        _saveError.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");

        _skip.SetResourceReference(StyleProperty, "FlyoutButton");
        _skip.ToolTip = "用默认设置直接开始（Esc 同效）";
        _skip.Click += (_, _) => Skip();

        _back.Click += (_, _) => Show(_step - 1);
        _next.Click += (_, _) => Next();
        _next.SetResourceReference(Control.BackgroundProperty, "Brush.Accent");
        _next.SetResourceReference(Control.ForegroundProperty, "Brush.TextOnAccent");
        _next.BorderThickness = new Thickness(0);

        var footer = new DockPanel { Margin = new Thickness(24, 12, 24, 18), LastChildFill = true };

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_skip);
        var stepHost = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        stepHost.Children.Add(_stepLabel);
        left.Children.Add(stepHost);
        DockPanel.SetDock(left, Dock.Left);
        footer.Children.Add(left);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _back.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);

        var footerRows = new StackPanel();
        footerRows.Children.Add(footer);
        footerRows.Children.Add(_saveError);
        DockPanel.SetDock(footerRows, Dock.Bottom);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(footerRows);
        root.Children.Add(_body);
        Content = root;
    }

    private void BuildSteps()
    {
        _steps.Add(WelcomeStep()); _stepCommits.Add(() => { });
        _steps.Add(PrivacyStep()); _stepCommits.Add(CommitPrivacy);
        _steps.Add(SummonStep()); _stepCommits.Add(CommitSummon);
        _steps.Add(TranslateStep()); _stepCommits.Add(CommitTranslate);
        _steps.Add(TrialStep()); _stepCommits.Add(() => { });
    }

    private void Show(int index)
    {
        if (index < 0 || index >= _steps.Count || index == _step && _body.Content is not null)
        {
            return;
        }

        // 离开即生效（§5.3）：上一步的答案此刻落盘，不是攒到最后。
        if (_body.Content is not null)
        {
            _stepCommits[_step]();
        }

        _step = index;
        _body.Content = _steps[index];
        _stepLabel.Text = $"第 {index + 1} 步，共 {_steps.Count} 步";
        _back.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = index == _steps.Count - 1 ? "完成" : "下一步";

        // 欢迎屏自带「开始 / 跳过，用默认设置」两枚主按钮（§5.3）：脚注里
        // 再挂一组下一步/跳过就是同一件事问两遍——只留步数标签（Enter 照常
        // 前进）。
        var footerButtons = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        _skip.Visibility = footerButtons;
        _next.Visibility = footerButtons;
    }

    private void Next()
    {
        if (_step >= _steps.Count - 1)
        {
            Finish();
            return;
        }

        Show(_step + 1);
    }

    /// <summary>跳过（按钮或 Esc）：已答的照常生效，剩下的交给默认值。</summary>
    private void Skip()
    {
        _stepCommits[_step]();
        Finish();
    }

    private void Finish()
    {
        _finished = true;
        MarkCompleted();
        Close();
    }

    /// <summary>记 OnboardingCompleted；关窗路径（X）也会走到。失败要说人话，不许安静。只试一次。</summary>
    private void MarkCompleted()
    {
        if (_completedMarked || _baseline.OnboardingCompleted)
        {
            return;
        }

        _completedMarked = true;
        try
        {
            _store.Update(s => s with { OnboardingCompleted = true }, AppPaths.SettingsFile);
        }
        catch (SettingsSaveException failure)
        {
            // 引导的结果没能落盘必须明说（票 23 留尾：MessageBox 换共享
            // ContentDialog）——安静关窗会让人以为一切都好了。
            if (_finished)
            {
                ContentDialog.Show(
                    this,
                    "引导没有保存成功",
                    failure.Message + " 可以关闭本窗继续使用，稍后从 设置 → 关于 重新运行引导即可重试。",
                    new ContentDialogButton("知道了", ContentDialogButtonStyle.Accent, IsCancelFocus: true));
            }
            else
            {
                // 还没到收尾（用户点了 X）：说在同一扇窗的脚注里，不打断关窗。
                _saveError.Text = failure.Message + " 这次改动没有生效，可以重试。";
                _saveError.Visibility = Visibility.Visible;
            }
        }
    }

    // --- keyboard（§5.3：Enter 下一步，Alt+← 上一步，Esc 跳过） ----------------------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // 录制器在听时，Esc 是它的取消，不是跳过。
        if (_recorders.Any(recorder => recorder.IsListening))
        {
            return;
        }

        // Alt 组合键以 System 键到来（菜单访问键语义），取真正的键。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            e.Handled = true;
            Skip();
            return;
        }

        if (key == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            e.Handled = true;
            Show(_step - 1);
            return;
        }

        if (key != Key.Enter)
        {
            return;
        }

        // Enter 落在编辑器（密码框、下拉）或按钮上时归它们：默认按钮语义
        // 不能与"下一步"抢同一枚键。
        if (FocusManager.GetFocusedElement(this)
            is TextBox or PasswordBox or ComboBox or Button or CheckBox or ToggleButton)
        {
            return;
        }

        e.Handled = true;
        Next();
    }

    // --- step 0: 欢迎 ------------------------------------------------------------------

    private FrameworkElement WelcomeStep()
    {
        var panel = new StackPanel();

        var mark = new Border
        {
            Width = 48,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 12),
            Child = new TextBlock
            {
                Text = "拾",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        mark.SetResourceReference(BackgroundProperty, "Brush.Accent");
        mark.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        var markText = (TextBlock)mark.Child;
        markText.SetResourceReference(TextElement.FontSizeProperty, "Type.Title");
        markText.FontWeight = FontWeights.SemiBold;
        markText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextOnAccent");
        panel.Children.Add(mark);

        var title = new TextBlock { Text = "拾语", Margin = new Thickness(0, 0, 0, 4) };
        title.SetResourceReference(TextElement.FontSizeProperty, "Type.Title");
        title.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(title);

        var tagline = new TextBlock
        {
            Text = "剪贴板历史、划词翻译与悬停动作——一切只在这台电脑上。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        tagline.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(tagline);

        foreach (var promise in new[]
                 {
                     "复制过的内容只存在这台电脑，不上传、不联网。",
                     "常见密码管理器默认不记录，还可以自己加排除。",
                     "划词翻译可用——用你自己的密钥，或等即将推出的公共通道。",
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var glyph = new TextBlock { Text = "\uE73E", Margin = new Thickness(0, 0, 8, 0) };
            glyph.SetResourceReference(TextElement.FontFamilyProperty, "Font.Icon");
            glyph.SetResourceReference(TextElement.FontSizeProperty, "Size.IconS");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
            row.Children.Add(glyph);
            var text = new TextBlock { Text = promise, TextWrapping = TextWrapping.Wrap };
            row.Children.Add(text);
            panel.Children.Add(row);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 4),
        };
        var start = new Button
        {
            Content = "开始",
            Padding = new Thickness(20, 6, 20, 6),
            Cursor = Cursors.Hand,
        };
        start.SetResourceReference(Control.BackgroundProperty, "Brush.Accent");
        start.SetResourceReference(Control.ForegroundProperty, "Brush.TextOnAccent");
        start.BorderThickness = new Thickness(0);
        start.Click += (_, _) => Show(1);
        buttons.Children.Add(start);

        var skipDefaults = new Button
        {
            Content = "跳过，用默认设置",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(10, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "默认设置已完全可用（Esc 同效）",
        };
        skipDefaults.SetResourceReference(StyleProperty, "FlyoutButton");
        skipDefaults.Click += (_, _) => Skip();
        buttons.Children.Add(skipDefaults);
        panel.Children.Add(buttons);

        return panel;
    }

    // --- step 1: 记录与隐私 ---------------------------------------------------------

    private FrameworkElement PrivacyStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(StepIntro("记录什么由你定。文本是拾语成立的前提，始终记录；其余两样是开关。"));

        // 只读行：文本 · 始终记录（§5.3——不再把"不记录什么"排在前面）。
        var readOnly = new TextBlock { Text = "始终记录", VerticalAlignment = VerticalAlignment.Center };
        readOnly.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(Row("文本", readOnly));

        _recordImages = SwitchRow(_baseline.RecordImages, "复制图片时是否入库");
        panel.Children.Add(Row("图片", _recordImages));
        _recordFiles = SwitchRow(_baseline.RecordFiles, "复制文件时是否入库");
        panel.Children.Add(Row("文件", _recordFiles));

        panel.Children.Add(SectionGap());
        var exclusion = new TextBlock { Text = "排除" };
        exclusion.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        exclusion.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(exclusion);

        // 内置清单收进一枚可展开的 chip（§5.3：不再 11 个灰显复选框全列出）。
        var presets = ExclusionPolicy.Presets.Select(rule => rule.Value).ToList();
        var presetHost = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var fold = new ToggleButton
        {
            Content = $"已内置 {presets.Count} 个常见密码管理器 ▾",
            Padding = new Thickness(10, 4, 10, 4),
            Cursor = Cursors.Hand,
        };
        fold.SetResourceReference(BackgroundProperty, "Brush.Surface");
        fold.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        fold.BorderThickness = new Thickness(1);
        fold.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        presetHost.Children.Add(fold);

        var names = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        foreach (var name in presets)
        {
            names.Children.Add(Chip(name));
        }

        fold.Checked += (_, _) => { names.Visibility = Visibility.Visible; fold.Content = $"已内置 {presets.Count} 个常见密码管理器 ▴"; };
        fold.Unchecked += (_, _) => { names.Visibility = Visibility.Collapsed; fold.Content = $"已内置 {presets.Count} 个常见密码管理器 ▾"; };
        presetHost.Children.Add(names);
        panel.Children.Add(presetHost);

        // 只列检测到的正在运行候选（§5.3）——不猜用户装了什么。
        var candidates = RunningCandidates(presets);
        var pickHost = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (candidates.Count == 0)
        {
            var none = new TextBlock { Text = "没有检测到正在运行的密码管理器——需要的话手动添加。" };
            none.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
            none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            pickHost.Children.Add(none);
        }
        else
        {
            var ask = new TextBlock { Text = "检测到正在运行的：" };
            ask.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
            ask.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            pickHost.Children.Add(ask);

            foreach (var name in candidates)
            {
                var captured = name;
                var box = new CheckBox
                {
                    Content = name,
                    Margin = new Thickness(0, 2, 12, 2),
                    Cursor = Cursors.Hand,
                    ToolTip = "勾选后不再记录来自它的复制",
                };
                box.Checked += (_, _) => _excludedApps.Add(captured);
                box.Unchecked += (_, _) => _excludedApps.Remove(captured);
                pickHost.Children.Add(box);
            }
        }

        // ＋ 添加应用…（§5.3）。
        var add = new Button { Content = "＋ 添加应用…", Padding = new Thickness(10, 3, 10, 3), Cursor = Cursors.Hand };
        add.SetResourceReference(StyleProperty, "FlyoutButton");
        var manual = new TextBox { Width = 180, Visibility = Visibility.Collapsed, Padding = new Thickness(6, 3, 6, 3) };
        manual.SetResourceReference(TextBox.BackgroundProperty, "Brush.SurfaceInput");
        var join = new Button { Content = "加入", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed, Cursor = Cursors.Hand };
        join.SetResourceReference(StyleProperty, "FlyoutButton");
        add.Click += (_, _) =>
        {
            manual.Visibility = Visibility.Visible;
            join.Visibility = Visibility.Visible;
            add.Visibility = Visibility.Collapsed;
            manual.Focus();
        };
        void Join()
        {
            var name = manual.Text.Trim();
            if (name.Length > 0)
            {
                _excludedApps.Add(name);
                manual.Clear();
            }
        }
        join.Click += (_, _) => Join();
        manual.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Join();
                e.Handled = true;
            }
        };

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        addRow.Children.Add(add);
        addRow.Children.Add(manual);
        addRow.Children.Add(join);
        pickHost.Children.Add(addRow);
        panel.Children.Add(pickHost);

        var footnote = new TextBlock
        {
            Text = "勾选与手填的名字都写进排除规则，随时在 设置 → 记录与隐私 修改。",
            Margin = new Thickness(0, 10, 0, 0),
        };
        footnote.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        footnote.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(footnote);
        return panel;
    }

    private void CommitPrivacy()
    {
        // 单字段增量写（O-20）：在最新设置上只动这一步真正改过的东西。
        TryUpdate(latest => latest with
        {
            RecordImages = _recordImages?.IsChecked == true,
            RecordFiles = _recordFiles?.IsChecked == true,
            ExclusionRules = MergeExclusions(latest),
        });
    }

    /// <summary>用户挑的名单并进现有规则：已有的不动，新的追加，绝不删别人的。</summary>
    private IReadOnlyList<StoredExclusionRule> MergeExclusions(AppSettings latest)
    {
        var kept = latest.ExclusionRules
            .Where(rule => rule.Kind == ExclusionRuleKind.SourceApp)
            .Select(rule => rule.Value)
            .ToList();
        var merged = latest.ExclusionRules.ToList();
        foreach (var name in _excludedApps.Where(name => !kept.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            merged.Add(new StoredExclusionRule(ExclusionRuleKind.SourceApp, name));
        }

        return merged;
    }

    // --- step 2: 呼出 -----------------------------------------------------------------

    private FrameworkElement SummonStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(StepIntro("三组组合键。窄条是常驻的取用面板，快速粘贴贴光标即贴即走，划词翻译抓当前选中。"));

        // 窄条示意图：静态占位（§5.3）。
        panel.Children.Add(BarSketch());

        // 主卡：呼出窄条。
        _barRecorder = NewRecorder(HotkeyAction.Bar, _baseline.BarHotkey);
        panel.Children.Add(Card("呼出窄条", "唤出/收起常驻的历史窄条", _barRecorder.Build()));

        // 子卡：快速粘贴 + 也用 Win+V。
        _quickRecorder = NewRecorder(HotkeyAction.QuickBar, _baseline.QuickBarHotkey);
        var quickBody = new StackPanel();
        quickBody.Children.Add(_quickRecorder.Build());
        _winVSwitch = new CheckBox { Content = "也用 Win+V 呼出", Cursor = Cursors.Hand, Margin = new Thickness(0, 8, 0, 0) };
        _winVSwitch.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        _winVSwitch.IsChecked = _baseline.TakeOverWinV;
        var winVHint = new TextBlock
        {
            Text = "代替系统的剪贴板面板；默认关闭，关闭即刻还原。",
            TextWrapping = TextWrapping.Wrap,
        };
        winVHint.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        winVHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        var winVStack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        winVStack.Children.Add(_winVSwitch);
        winVStack.Children.Add(winVHint);
        quickBody.Children.Add(winVStack);
        panel.Children.Add(Card("快速粘贴", "选中即粘贴，粘贴后消失", quickBody));

        // 次卡：划词翻译。
        _captureRecorder = NewRecorder(HotkeyAction.CaptureSelection, _baseline.CaptureHotkey);
        panel.Children.Add(Card("划词翻译", "抓取当前选中的文字并翻译", _captureRecorder.Build()));

        return panel;
    }

    /// <summary>录制器：即时校验五键互撞（含未在此编辑的翻译剪贴板/管理窗），文案逐条点名。</summary>
    private KeyCapRecorder NewRecorder(HotkeyAction action, string initial)
    {
        var recorder = new KeyCapRecorder(
            this,
            initial,
            _ => { },
            combination =>
            {
                var candidate = HotkeyCandidate(action, combination);
                var problems = HotkeyPlan.Build(candidate).Problems;
                return problems.Count == 0 ? null : string.Join(" ", problems);
            });
        _recorders.Add(recorder);
        return recorder;
    }

    /// <summary>当前三个录制器的值 + 未编辑的键取现设置，组合出一份可判定的候选方案。</summary>
    private AppSettings HotkeyCandidate(HotkeyAction action, string combination)
        => ApplyKey(
            _store.Current with
            {
                BarHotkey = _barRecorder?.Value ?? string.Empty,
                QuickBarHotkey = _quickRecorder?.Value ?? string.Empty,
                CaptureHotkey = _captureRecorder?.Value ?? string.Empty,
            },
            action,
            combination);

    private static AppSettings ApplyKey(AppSettings settings, HotkeyAction action, string combination)
        => action switch
        {
            HotkeyAction.Bar => settings with { BarHotkey = combination },
            HotkeyAction.QuickBar => settings with { QuickBarHotkey = combination },
            HotkeyAction.CaptureSelection => settings with { CaptureHotkey = combination },
            HotkeyAction.ClipboardTranslate => settings with { ClipboardTranslateHotkey = combination },
            HotkeyAction.Library => settings with { LibraryHotkey = combination },
            _ => settings,
        };

    private void CommitSummon()
    {
        // 录制时已即时校验过互撞；这里照单落盘，含 Win+V 接管开关。
        TryUpdate(latest => latest with
        {
            BarHotkey = _barRecorder?.Value ?? string.Empty,
            QuickBarHotkey = _quickRecorder?.Value ?? string.Empty,
            CaptureHotkey = _captureRecorder?.Value ?? string.Empty,
            TakeOverWinV = _winVSwitch?.IsChecked == true,
        });
    }

    // --- step 3: 翻译 ----------------------------------------------------------------

    private FrameworkElement TranslateStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(StepIntro("翻译怎么走。自备密钥：文本直接发给你选的服务商；公共通道上线前只能选自备。"));

        // 译文语言：默认取系统显示语言（§5.3；仅真首启预选，重跑尊重已存值）。
        var initialLanguage = _firstRun && LanguageOptions.FromSystemUi() is { } system
            ? system
            : _baseline.TargetLanguage;

        _languageState = new ItemState { Text = initialLanguage };
        var picker = ItemEditors.Choice(
            SettingsSchema.Find("service.target-language")!,
            _baseline with { TargetLanguage = initialLanguage },
            _languageState,
            toDisplay: LanguageOptions.ToDisplay,
            toValue: LanguageOptions.ToValue);

        panel.Children.Add(Card("译文语言", "翻译结果使用的语言", picker));

        // 两张单选卡（§5.3）：自备密钥可选，公共通道「即将推出」点不动。
        var ownBody = new StackPanel();

        _presetState = new ItemState();
        var presetRow = new ServicePresetRow(
            _baseline,
            _presetState,
            readForm: () =>
            {
                // 凭据留空 = 沿用已存的——只沿用属于这个地址的那把（票 29）。
                var preset = ProviderPresets.Find(_presetState.Text);
                return new ServiceForm(
                    _baseline,
                    preset?.BaseUrl ?? _baseline.BackendBaseUrl,
                    preset?.DefaultModel ?? _baseline.BackendModel,
                    _keyBox?.Password ?? string.Empty);
            },
            applyPreset: _ => { });

        ownBody.Children.Add(presetRow.Element);

        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _keyBox = new PasswordBox { Width = 240, Padding = new Thickness(6, 3, 6, 3) };
        _keyBox.SetResourceReference(PasswordBox.BackgroundProperty, "Brush.SurfaceInput");
        _keyBox.ToolTip = "已保存的凭据不回显；留空 = 保留已存的那个";
        keyRow.Children.Add(new TextBlock
        {
            Text = "凭据",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        keyRow.Children.Add(_keyBox);
        ownBody.Children.Add(keyRow);

        // 已存的密钥属于别家时，凭据框下方说清楚（票 29）；框里开始填了就不再提示。
        _keyBox.PasswordChanged += (_, _) => presetRow.RefreshKeyHint();
        ownBody.Children.Add(presetRow.KeyHint);
        presetRow.RefreshKeyHint();

        var ownCard = Card("自备密钥", "选中即填好服务地址与模型；「申请密钥」直达服务商，「测试连接」当场验证。", ownBody);
        MarkSelected(ownCard, selected: true);
        panel.Children.Add(ownCard);

        var relayCard = Card(
            "公共通道 · 即将推出",
            "零配置零密钥；上线条件（大陆可达、服务端加固、运营费用）未满足前不可选。",
            new TextBlock(),
            disabled: true);
        panel.Children.Add(relayCard);

        var privacy = new TextBlock
        {
            Text = "只有被翻译的文本会离开这台电脑；剪贴板历史始终只在本机。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        privacy.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        privacy.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(privacy);

        return panel;
    }

    private void CommitTranslate()
    {
        var presetId = _presetState?.Text ?? _baseline.BackendPresetId;
        var preset = ProviderPresets.Find(presetId);
        var typedKey = _keyBox?.Password is { Length: > 0 } key ? key : null;
        var language = _languageState?.Text ?? _baseline.TargetLanguage;

        TryUpdate(latest =>
        {
            var updated = latest with
            {
                TranslationBackend = TranslationBackendKind.OwnKey,
                BackendPresetId = preset?.Id ?? latest.BackendPresetId,
                BackendBaseUrl = preset?.BaseUrl ?? latest.BackendBaseUrl,
                BackendModel = preset?.DefaultModel ?? latest.BackendModel,
                TargetLanguage = language,
            };

            // 票 29：填了密钥就连同它的来源一起写——来源取这一步刚落下的地址，
            // 不是旧地址。没填就不动已存的那一对：换了服务商，它自然不再被带上。
            return typedKey is null ? updated : updated.WithApiKey(typedKey);
        });
    }

    // --- step 4: 试一试 ---------------------------------------------------------------

    private FrameworkElement TrialStep()
    {
        var panel = new StackPanel();
        panel.Children.Add(StepIntro("三十秒，把窄条真的用一次——检测到就打勾；练习不是关卡，完成随时可点。"));

        _trial1 = TrialRow("复制任意一段文字");
        _trial2 = TrialKeyRow("呼出窄条", HotkeyAction.Bar);
        _trial3 = TrialRow("按 Enter 粘贴回来");
        panel.Children.Add(_trial1);
        panel.Children.Add(_trial2);
        panel.Children.Add(_trial3);

        var signals = _shell;
        if (signals is null)
        {
            var note = new TextBlock
            {
                Text = "（从设置重跑的引导收不到实时检测——三项请自行确认。）",
                Margin = new Thickness(0, 8, 0, 0),
            };
            note.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
            note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
            panel.Children.Add(note);
            return panel;
        }

        // 三个信号（§5.3「检测到即勾」）：复制→库里有新内容；呼出→ToggleBar
        // 被触发；粘贴→窄条 Enter/编号键落进原窗口。
        signals.Store.Changed += OnTrialSignal1;
        signals.BarSummoned += OnTrialSignal2;
        signals.BarPasted += OnTrialSignal3;
        Closed += (_, _) =>
        {
            signals.Store.Changed -= OnTrialSignal1;
            signals.BarSummoned -= OnTrialSignal2;
            signals.BarPasted -= OnTrialSignal3;
        };

        return panel;
    }

    private void OnTrialSignal1() => Tick(_trial1);
    private void OnTrialSignal2() => Tick(_trial2);
    private void OnTrialSignal3() => Tick(_trial3);

    /// <summary>打勾要在 UI 线程画（信号可能来自剪贴板线程）。</summary>
    private void Tick(FrameworkElement? row)
        => Dispatcher.Invoke(() => PaintTrial(row, done: true));

    private FrameworkElement TrialRow(string text)
        => TrialHost(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });

    /// <summary>带键帽的任务行：② 的组合键从现设置渲染（第 2 步刚录的）。</summary>
    private FrameworkElement TrialKeyRow(string text, HotkeyAction action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        foreach (var chip in KeyMap.Chips(KeyMap.Combination(action, _store.Current)))
        {
            var cap = new ContentControl { Content = chip, VerticalAlignment = VerticalAlignment.Center };
            cap.SetResourceReference(StyleProperty, "KeyCap");
            cap.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(cap);
        }

        return TrialHost(row);
    }

    /// <summary>一行任务：勾选圈（Tag 记着它，打勾只换这一个）+ 文本。</summary>
    private static FrameworkElement TrialHost(FrameworkElement content)
    {
        var glyph = new TextBlock
        {
            Text = "○",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        glyph.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(glyph);
        row.Children.Add(content);
        row.Tag = glyph;
        return row;
    }

    private static void PaintTrial(FrameworkElement? row, bool done)
    {
        if (row?.Tag is not TextBlock glyph)
        {
            return;
        }

        if (done)
        {
            glyph.Text = "\uE73E";
            glyph.SetResourceReference(TextElement.FontFamilyProperty, "Font.Icon");
            glyph.SetResourceReference(TextElement.FontSizeProperty, "Size.IconS");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        }
        else
        {
            glyph.Text = "○";
            glyph.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
            glyph.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        }
    }

    // --- small builders ---------------------------------------------------------------

    private static TextBlock StepIntro(string text)
    {
        var intro = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        intro.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        intro.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        return intro;
    }

    private static FrameworkElement SectionGap() => new Border { Height = 14 };

    /// <summary>标签列 + 控件列的一行（沿用引导旧版的栅格：96 + 弹性）。</summary>
    private static FrameworkElement Row(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static CheckBox SwitchRow(bool initial, string hint)
    {
        var box = new CheckBox { IsChecked = initial, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
        box.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        box.ToolTip = hint;
        return box;
    }

    private static Border Chip(string text)
    {
        var chip = new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock { Text = text },
        };
        chip.SetResourceReference(BackgroundProperty, "Brush.Surface");
        chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        chip.BorderThickness = new Thickness(1);
        chip.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        return chip;
    }

    /// <summary>
    /// 一张卡（§5.3 的主卡/子卡/次卡）：Surface 底、1px 边、Radius.Control、
    /// 标题加说明在上。<paramref name="disabled"/> 把文字压到 TextTertiary——
    /// IsEnabled 挡不住点击之外的视觉，"即将推出"必须一眼看出点不动。
    /// </summary>
    private static Border Card(string title, string description, FrameworkElement body, bool disabled = false)
    {
        var heading = new TextBlock { Text = title };
        heading.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        heading.FontWeight = FontWeights.SemiBold;
        heading.SetResourceReference(
            TextBlock.ForegroundProperty,
            disabled ? "Brush.TextTertiary" : "Brush.Text");

        var caption = new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8),
        };
        caption.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        caption.SetResourceReference(
            TextBlock.ForegroundProperty,
            disabled ? "Brush.TextTertiary" : "Brush.TextSecondary");

        var panel = new StackPanel();
        panel.Children.Add(heading);
        panel.Children.Add(caption);
        panel.Children.Add(body);

        var card = new Border
        {
            Child = panel,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            IsEnabled = !disabled,
        };
        card.SetResourceReference(BackgroundProperty, "Brush.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        card.BorderThickness = new Thickness(1);
        card.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        return card;
    }

    private static void MarkSelected(Border card, bool selected)
        => card.SetResourceReference(Border.BorderBrushProperty, selected ? "Brush.Accent" : "Brush.Border");

    /// <summary>窄条示意图（§5.3 静态占位）：头部 + 搜索条 + 三张灰卡，一眼认出"就是这个东西"。</summary>
    private static FrameworkElement BarSketch()
    {
        var sketch = new StackPanel { Width = 240 };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var dot = new Border { Width = 12, Height = 12 };
        dot.SetResourceReference(BackgroundProperty, "Brush.Accent");
        dot.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        head.Children.Add(dot);
        head.Children.Add(new Border
        {
            Height = 12,
            Width = 120,
            Margin = new Thickness(8, 0, 0, 0),
        });

        sketch.Children.Add(head);

        var search = new Border { Height = 18, Margin = new Thickness(0, 0, 0, 6) };
        search.SetResourceReference(BackgroundProperty, "Brush.SurfaceInput");
        search.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        search.BorderThickness = new Thickness(1);
        search.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        sketch.Children.Add(search);

        foreach (var height in new[] { 34, 26, 30 })
        {
            var cardBar = new Border { Height = height, Margin = new Thickness(0, 0, 0, 6) };
            cardBar.SetResourceReference(BackgroundProperty, "Brush.Surface");
            cardBar.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            cardBar.BorderThickness = new Thickness(1);
            cardBar.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
            sketch.Children.Add(cardBar);
        }

        var host = new Border
        {
            Child = sketch,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        host.SetResourceReference(BackgroundProperty, "Brush.Background");
        host.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        host.BorderThickness = new Thickness(1);
        host.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        return host;
    }

    /// <summary>正在运行的密码管理器候选：内置清单已有的不算（已覆盖），其余按名字认。</summary>
    private static List<string> RunningCandidates(IReadOnlyCollection<string> presetNames)
    {
        var known = presetNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            var name = process.ProcessName;
            if (name.Length == 0
                || known.Contains(name)
                || found.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Contains("pass", StringComparison.OrdinalIgnoreCase)
                || name.Contains("vault", StringComparison.OrdinalIgnoreCase)
                || name.Contains("keepass", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(name);
            }
        }

        return found;
    }

    /// <summary>每步离开时的增量写：失败说在脚注，不抛出 UI 处理器（O-24）。</summary>
    private void TryUpdate(Func<AppSettings, AppSettings> mutate)
    {
        try
        {
            _store.Update(mutate, AppPaths.SettingsFile);
        }
        catch (SettingsSaveException failure)
        {
            _saveError.Text = failure.Message + " 这一步的改动没有生效，回到这一步改完再走即可。";
            _saveError.Visibility = Visibility.Visible;
        }
    }
}
