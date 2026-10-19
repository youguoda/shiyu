#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
        _modules!.Bar.ProbeShowPinned();
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

    /// <summary>此刻所有顶层窗口的句柄（找「新出现的记事本窗口」用）。</summary>
    private static System.Collections.Generic.HashSet<System.IntPtr> KnownWindowHandles()
    {
        var handles = new System.Collections.Generic.HashSet<System.IntPtr>();
        foreach (System.Windows.Automation.AutomationElement window in System.Windows.Automation.AutomationElement
            .RootElement.FindAll(
                System.Windows.Automation.TreeScope.Children,
                System.Windows.Automation.Condition.TrueCondition))
        {
            try
            {
                handles.Add((System.IntPtr)window.Current.NativeWindowHandle);
            }
            catch (System.Windows.Automation.ElementNotAvailableException)
            {
                // expected: 窗口正在消失。
            }
        }

        return handles;
    }

    /// <summary>
    /// 探针命令 batch-paste 的入口（评审 2026-10-16 F-12 的验收，用户委托 2026-10-17；**会动真剪贴板**，
    /// 脚本不进 run-all）：起一个空记事本置前台，开历史窗口（贴回目标记成记事本），库里造三条 ASCII 条目，
    /// 按非列表顺序选中、点「粘贴全部」，读回记事本文字比对顺序。目标不是前台就收手不贴——宁可不验，
    /// 不往一个不知哪来的窗口里贴。
    /// </summary>
    private async void ProbeBatchPaste(AppShell shell)
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();
        // Win11 的记事本是打包应用：新进程的 Process.MainWindowHandle 一直是零（实测 2026-10-17）。
        // 窗口按「启动之后新出现的、标题以 Notepad 结尾的顶层窗口」找——UIA 看得见它（UIA spike 已证）。
        var before = KnownWindowHandles();
        System.Diagnostics.Process? launcher = null;
        try
        {
            launcher = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // expected: 起不来——下面等不到窗口，按计划收手。
        }

        try
        {
            var target = Shiyu.Windows.ForegroundWindow.Current();
            var haveNotepadWindow = false;
            for (var waited = 0; waited < 100 && !haveNotepadWindow; waited++)
            {
                await Task.Delay(200);
                foreach (System.Windows.Automation.AutomationElement window in System.Windows.Automation.AutomationElement
                    .RootElement.FindAll(
                        System.Windows.Automation.TreeScope.Children,
                        System.Windows.Automation.Condition.TrueCondition))
                {
                    try
                    {
                        var name = window.Current.Name;
                        if (!name.EndsWith("notepad", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var handle = (IntPtr)window.Current.NativeWindowHandle;
                        if (handle != IntPtr.Zero && !before.Contains(handle))
                        {
                            target = new Shiyu.Windows.ForegroundWindow(handle);
                            haveNotepadWindow = true;
                            break;
                        }

                        // 句柄拿不到（窗口挂在别人名下）：让 UIA 把焦点交给它，再取此刻的前台。
                        if (handle == IntPtr.Zero)
                        {
                            window.SetFocus();
                            await Task.Delay(400);
                            var foreground = Shiyu.Windows.ForegroundWindow.Current();
                            if (foreground.IsSomething && !foreground.BelongsToThisProcess)
                            {
                                target = foreground;
                                haveNotepadWindow = true;
                                break;
                            }
                        }
                    }
                    catch (System.Windows.Automation.ElementNotAvailableException)
                    {
                        // expected: 窗口正在出现或消失的半路上。
                    }
                }
            }

            // 没拿到真记事本的窗口（新版记事本的启动器把它并进了已开的实例）就收手：
            // 继续做会把这三个字贴进"此刻的前台"，那扇窗是谁没人知道。
            if (!haveNotepadWindow)
            {
                log.AppendLine("target|ok=False|reason=no-notepad-window");
                return;
            }

            // 抢前台是和用户的手赛跑（settings-front 探针的同款 flake）：试三次。
            var okTarget = false;
            for (var attempt = 0; attempt < 3 && !okTarget; attempt++)
            {
                target.Restore();
                await Task.Delay(400);
                okTarget = target.IsSomething
                    && !target.BelongsToThisProcess
                    && Shiyu.Windows.ForegroundWindow.Current().Handle == target.Handle;
            }

            log.AppendLine(FormattableString.Invariant($"target|ok={okTarget}"));
            if (!okTarget)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var first = shell.Store.Append("probe paste one", "notepad-probe", now);
            var third = shell.Store.Append("probe paste three", "notepad-probe", now.AddSeconds(1));
            var second = shell.Store.Append("probe paste two", "notepad-probe", now.AddSeconds(2));

            _modules!.Library.ShowFor(target);
            for (var waited = 0; waited < 50 && Windows.OfType<LibraryWindow>().All(window => !window.IsLoaded); waited++)
            {
                await Task.Delay(100);
            }

            await Task.Delay(800);
            if (Windows.OfType<LibraryWindow>().FirstOrDefault() is not { } library)
            {
                log.AppendLine("library|missing");
                return;
            }

            // 列表最新在前（two, three, one）；选中顺序故意 one → three → two。
            log.Append(await library.ProbeBatchPaste(
                [first.Id, third.Id, second.Id],
                string.Join("\r\n", "probe paste one", "probe paste three", "probe paste two"),
                target.Handle,
                () => shell.CapturePlatform?.ReadClipboardText()));
        }
        catch (Exception failure)
        {
            // expected: 探针把意外写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }
        finally
        {
            // 只关这一趟起的记事本进程（若被并进用户已开的实例，launcher 已退出，这里什么都不杀；
            // 那个页签留在用户的记事本里，里面就是被贴进去的三行）。日志无论如何都落地。
            try
            {
                if (launcher is { HasExited: false } process)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // expected: 已经退出了。
            }

            log.AppendLine("done");
            File.WriteAllText(Path.Combine(directory, "batch-paste.log"), log.ToString());
        }
    }

    /// <summary>
    /// 探针命令 overlay-pin（用户需求 2026-10-19：反向输入框与翻译面板加钉，钉住就不自动收起）：
    /// 面板与反向框各走一遍——钉上，焦点被别处夺走 / 按 Esc，核对每一步之后的可见性。
    /// 判断在应用里做完，日志只有真假。
    /// </summary>
    private async void ProbeOverlayPin()
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();
        var elsewhere = new Window
        {
            Width = 120,
            Height = 80,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
        };
        try
        {
            // 面板：假后端出一句译文 → 钉上 → Esc 第一击（取消钉，面板留下）→ 第二击（关）。
            _modules!.Translation.ShowPanel("probe overlay pin");
            for (var waited = 0; waited < 50 && Windows.OfType<PanelWindow>().All(window => !window.IsVisible); waited++)
            {
                await Task.Delay(100);
            }

            if (Windows.OfType<PanelWindow>().FirstOrDefault() is { } panel)
            {
                await Task.Delay(600);
                var pinnedVisible = panel.IsVisible;
                panel.ProbeClickPin();
                await Task.Delay(200);
                panel.ProbeEscape();
                await Task.Delay(200);
                var surviveEsc1 = panel.IsVisible;
                panel.ProbeEscape();
                await Task.Delay(300);
                log.AppendLine(FormattableString.Invariant(
                    $"panel|pinnedVisible={pinnedVisible}|surviveEsc1={surviveEsc1}|closedAfterEsc2={!panel.IsVisible}"));
            }
            else
            {
                log.AppendLine("panel|missing");
            }

            // 反向框：呼出（夺走焦点的是它自己）→ 钉上 → 让别处的窗口抢走焦点 → 仍在。
            elsewhere.Show();
            elsewhere.Activate();
            await Task.Delay(300);
            _shell!.ShowReverseInput?.Invoke();
            for (var waited = 0; waited < 50 && Windows.OfType<ReverseInputWindow>().All(window => !window.IsVisible); waited++)
            {
                await Task.Delay(100);
            }

            if (Windows.OfType<ReverseInputWindow>().FirstOrDefault() is { } reverse)
            {
                await Task.Delay(400);
                reverse.ProbeClickPin();
                await Task.Delay(200);
                elsewhere.Activate();
                await Task.Delay(500);
                var surviveFocusLoss = reverse.IsVisible;
                reverse.ProbeEscapeKey();
                await Task.Delay(200);
                var surviveEsc1 = reverse.IsVisible;
                reverse.ProbeEscapeKey();
                await Task.Delay(300);
                log.AppendLine(FormattableString.Invariant(
                    $"reverse|surviveFocusLoss={surviveFocusLoss}|surviveEsc1={surviveEsc1}|closedAfterEsc2={!reverse.IsVisible}"));
            }
            else
            {
                log.AppendLine("reverse|missing");
            }
        }
        catch (Exception failure)
        {
            // expected: 探针把意外写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }
        finally
        {
            log.AppendLine("done");
            File.WriteAllText(Path.Combine(directory, "overlay-pin.log"), log.ToString());
        }
    }

    /// <summary>
    /// 探针命令 preview-pin 的入口（用户需求 2026-10-19：预览弹窗加钉，钉住不自动收起、
    /// 只留干净内容。验收用）：开窄条（种子数据已备），走真实悬停路径出预览 → 钉上 →
    /// 模拟收起被挡 → 取消钉收场。判断在应用里做完，日志只有真假。
    /// </summary>
    private async void ProbePreviewPin()
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();
        try
        {
            _modules!.Bar.ShowQuickPaste();
            for (var waited = 0; waited < 50 && Windows.OfType<BarWindow>().All(window => !window.IsVisible); waited++)
            {
                await Task.Delay(100);
            }

            await Task.Delay(600);
            log.Append(await _modules.Bar.ProbePreviewPin());
        }
        catch (Exception failure)
        {
            // expected: 探针把意外写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }
        finally
        {
            log.AppendLine("done");
            File.WriteAllText(Path.Combine(directory, "preview-pin.log"), log.ToString());
        }
    }

    /// <summary>
    /// 探针命令 folder-open-locate 的入口（用户实录 2026-10-19：复制的是文件夹时，「打开」与
    /// 「定位」没有反应。复现用）：造一条指向临时**文件夹**的文件条目，开窄条，按打开/定位的解析
    /// 原样走一遍。不真开资源管理器、不碰剪贴板。
    /// </summary>
    private async void ProbeFolderOpenLocate()
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();
        try
        {
            var shell = _shell!;
            var folder = System.IO.Path.Combine(directory, "probe-folder", "nested");
            System.IO.Directory.CreateDirectory(folder);
            var entry = shell.Store.AppendFiles([folder], "probe", DateTimeOffset.UtcNow);

            _modules!.Bar.ShowQuickPaste();
            for (var waited = 0; waited < 50 && Windows.OfType<BarWindow>().All(window => !window.IsLoaded); waited++)
            {
                await Task.Delay(100);
            }

            await Task.Delay(800);
            if (Windows.OfType<BarWindow>().FirstOrDefault() is not { } bar)
            {
                log.AppendLine("bar|missing");
                return;
            }

            log.Append(bar.ProbeFolderOpenLocate(entry.Id));
        }
        catch (Exception failure)
        {
            // expected: 探针把意外写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }
        finally
        {
            log.AppendLine("done");
            File.WriteAllText(Path.Combine(directory, "folder-open-locate.log"), log.ToString());
        }
    }

    /// <summary>
    /// 探针命令 image-paste 的入口（用户实录 2026-10-19：窄条图片卡按 Enter，贴出的是
    /// 「图片 宽×高」标签。复现用；会动真剪贴板，脚本不进 run-all）：先在探测库里造一条真图片
    /// （缩略图 + 原图 + 尺寸），再开窄条、选中它、走 PasteEntry，读回剪贴板核对。
    /// </summary>
    private async void ProbeImagePaste()
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();
        try
        {
            var shell = _shell!;
            var now = DateTimeOffset.UtcNow;

            var visual = new System.Windows.Media.DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, 800, 500));
                context.DrawRectangle(System.Windows.Media.Brushes.SteelBlue, null, new System.Windows.Rect(40, 40, 720, 420));
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(800, 500, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var pngStream = new MemoryStream();
            encoder.Save(pngStream);
            var png = pngStream.ToArray();

            var path = shell.Images.Save(png, now);
            var entry = shell.Store.AppendImage($"图片 {800}×{500}", png, path, "probe", now, 800, 500);

            _modules!.Bar.ShowQuickPaste();
            for (var waited = 0; waited < 50 && Windows.OfType<BarWindow>().All(window => !window.IsLoaded); waited++)
            {
                await Task.Delay(100);
            }

            await Task.Delay(800);
            if (Windows.OfType<BarWindow>().FirstOrDefault() is not { } bar)
            {
                log.AppendLine("bar|missing");
                return;
            }

            log.Append(await bar.ProbeImagePaste(entry.Id, $"图片 {800}×{500}"));
        }
        catch (Exception failure)
        {
            // expected: 探针把意外写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }
        finally
        {
            log.AppendLine("done");
            File.WriteAllText(Path.Combine(directory, "image-paste.log"), log.ToString());
        }
    }

    /// <summary>
    /// 探针命令 degraded-shell（评审 F-13 的 Win10 降级外观；用户在 Win10 虚拟机实录 2026-10-17：
    /// 三扇壳窗透出桌面）：在 Win11 上强制走 <see cref="Backdrop.DegradeShell"/>（不透明底 + 自绘描边
    /// + 阴影），渲一张窄条 PNG 给人眼核对。产品在 Win10 上走的就是这条路——DWM 拒收材质即触发。
    /// </summary>
    private async void ProbeDegradedShell()
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();
        try
        {
            _modules!.Bar.ShowQuickPaste();
            for (var waited = 0; waited < 50 && Windows.OfType<BarWindow>().All(window => !window.IsLoaded); waited++)
            {
                await Task.Delay(100);
            }

            if (Windows.OfType<BarWindow>().FirstOrDefault() is not { } bar)
            {
                log.AppendLine("bar|missing");
                return;
            }

            await Task.Delay(600);
            Backdrop.DegradeShell(bar.Shell);
            await Task.Delay(300);

            if (bar.Content is FrameworkElement root)
            {
                App.RenderToPng(root, Path.Combine(directory, "degraded-bar.png"));
            }

            var fill = bar.Shell.Background;
            var opaque = fill is System.Windows.Media.SolidColorBrush brush && brush.Color.A == 255;
            log.AppendLine(FormattableString.Invariant(
                $"degraded|margin={bar.Shell.Margin.Left == Backdrop.FallbackMargin}|stroke={bar.Shell.BorderThickness.Left == 1}|opaque={opaque}"));
        }
        catch (Exception failure)
        {
            // expected: 探针把意外写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "degraded-shell.log"), log.ToString());
    }

    /// <summary>
    /// 探针命令 vocabulary（评审 2026-10-16 F-09 的验收，用户委托 2026-10-17）：收三个词的星
    /// （走 TranslationLogger.StarWord 与真的离线词典卡），「生词」筛选该有三条，历史窗口里点开
    /// 「生词」列表也只有三条，导出的 CSV 带 Anki 文件头、三个词头。判断在应用里做完，日志只有
    /// 真假与词头（词头本身是 ASCII，脚本只认 ASCII）。
    /// </summary>
    private async void ProbeVocabulary(AppShell shell)
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();
        try
        {
            var local = TranslationModule.LocalWords;
            var logger = shell.TranslationLog;
            foreach (var word in new[] { "example", "went", "ubiquitous" })
            {
                if (local.Lookup(word) is not { } card)
                {
                    log.AppendLine(FormattableString.Invariant($"star|word={word}|card=False"));
                    continue;
                }

                logger.StarWord(word, "probe", "标准", card);
            }

            var words = shell.Store.TranslationLog(vocabularyOnly: true);
            log.AppendLine(FormattableString.Invariant(
                $"filter|count={words.Count}|headwords={string.Join(",", words.Select(record => record.Word?.Headword))}"));

            shell.ShowTranslationLog?.Invoke();
            for (var waited = 0; waited < 50 && Windows.OfType<LibraryWindow>().All(window => !window.IsLoaded); waited++)
            {
                await Task.Delay(100);
            }

            if (Windows.OfType<LibraryWindow>().FirstOrDefault() is not { } library)
            {
                log.AppendLine("library|missing");
            }
            else
            {
                await Task.Delay(800);
                library.RaiseVocabularyToggle();
                await Task.Delay(400);
                log.AppendLine(FormattableString.Invariant($"toggle|count={library.ProbeVocabularyListCount()}"));
            }

            var csv = Path.Combine(directory, "vocabulary.csv");
            int written;
            using (var writer = new StreamWriter(csv, append: false, Shiyu.Core.ExportFile.EncodingFor(Shiyu.Core.ExportFormat.Csv)))
            {
                written = Shiyu.Core.VocabularyExport.Write(
                    writer, shell.Store.TranslationLog(vocabularyOnly: true, limit: int.MaxValue), Shiyu.Core.ExportFormat.Csv);
            }

            var content = File.ReadAllText(csv);
            var csvLine =
                $"csv|written={written}|header={content.StartsWith("#separator:Comma", StringComparison.Ordinal)}"
                + $"|example={content.Contains("example,", StringComparison.Ordinal)}"
                + $"|go={content.Contains("go,", StringComparison.Ordinal)}"
                + $"|ubiquitous={content.Contains("ubiquitous,", StringComparison.Ordinal)}";
            log.AppendLine(csvLine);
        }
        catch (Exception failure)
        {
            // expected: 探针把异常写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "vocabulary.log"), log.ToString());
    }

    /// <summary>
    /// 探针命令 auto-backup（评审 2026-10-16 F-20）：在探针自己的目录下摆一个备份文件夹——5 份旧的自动备份、
    /// 1 份手动导出的、1 个别的文件——设好口令，按「立即备份一次」：该多出一份新的、是加密的；自动备份只剩
    /// 4 份；手动导出的和别的文件原样在。再把口令换成本机打不开的，应当报出人话、不多出文件。日志一行一事。
    /// </summary>
    private async void ProbeAutoBackup(AppShell shell)
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();
        try
        {
            var folder = Path.Combine(directory, "auto-backup");
            Directory.CreateDirectory(folder);
            var now = DateTime.Now;
            for (var week = 1; week <= 5; week++)
            {
                File.WriteAllText(Path.Combine(folder, Shiyu.Core.AutoBackupSchedule.FileName(now.AddDays(-7 * week))), "old");
            }

            File.WriteAllText(Path.Combine(folder, "拾语备份 2026-01-01.shiyubk"), "manual");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "keep me");

            var password = Encoding.Unicode.GetBytes("probe password");
            var sealedPassword = Shiyu.Windows.BackupPasswordVault.Protect(password) ?? string.Empty;
            Array.Clear(password);
            shell.TryUpdateSettings(settings => settings with
            {
                AutoBackupEnabled = true,
                AutoBackupFolder = folder,
                AutoBackupKeep = 4,
                AutoBackupPassword = sealedPassword,
            });

            var outcome = await shell.AutoBackup!.RunAsync(manual: true);
            var names = Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToList();
            var ours = names.Where(name => Shiyu.Core.AutoBackupSchedule.TryReadStamp(name, out _)).ToList();
            var newest = Shiyu.Core.AutoBackupSchedule.Newest(ours);
            var fresh = newest is { } stamp && now - stamp < TimeSpan.FromMinutes(5);
            var head = new byte[8];
            if (newest is { } latest)
            {
                using var stream = File.OpenRead(Path.Combine(folder, Shiyu.Core.AutoBackupSchedule.FileName(latest)));
                _ = stream.Read(head, 0, head.Length);
            }

            log.AppendLine(FormattableString.Invariant(
                $"run|ok={outcome.StartsWith("已备份", StringComparison.Ordinal)}|fresh={fresh}|ours={ours.Count}|sealed={Encoding.ASCII.GetString(head) == "SHIYUBK1"}|manual={names.Contains("拾语备份 2026-01-01.shiyubk")}|other={names.Contains("notes.txt")}"));

            // 验收补一刀（2026-10-17，用户委托）：把这份加密备份导进一个空库，条目该原样回来。
            if (newest is { } backupStamp)
            {
                var importDatabase = Path.Combine(directory, "import-check.db");
                var importImages = Path.Combine(directory, "import-images");
                Directory.CreateDirectory(importImages);
                using (var importStore = Shiyu.Core.EntryStore.Open(importDatabase))
                {
                    var importPassword = Encoding.Unicode.GetBytes("probe password");
                    try
                    {
                        var restored = Shiyu.Core.BackupArchive.Import(
                            Path.Combine(folder, Shiyu.Core.AutoBackupSchedule.FileName(backupStamp)),
                            importStore, importImages, overwrite: true, importPassword);
                        log.AppendLine(FormattableString.Invariant(
                            $"import|added={restored.Added}|images={restored.ImagesRestored}|entriesInStore={importStore.Count()}"));
                    }
                    finally
                    {
                        Array.Clear(importPassword);
                    }
                }
            }

            // 本机打不开的口令（另一台电脑保护的）：不写文件，说人话。
            shell.TryUpdateSettings(settings => settings with { AutoBackupPassword = Shiyu.Core.AppSettings.SecretMarker + "AAAA" });
            var before = Directory.EnumerateFiles(folder).Count();
            var refused = await shell.AutoBackup!.RunAsync(manual: true);
            log.AppendLine(FormattableString.Invariant(
                $"locked|said={refused.Contains("口令", StringComparison.Ordinal)}|unchanged={Directory.EnumerateFiles(folder).Count() == before}"));
        }
        catch (Exception failure)
        {
            // expected: 探针把异常写进自己的日志（脚本据此判 FAIL），吞下是为了日志照样落地。
            log.AppendLine(FormattableString.Invariant($"error|type={failure.GetType().Name}"));
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "auto-backup.log"), log.ToString());
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
    /// <summary>
    /// 探针命令 source-icons（用户需求 2026-10-14：来源图标可以关；关掉或取不到图标时那一格什么
    /// 都不放）。种子数据里谁都没有图标：先给 probe-browser 存一枚，窄条与管理窗里就有"有图标"和
    /// "取不到"两种行。开着看一遍，经 store 关掉再看一遍；关着时按住 Ctrl 的数字键帽仍浮在行首。
    /// </summary>
    private async void ProbeSourceIcons(AppShell shell)
    {
        var log = new StringBuilder();

        static async Task Settle(int milliseconds)
        {
            await Task.Delay(milliseconds);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        shell.Store.SaveApplicationIcon("probe-browser", ProbeIconPng());

        _modules!.Bar.ProbeShowPinned();
        await Settle(1200);
        log.Append(_modules.Bar.ProbeIconSlots("on"));

        _modules.Library.Show();
        await Settle(1200);
        log.Append(_modules.Library.ProbeIconSlots("on"));

        shell.TryUpdateSettings(settings => settings with { ShowSourceIcons = false });
        await Settle(800);
        log.Append(_modules.Bar.ProbeIconSlots("off"));
        log.Append(_modules.Library.ProbeIconSlots("off"));
        log.Append(await _modules.Bar.ProbeKeycap());

        File.WriteAllText(
            Path.Combine(DebugOverrides.ProbeDirectory!, "source-icons.log"), log + "done" + Environment.NewLine);

        // 一枚 32 像素的实心圆角方块，当作 probe-browser 的应用图标。
        static byte[] ProbeIconPng()
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromRgb(0x2B, 0x7C, 0xD3)), null, new Rect(0, 0, 32, 32), 6, 6);
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    /// <summary>
    /// 探针命令 badge（用户需求 2026-10-10："翻译这段"去掉，只留一个「译」）：把翻译徽标亮出来，
    /// 量它的尺寸与圆角（2026-10-13 起是圆角方形）、数它里面有几段字，渲一张 PNG。
    /// </summary>
    private async void ProbeBadge(AppShell shell)
    {
        shell.ShowBadge?.Invoke("probe badge text", null);
        for (var round = 0; round < 6; round++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();
        if (Windows.OfType<BadgeWindow>().FirstOrDefault(window => window.IsVisible) is { } badge
            && badge.Content is Border tile)
        {
            var texts = Descendants(badge).OfType<TextBlock>().Where(text => text.IsVisible).ToList();
            log.AppendLine(FormattableString.Invariant(
                $"badge|width={tile.ActualWidth:F0}|height={tile.ActualHeight:F0}|radius={tile.CornerRadius.TopLeft}|radiusExpect={((CornerRadius)FindResource("Radius.Overlay")).TopLeft}|texts={texts.Count}|glyph={(texts.Count == 1 && texts[0].Text == "译")}"));
            RenderToPng(tile, Path.Combine(directory, "badge.png"));
        }
        else
        {
            log.AppendLine("badge|missing");
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "badge.log"), log.ToString());
    }

    private async void ProbeContentSize(AppShell shell)
    {
        _modules!.Bar.ProbeShowPinned();
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
            var output = reverse?.FindName("OutputDoc") as MarkdownBox;
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

    /// <summary>
    /// 探针命令 scroll-lane（用户实录 2026-10-10：指到滚动条滑块时它变宽，旁边的控件跟着变窄、
    /// 来回伸缩）。先查样式本身：任何 IsMouseOver 触发器都不许碰滚动条的 Width/Height。再量钉住
    /// 的窄条与设置窗（翻译页，够长）的滚动条：道宽、滑块宽与它离道边的距离、内容区宽。然后把
    /// 设置窗滑块的屏幕中心写进 scroll-lane.ready，等脚本把真鼠标移上去（用户在场时脚本不动
    /// 鼠标，这一步如实记"没悬停"），悬停时再量一次：内容区一分不能变。
    /// </summary>
    private async void ProbeScrollLane()
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();

        async Task Settle()
        {
            for (var round = 0; round < 6; round++)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
        }

        static IEnumerable<SetterBase> SettersOf(TriggerBase trigger) => trigger switch
        {
            Trigger single => single.Setters,
            MultiTrigger multi => multi.Setters,
            _ => [],
        };

        static bool OnHover(TriggerBase trigger) => trigger switch
        {
            Trigger single => single.Property == UIElement.IsMouseOverProperty,
            MultiTrigger multi => multi.Conditions.Any(condition => condition.Property == UIElement.IsMouseOverProperty),
            _ => false,
        };

        var style = (Style)FindResource(typeof(System.Windows.Controls.Primitives.ScrollBar));
        var resizes = style.Triggers.Where(OnHover).SelectMany(SettersOf).OfType<Setter>()
            .Any(setter => setter.Property == FrameworkElement.WidthProperty || setter.Property == FrameworkElement.HeightProperty);
        log.AppendLine(FormattableString.Invariant($"style|size-on-hover={resizes}"));

        static (System.Windows.Controls.Primitives.ScrollBar? Bar, FrameworkElement? Content) PartsOf(ScrollViewer viewer)
            => (viewer.Template?.FindName("PART_VerticalScrollBar", viewer) as System.Windows.Controls.Primitives.ScrollBar,
                viewer.Template?.FindName("PART_ScrollContentPresenter", viewer) as FrameworkElement);

        string Measure(string phase, ScrollViewer viewer)
        {
            var (lane, content) = PartsOf(viewer);
            var thumb = lane?.Track?.Thumb;
            var pill = thumb?.Template?.FindName("Pill", thumb) as FrameworkElement;
            var inset = pill is not null && lane is not null ? pill.TranslatePoint(new Point(0, 0), lane).X : -1;
            return FormattableString.Invariant(
                $"{phase}|visible={lane?.IsVisible}|lane={lane?.ActualWidth:F2}|pill={pill?.ActualWidth:F2}|inset={inset:F2}|content={content?.ActualWidth:F2}|hovered={lane?.IsMouseOver}|thumb={thumb?.IsVisible}|scrollable={viewer.ScrollableHeight:F0}");
        }

        // 1. The narrow bar's list, pinned so it stays up beside the settings window.
        _modules!.Bar.ProbeShowPinned();
        await Settle();
        var bar = Windows.OfType<BarWindow>().FirstOrDefault();
        if (bar is not null && Descendants(bar.Cards).OfType<ScrollViewer>().FirstOrDefault() is { } list)
        {
            log.AppendLine(Measure("bar", list));
        }

        // Out of the way: the pointer is about to go to the settings window,
        // and a topmost bar over its scrollbar would take the hover.
        bar?.Dismiss();

        // 2. The settings window on a page long enough to scroll.
        _modules.Settings.ShowAt("service.preset");
        await Settle();
        var settings = Windows.OfType<SettingsWindow>().FirstOrDefault(window => window.IsVisible);
        if (settings is null || PartsOf(settings.PageScroller).Bar is not { IsVisible: true } pageBar)
        {
            log.AppendLine("settings|missing");
            log.AppendLine("done");
            File.WriteAllText(Path.Combine(directory, "scroll-lane.log"), log.ToString());
            return;
        }

        settings.Activate();
        await Settle();
        log.AppendLine(Measure("settings-rest", settings.PageScroller));

        // 3. The script moves the real pointer onto the thumb (only when the
        // user is not using the mouse); the hover is measured once it lands.
        // Across the lane's own middle, not the thumb's: a thumb laid out
        // wider than its lane (the old style's 24 DIP) has its middle outside.
        var thumbY = pageBar.Track?.Thumb is { } pageThumb
            ? pageThumb.TranslatePoint(new Point(0, pageThumb.ActualHeight / 2), pageBar).Y
            : pageBar.ActualHeight / 2;
        var target = pageBar.PointToScreen(new Point(pageBar.ActualWidth / 2, thumbY));
        File.WriteAllText(Path.Combine(directory, "scroll-lane.ready"),
            FormattableString.Invariant($"{(int)Math.Round(target.X)},{(int)Math.Round(target.Y)}"));

        for (var waited = 0; waited < 120 && !pageBar.IsMouseOver; waited++)
        {
            await Task.Delay(100);
        }

        await Settle();
        log.AppendLine(Measure("settings-hover", settings.PageScroller));
        if (pageBar.IsMouseOver && settings.Content is FrameworkElement settingsRoot)
        {
            RenderToPng(settingsRoot, Path.Combine(directory, "scroll-lane-hover.png"));
        }

        // 4. A page too short to scroll keeps the lane (用户需求 2026-10-10：滚动条出现
        // 时内容也不能动): same content width as the long page, and no thumb. The
        // window grows until the About page fits — at its usual height every page
        // still scrolls a little.
        settings.Height = Math.Max(settings.Height, SystemParameters.WorkArea.Height - 40);
        _modules.Settings.ShowAt("about.version");
        await Settle();
        log.AppendLine(Measure("settings-short", settings.PageScroller));

        // 5. Scroll areas draw no focus frame and are no Tab stop (用户实录 2026-10-13：设置页
        // 整块内容外面一圈点线，正好框住内容列). A press on blank page space hands keyboard
        // focus to the page scroller itself (ScrollViewer focuses itself on mouse down);
        // once the keyboard is in use, WPF draws the focused element's focus visual — by
        // default the system's dotted frame, round the whole page. The scroller keeps
        // taking focus (PageUp/PageDown, arrows); it just has no frame to draw.
        var scrollers = Descendants(settings).OfType<ScrollViewer>().ToList();
        var framed = scrollers.Count(viewer => viewer.IsTabStop || viewer.FocusVisualStyle is not null);
        settings.Activate();
        var holds = ReferenceEquals(Keyboard.Focus(settings.PageScroller), settings.PageScroller);
        log.AppendLine(FormattableString.Invariant(
            $"focus|scrollers={scrollers.Count}|framed={framed}|page-takes-focus={holds}|page-frame={settings.PageScroller.FocusVisualStyle is not null}"));

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "scroll-lane.log"), log.ToString());
    }

    /// <summary>
    /// 一段模型常写的 Markdown：每种块都有，外加一条不该能点的链接。逐行拼而不用原始字符串：
    /// 本文件包在 #if DEBUG 里，Release 下预处理器会把行首的 "# 标题" 当成指令。
    /// </summary>
    private static readonly string MarkdownSample = string.Join('\n',
        "# 发布说明",
        "",
        "这是 **加粗**、*斜体*、~~删除~~ 和 `行内代码`，还有[一个链接](https://example.com)。",
        "第二行紧跟着，不并进上一行。",
        "",
        "## 步骤",
        "1. 打开设置",
        "2. 选择 **翻译**",
        "   - 子项一",
        "   - 子项二",
        "3. 保存",
        "",
        "- [x] 已完成",
        "- [ ] 未完成",
        "",
        "> 引用的一段话。",
        "",
        "```csharp",
        "var answer = 42;",
        "```",
        "",
        "| 词 | 释义 |",
        "|:---|---:|",
        "| apple | 苹果 |",
        "",
        "---",
        "",
        "[不该能点的链接](javascript:alert(1))");

    /// <summary>
    /// 探针命令 markdown（用户需求 2026-10-10：大模型的输出按 Markdown 显示，ADR-0014）。翻译框
    /// （假后端先译完一句，再把样稿排进同一个译文框）与反向输入框各排一遍样稿：数标题、列表、
    /// 表格、引用、分隔线、代码段、链接，看标记符号都被吃掉、单个换行留着、只有网页链接能点；
    /// 再看流式光标结算即撤、同一段字重排不丢选区。两张 PNG 供人眼看。
    /// </summary>
    private async void ProbeMarkdown(AppShell shell)
    {
        var directory = DebugOverrides.ProbeDirectory!;
        var log = new StringBuilder();

        async Task Settle()
        {
            for (var round = 0; round < 6; round++)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
        }

        static IEnumerable<Block> AllBlocks(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                yield return block;
                IEnumerable<Block> children = block switch
                {
                    Section section => section.Blocks,
                    List list => list.ListItems.SelectMany(item => item.Blocks),
                    Table table => table.RowGroups.SelectMany(group => group.Rows)
                        .SelectMany(row => row.Cells).SelectMany(cell => cell.Blocks),
                    _ => [],
                };
                foreach (var child in AllBlocks(children))
                {
                    yield return child;
                }
            }
        }

        static IEnumerable<Inline> AllInlines(IEnumerable<Inline> inlines)
        {
            foreach (var inline in inlines)
            {
                yield return inline;
                if (inline is Span span)
                {
                    foreach (var child in AllInlines(span.Inlines))
                    {
                        yield return child;
                    }
                }
            }
        }

        string Describe(string host, MarkdownBox box)
        {
            var blocks = AllBlocks(box.Document.Blocks).ToList();
            var paragraphs = blocks.OfType<Paragraph>().ToList();
            var headings = paragraphs
                .Where(paragraph => paragraph.FontWeight == FontWeights.SemiBold && paragraph.FontSize > box.FontSize + 0.5)
                .ToList();
            var mono = FindResource("Font.Mono");
            var links = paragraphs.SelectMany(paragraph => AllInlines(paragraph.Inlines)).OfType<Hyperlink>().ToList();
            var text = new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;
            var markers = new[] { "**", "```", "|:---", "- [x]", "~~", "## " }.Count(text.Contains);
            return FormattableString.Invariant(
                $"{host}|headings={headings.Count}|lists={blocks.OfType<List>().Count()}|tables={blocks.OfType<Table>().Count()}|rows={blocks.OfType<Table>().Sum(table => table.RowGroups.Sum(group => group.Rows.Count))}|quotes={blocks.OfType<Section>().Count(section => section.BorderThickness.Left >= 3)}|rules={blocks.OfType<BlockUIContainer>().Count()}|code={paragraphs.Count(paragraph => Equals(paragraph.FontFamily, mono))}|links={links.Count}|openable={links.Count(link => link.NavigateUri is not null)}|markers={markers}|linebreak={text.Contains("。\r\n第二行")}|body={box.FontSize:F1}|h1={headings.FirstOrDefault()?.FontSize ?? 0:F1}");
        }

        static int Carets(MarkdownBox box)
            => AllBlocks(box.Document.Blocks).OfType<Paragraph>()
                .SelectMany(paragraph => AllInlines(paragraph.Inlines)).OfType<InlineUIContainer>().Count();

        // 1. The panel: the fake backend settles one sentence, then the sample goes
        // into the very box the translation landed in.
        shell.ShowPanel?.Invoke("probe markdown", null);
        PanelWindow? panel = null;
        for (var waited = 0; waited < 80; waited++)
        {
            panel = Windows.OfType<PanelWindow>().FirstOrDefault(window => window.IsVisible);
            if (panel is { TranslatedText: { Streaming: false, Shown.Length: > 0 } })
            {
                break;
            }

            await Task.Delay(100);
        }

        if (panel is null)
        {
            File.WriteAllText(Path.Combine(directory, "markdown.log"), "panel|missing" + Environment.NewLine + "done" + Environment.NewLine);
            return;
        }

        // The panel's own follow-up renders (the settled state, the dictionary stage) go
        // first; after that the sample goes in and everything below is measured in the
        // same breath — a render of the panel's session would rightly put its text back.
        await Task.Delay(800);
        await Settle();
        var box = panel.TranslatedText;
        box.Show(MarkdownSample, streaming: false);
        panel.UpdateLayout();
        log.AppendLine(Describe("panel", box));

        // Where the first glyph lands inside the box: the text must start at the
        // box's own left edge, like the TextBlock it replaced.
        var origin = box.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        log.AppendLine(FormattableString.Invariant(
            $"inset|first={origin.GetCharacterRect(LogicalDirection.Forward).Left:F2}|pagePadding={box.Document.PagePadding}|padding={box.Padding}"));
        if (panel.Content is FrameworkElement panelRoot)
        {
            RenderToPng(panelRoot, Path.Combine(directory, "markdown-panel.png"));
        }

        // 2. Mid-stream the caret rides on the last line; settled, it is gone.
        box.Show(MarkdownSample[..60], streaming: true);
        var streamingCarets = Carets(box);
        box.Show(MarkdownSample, streaming: false);
        log.AppendLine(FormattableString.Invariant($"caret|streaming={streamingCarets}|settled={Carets(box)}"));

        // 3. The same text again must not re-lay the document: a selection survives.
        var start = box.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var end = start;
        for (var step = 0; step < 4 && end.GetNextInsertionPosition(LogicalDirection.Forward) is { } next; step++)
        {
            end = next;
        }

        box.Selection.Select(start, end);
        var before = box.Selection.Text;
        box.Show(MarkdownSample, streaming: false);
        log.AppendLine(FormattableString.Invariant($"selection|before={before}|after={box.Selection.Text}"));

        // 4. The reverse box, at its compact size.
        shell.ShowReverseInput?.Invoke();
        await Settle();
        if (Windows.OfType<ReverseInputWindow>().FirstOrDefault(window => window.IsVisible) is { } reverse)
        {
            // Measured in the same breath: the window re-renders its output from
            // its own (empty) session on the next state change, as it should.
            reverse.OutputDoc.Show(MarkdownSample, streaming: false);
            reverse.OutputDoc.Visibility = Visibility.Visible;
            reverse.OutputScroll.Visibility = Visibility.Visible;
            reverse.UpdateLayout();
            log.AppendLine(Describe("reverse", reverse.OutputDoc));
            if (reverse.Content is FrameworkElement reverseRoot)
            {
                RenderToPng(reverseRoot, Path.Combine(directory, "markdown-reverse.png"));
            }
        }
        else
        {
            log.AppendLine("reverse|missing");
        }

        log.AppendLine("done");
        File.WriteAllText(Path.Combine(directory, "markdown.log"), log.ToString());
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
            // 用户需求 2026-10-10 撤掉常驻窄条后，bar 命令量的是一扇钉住的窄条。
            case "bar":
                _modules!.Bar.ProbeShowPinned();
                break;

            // 光标与占位同一起点、光标用 Accent（用户实录 2026-10-05）。
            case "caret":
                ProbeCaretGeometry(shell);
                break;

            // 悬停提示是拾语的样子、跟随主题（用户需求 2026-10-05）。
            case "tooltip":
                ProbeTooltipLook(shell);
                break;

            // 翻译徽标只留一个「译」（用户需求 2026-10-10）。
            case "badge":
                ProbeBadge(shell);
                break;

            // 指到滚动条时它不再挤别的控件（用户实录 2026-10-10）。脚本负责移鼠标。
            case "scroll-lane":
                ProbeScrollLane();
                break;

            // 大模型的输出按 Markdown 显示（用户需求 2026-10-10，ADR-0014）。配 SHIYU_FAKE_BACKEND=1。
            case "markdown":
                ProbeMarkdown(shell);
                break;

            // 内容字号：被阅读的文字跟着变，控件文字不变（用户需求 2026-10-09）。
            case "content-size":
                ProbeContentSize(shell);
                break;

            // 来源图标可以关；关掉或取不到图标时那一格什么都不放（用户需求 2026-10-14）。
            case "source-icons":
                ProbeSourceIcons(shell);
                break;

            // 反向输入框的译文能用鼠标选取复制（用户需求 2026-10-05）。配 SHIYU_FAKE_BACKEND=1。
            case "reverse-select":
                // Long enough to wrap: the line pitch is only measurable on two lines.
                _modules!.ReverseInput.ProbeSelectableOutput(
                    "hello probe, this sentence runs long enough to wrap onto a second line of the reverse box");
                break;

            // 取词借走的剪贴板在别的程序抢着读时也还得回去（用户实录 2026-10-15）。脚本先把记事本放到
            // 前台、选中全文；会动真剪贴板，不在 run-all 里。
            case "capture-restore":
                _modules!.Selection.ProbeCaptureRestore();
                break;

            // 反向输入框的回车按钮（用户需求 2026-10-13）。配 SHIYU_FAKE_BACKEND=1；只点不会贴回的那一步。
            case "reverse-enter":
                _modules!.ReverseInput.ProbeEnterButton("hello probe enter button");
                break;

            // 把列表翻到底后重开、外部写、粘贴呼出：都必须回到最新的那条
            // （用户实录 2026-10-04：打开窄条有时停在最底部）。
            case "bar-newest":
                _modules!.Bar.ProbeStartsAtNewest();
                break;

            // 窄条里的图片按屏幕像素解码原图，不再拿 240 像素的缩略图放大（用户实录 2026-10-14）。
            case "bar-sharp":
                _modules!.Bar.ProbeSharpImages();
                break;

            // 常驻钉住：撤掉常驻窄条，头部加图钉与齿轮，设置里一个开关、默认关（用户需求 2026-10-10）。
            case "bar-pin":
                _modules!.Bar.ProbePinBehaviour();
                break;

            // 按住空格预览图片：不抖、按屏幕像素清楚（用户实录 2026-10-09）。脚本负责按键。
            case "preview-image":
                _modules!.Bar.ProbePreviewImage();
                break;

            case "quickbar":
                // 票 26 合并后 quickbar 命令就是快速粘贴本身（默认没钉住的
                // 窄条），定位参数（384 DIP）在探针脚本里同步。
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

            // 点托盘图标，设置窗要到最前（用户实录 2026-10-13）：最小化、最大化后最小化、被盖住，再开。
            case "settings-more":
                _modules!.Settings.ProbeMore();
                break;

            case "settings-front":
                _modules!.Settings.ProbeBringToFront();
                break;

            case "panel":
                _modules!.Translation.ShowPanel(text);
                break;

            // LLM 出字时界面不卡（用户实录 2026-10-15）。配 SHIYU_FAKE_BACKEND=stream。
            case "stream-jank":
                _modules!.Translation.ProbeStreamJank(text);
                break;

            // 翻译面板像反向输入框一样挑模板（用户需求 2026-10-09）。配 SHIYU_FAKE_BACKEND=1。
            case "panel-templates":
                _modules!.Translation.ProbeTemplatePicker("hello panel templates");
                break;

            // 热路径复测（评审 2026-10-16 F-21）：常驻进程里反复呼出窄条，轻量模式开与关各量一遍。
            case "summon-timing":
                _modules!.Bar.ProbeSummonTiming();
                break;

            // 图里的字：真认一张、量输入延迟（评审 2026-10-16 F-07）。
            case "ocr":
                _modules!.Ocr.ProbeOcr();
                break;

            // 朗读用 OneCore 音色、朗读中按钮显示停止（评审 2026-10-16 F-10）。配 SHIYU_FAKE_BACKEND=1；语音已静音。
            case "panel-speech":
                _modules!.Translation.ProbeSpeech();
                break;

            // 离线词典的卡（评审 2026-10-16 F-08）。配 SHIYU_FAKE_BACKEND=1 与 SHIYU_LOCAL_DICTIONARY=1。
            case "panel-dictionary":
                _modules!.Translation.ProbeDictionaryCard();
                break;

            // 面板与反向框的钉（用户需求 2026-10-19）：钉住就不自动收起。
            case "overlay-pin":
                ProbeOverlayPin();
                break;

            // 预览弹窗的钉（用户需求 2026-10-19）：钉住就不自动收起、只留干净内容。
            case "preview-pin":
                ProbePreviewPin();
                break;

            // 文件夹的打开/定位复现（用户实录 2026-10-19）：不碰剪贴板、不真开资源管理器。
            case "folder-open-locate":
                ProbeFolderOpenLocate();
                break;

            // 图片卡按 Enter 的复现（用户实录 2026-10-19）：会动真剪贴板，脚本不进 run-all。
            case "image-paste":
                ProbeImagePaste();
                break;

            // Win10 降级外观（评审 F-13；用户实录 2026-10-17）：强制走 DegradeShell 渲 PNG。
            case "degraded-shell":
                ProbeDegradedShell();
                break;

            // 生词本验收（评审 2026-10-16 F-09，用户委托 2026-10-17）。
            case "vocabulary":
                ProbeVocabulary(shell);
                break;

            // 粘贴全部验收（评审 2026-10-16 F-12，用户委托 2026-10-17）：会动真剪贴板，脚本不进 run-all。
            case "batch-paste":
                ProbeBatchPaste(shell);
                break;

            // 定期备份：备份、修剪、打不开的口令（评审 2026-10-16 F-20）。
            case "auto-backup":
                ProbeAutoBackup(shell);
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
