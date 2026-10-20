using System.Windows;
using System.Windows.Input;
using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// 整窗手动拖动：按下捕获、MouseMove 即 SetWindowPos 实时平移、松手回调。
/// 不走系统移动循环（HTCAPTION/DragMove）——透明窗上它只画轮廓不实时跟窗，
/// 松手才跳位（用户实录 2026-10-20）。窄条、预览、翻译/反向框与自绘标题栏
/// 的拖动共用这一套。
/// </summary>
public static class LiveDrag
{
    private sealed class Drag
    {
        public Window Window = null!;
        public Func<ZBand>? Band;
        public Action<bool>? Done;
        public Point Start;
        public ScreenRect Base;
        public bool Moved;
    }

    /// <summary>
    /// 从这一次按下开始拖。<paramref name="band"/> 给每个移动帧的 Z 序（空 = 置顶）；
    /// <paramref name="done"/> 在松手时收到「窗口是否真的动过」。按下处无法定位时返回假。
    /// </summary>
    public static bool Begin(Window window, MouseButtonEventArgs e, Func<ZBand>? band = null, Action<bool>? done = null)
    {
        var drag = new Drag { Window = window, Band = band, Done = done };
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (!WindowRects.TryGet(handle, out drag.Base))
        {
            return false;
        }

        var screen = window.PointToScreen(e.GetPosition(window));
        drag.Start = new Point(screen.X, screen.Y);
        window.CaptureMouse();
        window.MouseMove += OnMove;
        window.MouseLeftButtonUp += OnUp;
        return true;

        void OnMove(object? sender, MouseEventArgs args)
        {
            var at = window.PointToScreen(args.GetPosition(window));
            var dx = (int)Math.Round(at.X - drag.Start.X);
            var dy = (int)Math.Round(at.Y - drag.Start.Y);
            if (dx != 0 || dy != 0)
            {
                drag.Moved = true;
            }

            TransientWindow.MoveTo(handle, new ScreenPoint(drag.Base.Left + dx, drag.Base.Top + dy),
                drag.Band?.Invoke() ?? ZBand.Topmost);
            args.Handled = true;
        }

        void OnUp(object? sender, MouseButtonEventArgs args)
        {
            window.MouseMove -= OnMove;
            window.MouseLeftButtonUp -= OnUp;
            window.ReleaseMouseCapture();
            args.Handled = true;
            drag.Done?.Invoke(drag.Moved);
        }
    }
}
