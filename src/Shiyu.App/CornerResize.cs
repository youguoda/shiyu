using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 右下角拖拽改宽高（用户需求 2026-10-28：翻译剪贴板、反向输入、窄条都要能拖右下角）。拖着时窗口跟手、
/// 不随内容伸缩（SizeToContent 暂时让开）；松手交给窗口自己定：宽度记住，高度当上限，之后照旧随内容
/// 伸缩（窄条是列表，高度就是拖出来的高度）。左上角不动，宽高不超过窗口所在屏的工作区。
/// </summary>
internal static class CornerResize
{
    /// <param name="resizing">拖着时的每一步（DIP）：窗口据此跟着调里面滚动区的上限。</param>
    /// <param name="committed">松手（DIP）：窗口记住宽度、把高度当上限，并写进设置。</param>
    public static void Attach(
        Window window,
        Thumb grip,
        Size minimum,
        Action<double, double>? resizing,
        Action<double, double> committed)
    {
        var restoreSizing = SizeToContent.Manual;
        var maxHeight = double.PositiveInfinity;
        var limit = new Size(double.PositiveInfinity, double.PositiveInfinity);

        grip.DragStarted += (_, _) =>
        {
            restoreSizing = window.SizeToContent;
            maxHeight = window.MaxHeight;
            limit = WorkAreaDip(window);
            window.SizeToContent = SizeToContent.Manual;
            window.MaxHeight = double.PositiveInfinity;
            window.Width = window.ActualWidth;
            window.Height = window.ActualHeight;
        };

        grip.DragDelta += (_, e) =>
        {
            var width = Math.Clamp(window.Width + e.HorizontalChange, minimum.Width, Math.Max(minimum.Width, limit.Width));
            var height = Math.Clamp(window.Height + e.VerticalChange, minimum.Height, Math.Max(minimum.Height, limit.Height));
            window.Width = width;
            window.Height = height;
            resizing?.Invoke(width, height);
        };

        grip.DragCompleted += (_, _) =>
        {
            var (width, height) = (window.Width, window.Height);
            window.MaxHeight = maxHeight;
            if (restoreSizing != SizeToContent.Manual)
            {
                window.SizeToContent = restoreSizing;
            }

            committed(width, height);
        };
    }

    /// <summary>窗口左上角到它所在屏工作区右下角还有多大（DIP）：往右下拖不出屏幕。</summary>
    private static Size WorkAreaDip(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!WindowRects.TryGet(handle, out var rect))
        {
            return new Size(double.PositiveInfinity, double.PositiveInfinity);
        }

        var work = ScreenGeometry.WorkAreaAt(new ScreenPoint(rect.Left, rect.Top));
        var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice
            ?? System.Windows.Media.Matrix.Identity;
        return new Size(
            Math.Max(1, work.Right - rect.Left) / scale.M11,
            Math.Max(1, work.Bottom - rect.Top) / scale.M22);
    }
}
