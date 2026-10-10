#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Shiyu.App;

/// <summary>
/// 宿主的调试探针区（票 06/15，O-40 拆出宿主文件）：只在 Debug 构建存在的
/// 启动旋钮与呼出通道。发布构建里整个文件编译为空——旋钮的读取本身住在
/// <see cref="DebugOverrides"/>（O-41）。
/// </summary>
public partial class App
{
    /// <summary>
    /// 探针决策先于互斥量（票 15）：名字就是让探针实例与用户真实例并排跑
    /// 的全部机关——同一数据目录得到同一互斥量，同目录探针仍然互斥；不同
    /// 目录、以及与用户的 <c>Shiyu</c>，永不相撞。
    /// </summary>
    private static string PrepareProbe()
    {
        if (DebugOverrides.ProbeDirectory is not { } probeDirectory)
        {
            return "Shiyu";
        }

        // Settings move into the probe directory too: reading the real ones
        // would aim the probe at the user's backend, and writing them back
        // (geometry, relay id) would reach the real file.
        AppPaths.UseProbeDirectory(probeDirectory);
        Trace.WriteLine($"probe mode: hotkeys/hooks off, data dir {probeDirectory}");
        return DebugOverrides.ProbeMutexName(probeDirectory);
    }

    /// <summary>
    /// 实机验收用的隐藏命令（票 06）：在 DispatcherTimer.Tick 里抛一个异常，
    /// 验证 UI 线程兜底——进程存活、日志一行、托盘提示一次。与
    /// <c>SHIYU_DATA_DIR</c> 同族，绝不进发布。
    /// </summary>
    private static void InstallDebugTickCrash()
    {
        if (!DebugOverrides.DebugTickCrash)
        {
            return;
        }

        var crash = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        crash.Tick += (_, _) =>
        {
            // 单发：验证的是"抛 + 存活 + 提示一次"，不是压测节流。
            crash.Stop();
            throw new InvalidOperationException("debug tick crash probe");
        };
        crash.Start();
    }

    /// <summary>
    /// 探针便利（同 SHIYU_DATA_DIR 一族）：启动即打开设置窗/更新窗，自动化
    /// 检查不必找托盘图标、可以端到端驱动手工流程。
    /// </summary>
    private void OpenDebugStartupWindows()
    {
        if (DebugOverrides.OpenSettings)
        {
            _modules?.Settings.Show();
        }

        if (DebugOverrides.OpenUpdate)
        {
            _modules?.Update.ShowWindow();
        }
    }

    /// <summary>
    /// 探针命令 caret（用户实录 2026-10-05：空搜索框里光标落在占位第一、二个字
    /// 之间，而且不明显）。WPF 在运行时把 TextBox 的 Padding 交给 PART_ContentHost，
    /// 正文再缩进 2 DIP；模板若再拿 Padding 排一次，正文就比占位多缩进一个 Padding。
    /// 打开窄条、管理窗、设置（快捷键页：空框有「未设置」占位）和一扇样张窗
    /// （密码框、数字框、多行框），逐框把几何写进 caret.log，探针脚本据此判定。
    /// </summary>
    private async void ProbeCaretGeometry(AppShell shell)
    {
        shell.ToggleBar?.Invoke();
        shell.ShowLibrary?.Invoke();
        _modules!.Settings.ShowAt("hotkey.capture");

        var samples = new StackPanel { Margin = new Thickness(16) };
        samples.Children.Add(new PasswordBox { Password = "probe" });
        var number = new NumberBox { Unit = "ms", Text = "500", Margin = new Thickness(0, 8, 0, 8) };
        number.SetResourceReference(FrameworkElement.StyleProperty, "NumberBox");
        samples.Children.Add(number);
        var multiLine = new TextBox
        {
            AcceptsReturn = true,
            Height = 90,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(10, 6, 10, 6),
        };
        InputProps.SetPlaceholder(multiLine, "probe placeholder");
        samples.Children.Add(multiLine);
        new Window
        {
            Title = "caret-samples",
            Width = 420,
            Height = 320,
            Left = 40,
            Top = 40,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = samples,
        }.Show();

        for (var round = 0; round < 6; round++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        var accent = (FindResource("Brush.Accent") as SolidColorBrush)?.Color;
        var searchStyle = FindResource("SearchBox");
        var log = new StringBuilder();
        void Line(Window window, string kind, bool empty, Rect caret, Point placeholder, double origin, Thickness border, Thickness padding, Brush caretBrush)
            => log.AppendLine(FormattableString.Invariant(
                $"box|{window.GetType().Name}|{kind}|empty={empty}|caret={caret.X:F1},{caret.Y:F1}|placeholder={placeholder.X:F1},{placeholder.Y:F1}|origin={origin:F1}|expect={border.Left + padding.Left + 2:F1}|accent={(caretBrush as SolidColorBrush)?.Color == accent}"));

        foreach (Window window in Windows)
        {
            foreach (var box in Descendants(window).OfType<TextBox>())
            {
                var hasPlaceholder = !string.IsNullOrEmpty(InputProps.GetPlaceholder(box));
                if ((!hasPlaceholder && box is not NumberBox) || !box.IsVisible || box.Template is null)
                {
                    continue;
                }

                var caret = box.GetRectFromCharacterIndex(0);
                var shown = box.Template.FindName("Placeholder", box) as FrameworkElement;
                var empty = hasPlaceholder && box.Text.Length == 0 && shown is { IsVisible: true };
                var at = empty ? shown!.TranslatePoint(new Point(0, 0), box) : new Point(double.NaN, double.NaN);
                var kind = box.Style == searchStyle ? "SearchBox" : box.GetType().Name;
                Line(window, kind, empty, caret, at, caret.X, box.BorderThickness, box.Padding, box.CaretBrush);
            }

            foreach (var box in Descendants(window).OfType<PasswordBox>())
            {
                if (!box.IsVisible || box.Template is null)
                {
                    continue;
                }

                // No caret query on a PasswordBox: where its render scope starts is where the text does.
                var host = box.Template.FindName("PART_ContentHost", box) as ScrollViewer;
                var origin = (host?.Content as FrameworkElement)?.TranslatePoint(new Point(0, 0), box).X ?? double.NaN;
                Line(window, "PasswordBox", false, Rect.Empty, new Point(double.NaN, double.NaN), origin, box.BorderThickness, box.Padding, box.CaretBrush);
            }
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(DebugOverrides.ProbeDirectory!, "caret.log"), log.ToString());
    }

    /// <summary>
    /// 探针命令 tooltip（用户需求 2026-10-05：全应用的悬停提示换成拾语的样式——小圆角、
    /// 跟随浅色/深色主题）。在设置窗里挑提示文字最长的那个元素，照它的提示开一个真的
    /// ToolTip（不动鼠标），量外观；再把主题切到深色量一次——颜色走 DynamicResource
    /// 才会跟着变。两次各渲一张 PNG 进数据目录，供人眼看。
    /// </summary>
    private async void ProbeTooltipLook(AppShell shell)
    {
        _modules!.Settings.Show();
        for (var round = 0; round < 6; round++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        var log = new StringBuilder();
        var target = Windows.OfType<SettingsWindow>()
            .SelectMany(window => Descendants(window).OfType<FrameworkElement>())
            .Where(element => element.IsVisible && element.ToolTip is string)
            .OrderByDescending(element => ((string)element.ToolTip).Length)
            .FirstOrDefault();

        if (target is not null)
        {
            var tip = new ToolTip
            {
                Content = target.ToolTip,
                PlacementTarget = target,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            };
            tip.IsOpen = true;

            async Task Note(string phase)
            {
                for (var round = 0; round < 6; round++)
                {
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                }

                // The face is the bordered layer; the shadow layer behind it has no stroke.
                var face = Descendants(tip).OfType<Border>().FirstOrDefault(border => border.BorderThickness.Left > 0);
                string Hex(object? brush) => (brush as SolidColorBrush)?.Color.ToString() ?? "none";
                log.AppendLine(FormattableString.Invariant(
                    $"tip|{phase}|bg={Hex(face?.Background)}|bgExpect={Hex(FindResource("Brush.LayerFlyout"))}|stroke={Hex(face?.BorderBrush)}|strokeExpect={Hex(FindResource("Brush.Border"))}|radius={face?.CornerRadius.TopLeft}|radiusExpect={((CornerRadius)FindResource("Radius.Control")).TopLeft}|font={tip.FontSize}|fontExpect={FindResource("Type.Caption")}|systemShadow={tip.HasDropShadow}|width={tip.ActualWidth:F0}"));

                var scale = 1.5;
                var width = Math.Max(1, tip.ActualWidth);
                var height = Math.Max(1, tip.ActualHeight);
                var sheet = new DrawingVisual();
                using (var context = sheet.RenderOpen())
                {
                    context.DrawRectangle((Brush)FindResource("Brush.Background"), null, new Rect(0, 0, width, height));
                    context.DrawRectangle(new VisualBrush(tip), null, new Rect(0, 0, width, height));
                }

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(sheet);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(DebugOverrides.ProbeDirectory!, $"tooltip-{phase}.png"));
                encoder.Save(stream);
            }

            await Note("light");
            shell.TryUpdateSettings(settings => settings with { Theme = Shiyu.Core.AppTheme.Dark });
            await Note("dark");
            tip.IsOpen = false;
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(DebugOverrides.ProbeDirectory!, "tooltip.log"), log.ToString());
    }

    /// <summary>
    /// 探针命令 translation-log（用户需求 2026-10-09：两个框都能拖、翻译记下来、按周期清空）。
    /// 面板译一句；反向输入框译一句、挪到远处让它长高、再按 Esc 关掉——两条都该进记录，来处与
    /// 模板记对。然后管理窗开在「翻译记录」页、设置窗落到「翻译记录」一节，各渲一张 PNG。
    /// 配 SHIYU_FAKE_BACKEND=1（"[EN] " + 原文，不碰网络）。日志一行一事，值里不带换行。
    /// </summary>
    private async void ProbeTranslationLog(AppShell shell)
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();

        async Task<bool> Until(Func<bool> condition, int timeoutMs)
        {
            for (var waited = 0; waited < timeoutMs; waited += 100)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return condition();
        }

        async Task Settle()
        {
            for (var round = 0; round < 6; round++)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
        }

        string Newest()
        {
            var newest = shell.Store.TranslationLog(limit: 1).FirstOrDefault();
            return FormattableString.Invariant(
                $"records={shell.Store.CountTranslationLog()}|origin={newest?.Origin}|template={newest?.Template}|original={Shiyu.Core.TranslationLogText.OneLine(newest?.Original ?? string.Empty)}|translated={Shiyu.Core.TranslationLogText.OneLine(newest?.Translated ?? string.Empty)}");
        }

        try
        {
            _modules!.Translation.ShowPanel("probe rollout plan");
            log.AppendLine($"panel|logged={await Until(() => shell.Store.CountTranslationLog() >= 1, 10000)}|{Newest()}");
            log.Append(_modules!.Translation.ProbePanelGrips());

            log.Append(await _modules!.ReverseInput.ProbeTranslationLog("probe reverse sentence"));
            log.AppendLine($"reverse|logged={await Until(() => shell.Store.CountTranslationLog() >= 2, 3000)}|{Newest()}");

            shell.ShowTranslationLog?.Invoke();
            await Until(() => Windows.OfType<LibraryWindow>().Any(window => window.IsLoaded), 5000);
            await Settle();
            log.Append(Windows.OfType<LibraryWindow>().FirstOrDefault() is { } library
                ? library.ProbeLogPage(Path.Combine(directory, "translation-log-library.png"))
                : "library|missing" + Environment.NewLine);

            _modules!.Settings.ShowAt("translate.log");
            await Until(() => Windows.OfType<SettingsWindow>().Any(window => window.IsLoaded), 5000);
            await Settle();
            if (Windows.OfType<SettingsWindow>().FirstOrDefault() is { } settings)
            {
                var expected = $"共 {shell.Store.CountTranslationLog()} 条";
                var shown = Descendants(settings).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == expected);
                log.AppendLine(FormattableString.Invariant($"settings|count={shown}|expected={expected}"));
                if (settings.Content is FrameworkElement settingsRoot)
                {
                    RenderToPng(settingsRoot, Path.Combine(directory, "translation-log-settings.png"));
                }
            }
            else
            {
                log.AppendLine("settings|missing");
            }
        }
        catch (Exception failure)
        {
            // expected: 探针把异常写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "translation-log.log"), log.ToString());
    }

    /// <summary>
    /// 探针命令 content-size（ADR-0012 排版 2–3，用户需求 2026-10-09）：被阅读的文字跟着
    /// 内容字号变，控件文字不变。打开窄条与反向输入框，标准档量一次，切到「特大」再量一次：
    /// 窄条卡片正文与行高、反向输入框（紧凑档）随设置变，窄条搜索框（控件）始终是 14。
    /// 特大档各渲一张 PNG 进数据目录，供人眼看有没有挤坏。
    /// </summary>
    private async void ProbeContentSize(AppShell shell)
    {
        shell.ToggleBar?.Invoke();
        shell.ShowReverseInput?.Invoke();

        async Task Settle()
        {
            for (var round = 0; round < 6; round++)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
        }

        var log = new StringBuilder();
        void Note(string phase)
        {
            var bar = Windows.OfType<BarWindow>().FirstOrDefault();
            var card = bar is null ? null : Descendants(bar).OfType<EntryBodyText>().FirstOrDefault(text => text.IsVisible);
            var search = bar?.FindName("SearchBox") as TextBox;
            var reverse = Windows.OfType<ReverseInputWindow>().FirstOrDefault();
            var input = reverse?.FindName("InputBox") as TextBox;
            var output = reverse?.FindName("OutputText") as TextBlock;
            log.AppendLine(FormattableString.Invariant(
                $"size|{phase}|card={card?.FontSize}|cardLine={card?.LineHeight}|search={search?.FontSize}|reverseInput={input?.FontSize}|reverseOutput={output?.FontSize}"));
        }

        await Settle();
        Note("standard");

        shell.TryUpdateSettings(settings => settings with { ContentFontSize = Shiyu.Core.ContentFontSize.Larger });
        await Settle();
        Note("larger");

        var directory = DebugOverrides.ProbeDirectory!;
        if (Windows.OfType<BarWindow>().FirstOrDefault()?.Content is FrameworkElement barRoot)
        {
            RenderToPng(barRoot, Path.Combine(directory, "content-larger-bar.png"));
        }

        if (Windows.OfType<ReverseInputWindow>().FirstOrDefault()?.Content is FrameworkElement reverseRoot)
        {
            RenderToPng(reverseRoot, Path.Combine(directory, "content-larger-reverse.png"));
        }

        // 「较小」一档（用户需求 2026-10-10）：最小的一头同样量一次、各渲一张。
        shell.TryUpdateSettings(settings => settings with { ContentFontSize = Shiyu.Core.ContentFontSize.Smaller });
        await Settle();
        Note("smaller");

        if (Windows.OfType<BarWindow>().FirstOrDefault()?.Content is FrameworkElement smallBar)
        {
            RenderToPng(smallBar, Path.Combine(directory, "content-smaller-bar.png"));
        }

        if (Windows.OfType<ReverseInputWindow>().FirstOrDefault()?.Content is FrameworkElement smallReverse)
        {
            RenderToPng(smallReverse, Path.Combine(directory, "content-smaller-reverse.png"));
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "content-size.log"), log.ToString());
    }

    /// <summary>A window's content over the theme background, at 1.5×, into a PNG.</summary>
    internal static void RenderToPng(FrameworkElement root, string path)
    {
        if (root.ActualWidth <= 0 || root.ActualHeight <= 0)
        {
            return;
        }

        const double scale = 1.5;
        var area = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        var sheet = new DrawingVisual();
        using (var context = sheet.RenderOpen())
        {
            context.DrawRectangle((Brush)Current.FindResource("Brush.Background"), null, area);
            context.DrawRectangle(new VisualBrush(root), null, area);
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(area.Width * scale), (int)Math.Ceiling(area.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(sheet);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>
    /// 探针的呼出通道（票 15）：热键一颗都没注册，所以窗口只能这样开——
    /// <c>SHIYU_PROBE_CMD=bar|panel|library|settings|quickbar|reverse</c> 启动即直接
    /// 显示对应窗口，走的是和热键完全相同的内部方法。
    /// <c>SHIYU_PROBE_ITEM</c> 让设置窗直接落到某个设置项（既有深链）；
    /// <c>SHIYU_PROBE_TEXT</c> 给面板一句要翻译的话。仅在探针模式生效。
    /// </summary>
    private void RunProbeCommand()
    {
        if (_shell is not { } shell || !DebugOverrides.IsProbe)
        {
            return;
        }

        var text = DebugOverrides.ProbeText;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Placeholder sentence for probe 15.";
        }

        switch (DebugOverrides.ProbeCommand)
        {
            case "bar":
                shell.ToggleBar?.Invoke();
                break;

            // 光标与占位同一起点、光标用 Accent（用户实录 2026-10-05）。
            case "caret":
                ProbeCaretGeometry(shell);
                break;

            // 悬停提示是拾语的样子、跟随主题（用户需求 2026-10-05）。
            case "tooltip":
                ProbeTooltipLook(shell);
                break;

            // 内容字号：被阅读的文字跟着变，控件文字不变（用户需求 2026-10-09）。
            case "content-size":
                ProbeContentSize(shell);
                break;

            // 反向输入框的译文能用鼠标选取复制（用户需求 2026-10-05）。配 SHIYU_FAKE_BACKEND=1。
            case "reverse-select":
                // Long enough to wrap: the line pitch is only measurable on two lines.
                _modules!.ReverseInput.ProbeSelectableOutput(
                    "hello probe, this sentence runs long enough to wrap onto a second line of the reverse box");
                break;

            // 把列表翻到底后重开、外部写、粘贴呼出：都必须回到最新的那条
            // （用户实录 2026-10-04：打开窄条有时停在最底部）。
            case "bar-newest":
                _modules!.Bar.ProbeStartsAtNewest();
                break;

            // 按住空格预览图片：不抖、按屏幕像素清楚（用户实录 2026-10-09）。脚本负责按键。
            case "preview-image":
                _modules!.Bar.ProbePreviewImage();
                break;

            case "quickbar":
                // 票 26 合并后 quickbar 命令映射到粘贴模式的窄条：探针仍能
                // 检"快速粘贴"这条意图，定位参数（384 DIP）在探针脚本里同步。
                shell.ShowQuickPaste?.Invoke();
                break;

            case "library":
                shell.ShowLibrary?.Invoke();
                break;

            // 自备密钥每家各存一份、打码显示、选中即切换（用户需求 2026-10-10）。
            case "credentials":
                _modules!.Settings.ProbeCredentials();
                break;

            case "settings":
                if (DebugOverrides.ProbeItem is { } item)
                {
                    _modules!.Settings.ShowAt(item);
                }
                else
                {
                    _modules!.Settings.Show();
                }
                break;

            case "panel":
                _modules!.Translation.ShowPanel(text);
                break;

            // 翻译面板像反向输入框一样挑模板（用户需求 2026-10-09）。配 SHIYU_FAKE_BACKEND=1。
            case "panel-templates":
                _modules!.Translation.ProbeTemplatePicker("hello panel templates");
                break;

            // 两个框都能拖、翻译记下来（用户需求 2026-10-09）。配 SHIYU_FAKE_BACKEND=1。
            case "translation-log":
                ProbeTranslationLog(shell);
                break;

            // 票 43：反向输入框。探针没有全局热键，所以直接开；脚本在此之前把记事本置于前台——
            // 框记下的"原来的窗口"就是那时的前台。配 SHIYU_FAKE_BACKEND=1 用确定性的假后端。
            case "reverse":
                shell.ShowReverseInput?.Invoke();
                break;

            // 票 25：引导五屏的走查通道。探针原则上跳过首启引导（票 15），
            // 但 §5.3 的验收要真走一遍五屏——显式点名才打开，与 settings
            // 同族；数据落在本探针目录里，走完即记 OnboardingCompleted。
            case "onboarding":
                new OnboardingWindow(shell.SettingsStore, firstRun: true, shell).Show();
                break;
        }
    }
}
#endif
