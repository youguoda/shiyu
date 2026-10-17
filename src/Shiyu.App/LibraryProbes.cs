#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shiyu.App;

/// <summary>管理窗的调试探针区（只在 Debug 构建存在，同 App.DebugProbes.cs）。</summary>
public partial class LibraryWindow
{
    /// <summary>
    /// 探针命令 source-icons 的管理窗一段（用户需求 2026-10-14：来源图标可以关，空着时什么都不放）。
    /// 每个已实现的行一行：来源、行上有没有图标、有没有缩略图、图标格在不在、格里显出几张图、
    /// 正文从哪儿开始（开着时有图标、没图标的行正文应对齐；图片行多一列缩略图，单独比）。
    /// 顺手渲一张 PNG。
    /// </summary>
    internal string ProbeIconSlots(string phase)
    {
        var log = new StringBuilder();
        foreach (var item in _items)
        {
            if (EntryList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement row
                || Named(row, "IconSlot") is not { } slot)
            {
                continue;
            }

            var images = Visuals(slot).OfType<Image>().Count(image => image.IsVisible);
            var text = (slot.Parent as Grid)?.Children.OfType<FrameworkElement>()
                .FirstOrDefault(child => Grid.GetColumn(child) == 3);
            var textLeft = text?.TranslatePoint(new Point(0, 0), row).X ?? -1;
            log.AppendLine(FormattableString.Invariant(
                $"lib-{phase}|source={item.SourceApp}|icon={item.Icon is not null}|thumb={item.Thumbnail is not null}|slot={slot.IsVisible}|images={images}|textLeft={textLeft:F0}"));
        }

        if (Content is FrameworkElement root)
        {
            App.RenderToPng(root, Path.Combine(DebugOverrides.ProbeDirectory!, $"source-icons-lib-{phase}.png"));
        }

        return log.ToString();
    }

    private static FrameworkElement? Named(DependencyObject root, string name)
        => Visuals(root).OfType<FrameworkElement>().FirstOrDefault(element => element.Name == name);

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Visuals(child))
            {
                yield return deeper;
            }
        }
    }
    /// <summary>探针：点一下「生词」开关（评审 2026-10-16 F-09 的验收）。</summary>
    internal void RaiseVocabularyToggle() => OnLogVocabularyToggle(LogVocabularyButton, new RoutedEventArgs());

    /// <summary>探针：「生词」筛选开着时列表里的条数（评审 2026-10-16 F-09 的验收）。</summary>
    internal int ProbeVocabularyListCount() => LogList.Items.Count;

    /// <summary>
    /// 探针命令 batch-paste 的管理窗一段（评审 2026-10-16 F-12 的验收，用户委托 2026-10-17；会动真剪贴板，
    /// 不进 run-all）：按**非列表顺序**选中三条，点「粘贴全部」，读回记事本里的文字比对顺序。
    /// 判断在应用里做完，日志只有真假与一行正文（换行压成空格）。
    /// </summary>
    internal async Task<string> ProbeBatchPaste(
        IReadOnlyList<long> ids, string expected, IntPtr notepadHandle, Func<string?>? readClipboard)
    {
        var log = new StringBuilder();
        EntryList.SelectedItems.Clear();
        foreach (var id in ids)
        {
            if (_items.FirstOrDefault(item => item.Id == id) is { } item)
            {
                EntryList.SelectedItems.Add(item);
            }
        }

        log.AppendLine(FormattableString.Invariant(
            $"select|count={EntryList.SelectedItems.Count}|first={Flat(EntryList.SelectedItems.OfType<EntryItem>().FirstOrDefault()?.Text ?? string.Empty)}"));

        for (var round = 0; round < 4; round++)
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // 抢前台是和用户的手赛跑（settings-front 探针的同款 flake）：第一回没贴上就再试，最多三回。
        string? onClipboard = null;
        var actual = string.Empty;
        var frontAfter = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            PasteAll();

            frontAfter = Shiyu.Windows.ForegroundWindow.Current().Handle == notepadHandle;
            try { onClipboard = readClipboard?.Invoke(); }
            catch (Exception)
            {
                // expected: 剪贴板正被别的程序占着——诊断行如实记空。
            }

            for (var waited = 0; waited < 20; waited++)
            {
                await Task.Delay(200);
                actual = ReadWindowText(notepadHandle);
                if (Flat(actual).Contains(Flat(expected), StringComparison.Ordinal))
                {
                    break;
                }
            }

            if (Flat(actual).Contains(Flat(expected), StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(600);
        }

        var pastedLine =
            $"pasted|ok={Flat(actual).Contains(Flat(expected), StringComparison.Ordinal)}"
            + $"|expected={Flat(expected)}"
            + $"|clipboard={onClipboard is { } text && Flat(text).Contains(Flat(expected), StringComparison.Ordinal)}"
            + $"|front={frontAfter}"
            + $"|actual={Flat(actual)}";
        log.AppendLine(pastedLine);
        return log.ToString();

        // 按一切空白切分、单空格重连：换行在 expected 侧是 \r\n（Flat 成两个空格）、在记事本读回侧
        // 是单个，直接 Replace 会对不上（2026-10-17 实测）。
        static string Flat(string text)
            => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// UIA 读一个窗口里的文字（记事本的编辑区带 TextPattern，新版与经典版都走这条）。树里可能有好几个
    /// 带 TextPattern 的控件，读**面积最大的那个**——小的是标题条一类（UIA spike 的同款选法）。
    /// </summary>
    private static string ReadWindowText(IntPtr handle)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
            System.Windows.Automation.AutomationElement? best = null;
            var bestArea = -1.0;
            foreach (var descendant in root.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                System.Windows.Automation.Condition.TrueCondition).Cast<System.Windows.Automation.AutomationElement>())
            {
                try
                {
                    if (descendant.GetCurrentPattern(System.Windows.Automation.TextPattern.Pattern)
                        is not System.Windows.Automation.TextPattern)
                    {
                        continue;
                    }

                    var rect = descendant.Current.BoundingRectangle;
                    var area = rect.IsEmpty ? 0 : rect.Width * rect.Height;
                    if (area > bestArea)
                    {
                        best = descendant;
                        bestArea = area;
                    }
                }
                catch (Exception failure) when (failure
                    is System.Windows.Automation.ElementNotAvailableException
                    or InvalidOperationException)
                {
                    // expected: 不支持该模式的控件会抛 InvalidOperationException（不是 null）；
                    // 窗口正在出现或消失的半路上则抛 ElementNotAvailableException。都跳过试下一个。
                }
            }

            if (best?.GetCurrentPattern(System.Windows.Automation.TextPattern.Pattern)
                is System.Windows.Automation.TextPattern pattern)
            {
                return pattern.DocumentRange.GetText(-1);
            }
        }
        catch (Exception)
        {
            // expected: 窗口正在消失——当作没读到。
        }

        return string.Empty;
    }
}
#endif
