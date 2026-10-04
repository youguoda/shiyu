#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Shiyu.App;

/// <summary>反向输入框的调试探针区（只在 Debug 构建存在，同 App.DebugProbes.cs）。</summary>
internal partial class ReverseInputWindow
{
    /// <summary>
    /// 探针命令 reverse-select（用户需求 2026-10-05：译文能用鼠标选取复制）。把一句话放进
    /// 输入框，等后端译完，看结算后的输出是不是可选取的只读框；选中开头几个字，问一声
    /// "复制"此刻能不能执行——只问不执行，不碰用户的剪贴板。渲一张窗口 PNG 供人眼看。
    /// </summary>
    internal async Task<string> ProbeSelectableOutput(string text, string picture)
    {
        InputBox.Text = text;
        for (var waited = 0; waited < 60 && OutputBox.Visibility != Visibility.Visible; waited++)
        {
            await Task.Delay(100);
        }

        var log = new StringBuilder();
        log.AppendLine(FormattableString.Invariant(
            $"settled|box={OutputBox.IsVisible}|readOnly={OutputBox.IsReadOnly}|label={OutputText.IsVisible}|text={OutputBox.Text}"));

        // 结算前后同一段字的行距必须一样，否则多行译文一结算就跳一下：只读框的行距要等于
        // 流式标签的 LineHeight（TextBlock.LineHeight 附加属性）。
        var pitch = OutputBox.LineCount > 1
            ? OutputBox.GetRectFromCharacterIndex(OutputBox.GetCharacterIndexFromLineIndex(1)).Top
                - OutputBox.GetRectFromCharacterIndex(0).Top
            : double.NaN;
        log.AppendLine(FormattableString.Invariant(
            $"pitch|box={pitch:F1}|label={OutputText.LineHeight:F1}|lines={OutputBox.LineCount}"));

        OutputBox.Focus();
        OutputBox.Select(0, Math.Min(4, OutputBox.Text.Length));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        log.AppendLine(FormattableString.Invariant(
            $"selected|text={OutputBox.SelectedText}|canCopy={ApplicationCommands.Copy.CanExecute(null, OutputBox)}"));

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
}
#endif
