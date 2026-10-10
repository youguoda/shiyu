#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>反向输入框的调试探针区（只在 Debug 构建存在，同 App.DebugProbes.cs）。</summary>
internal partial class ReverseInputWindow
{
    /// <summary>
    /// 探针命令 reverse-select（用户需求 2026-10-05：译文能用鼠标选取复制）。把一句话放进
    /// 输入框，等后端译完，看结算后的输出是不是可选取的只读框；选中开头几个字，问一声
    /// "复制"此刻能不能执行——只问不执行，不碰用户的剪贴板。渲一张窗口 PNG 供人眼看。
    /// 2026-10-10 起输出是一个按 Markdown 排的框（ADR-0014），从流式到结算都是它：日志的
    /// label 一栏（旧的流式标签）恒为 False，行距拿来比的是内容字号的行高。
    /// </summary>
    internal async Task<string> ProbeSelectableOutput(string text, string picture)
    {
        InputBox.Text = text;
        for (var waited = 0; waited < 60 && (_session.Running || _session.SettledOutput is null); waited++)
        {
            await Task.Delay(100);
        }

        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        var log = new StringBuilder();
        log.AppendLine(FormattableString.Invariant(
            $"settled|box={OutputDoc.IsVisible}|readOnly={OutputDoc.IsReadOnly}|label=False|text={OutputDoc.Shown}"));

        // 行距必须等于内容字号的行高（Line.ContentCompact）——多行译文两行之间就是这一个值。
        var first = OutputDoc.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var lines = 1;
        for (var line = first.GetLineStartPosition(1); line is not null; line = line.GetLineStartPosition(1))
        {
            lines++;
        }

        var second = first.GetLineStartPosition(1);
        var pitch = second is null
            ? double.NaN
            : second.GetCharacterRect(LogicalDirection.Forward).Top - first.GetCharacterRect(LogicalDirection.Forward).Top;
        log.AppendLine(FormattableString.Invariant(
            $"pitch|box={pitch:F1}|label={(double)FindResource("Line.ContentCompact"):F1}|lines={lines}"));

        OutputDoc.Focus();
        var end = first;
        for (var step = 0; step < 4 && end.GetNextInsertionPosition(LogicalDirection.Forward) is { } next; step++)
        {
            end = next;
        }

        OutputDoc.Selection.Select(first, end);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        log.AppendLine(FormattableString.Invariant(
            $"selected|text={OutputDoc.Selection.Text}|canCopy={ApplicationCommands.Copy.CanExecute(null, OutputDoc)}"));

        if (Content is FrameworkElement root && root.ActualWidth > 0)
        {
            const double scale = 1.5;
            var sheet = new DrawingVisual();
            using (var context = sheet.RenderOpen())
            {
                var area = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                context.DrawRectangle((Brush)FindResource("Brush.Background"), null, area);
                context.DrawRectangle(new VisualBrush(root), null, area);
            }

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(sheet);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(picture);
            encoder.Save(stream);
        }

        return log.ToString();
    }

    /// <summary>
    /// 探针命令 translation-log 的反向输入框一段（用户需求 2026-10-09：两个框都能拖、翻译记下来）。
    /// 放一句话进去、等它落定，然后：
    ///   - 问拖动的把手：底栏提示是把手；输入框、输出框、两枚 chip 不是（选字、点按钮照旧）；
    ///   - 把窗口挪到远处、走拖动收尾，再让内容长高：窗口应留在拖到的地方，而不是被拽回插入符旁；
    ///   - 走 Esc 的路径关窗：落定的结果应记进翻译记录（由调用方查库）。
    /// 系统的移动循环要真鼠标，探针不碰用户的鼠标——拖动本身只能由人来试，这里验的是它前后两头。
    /// </summary>
    internal async Task<string> ProbeTranslationLog(string text)
    {
        var log = new StringBuilder();

        async Task<bool> Settled()
        {
            for (var waited = 0; waited < 80; waited++)
            {
                if (_session.SettledOutput is not null && OutputDoc.Visibility == Visibility.Visible)
                {
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        InputBox.Text = text;
        log.AppendLine(FormattableString.Invariant($"settled|ok={await Settled()}|output={_session.SettledOutput}"));

        log.AppendLine(FormattableString.Invariant(
            $"grip|hint={DragGrip.IsGrip(HintText, Shell)}|input={DragGrip.IsGrip(InputBox, Shell)}|output={DragGrip.IsGrip(OutputDoc, Shell)}|template={DragGrip.IsGrip(TemplateChip, Shell)}|direction={DragGrip.IsGrip(DirectionChip, Shell)}"));

        // 落点离原位尽量远：原来在右半边就挪到左边，反之亦然；纵向留出长高的余地。
        var handle = new WindowInteropHelper(this).Handle;
        WindowRects.TryGet(handle, out var start);
        var middle = _workArea.Left + _workArea.Width / 2;
        var target = new ScreenPoint(
            start.Left + start.Width / 2 > middle ? _workArea.Left + 40 : _workArea.Right - start.Width - 40,
            start.Top > _workArea.Top + 200 ? _workArea.Top + 40 : _workArea.Top + 240);
        TransientWindow.MoveTo(handle, target, ZBand.Topmost);
        KeepWhereDragged();

        InputBox.Text = string.Join("\n", text, text, text, text);
        var grown = await Settled();
        WindowRects.TryGet(handle, out var after);
        log.AppendLine(FormattableString.Invariant(
            $"drag|settled={grown}|from={start.Left},{start.Top},{start.Height}|target={target.X},{target.Y}|after={after.Left},{after.Top},{after.Height}"));

        CancelAndClose();
        log.AppendLine(FormattableString.Invariant($"closed|visible={IsVisible}"));
        return log.ToString();
    }
}
#endif
