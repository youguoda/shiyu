using System.Runtime.CompilerServices;
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
        public Action End = () => { };
    }

    /// <summary>
    /// 每扇窗正在进行的那一次拖动。抬起没送到时（见 <see cref="Begin"/> 里的 OnLost），
    /// 下一次按下先替上一次收尾——处理器不许越挂越多。
    /// </summary>
    private static readonly ConditionalWeakTable<Window, Drag> Active = new();

    /// <summary>
    /// 从这一次按下开始拖。<paramref name="band"/> 给每个移动帧的 Z 序（空 = 置顶）；
    /// <paramref name="done"/> 在拖动结束时收到「窗口是否真的动过」。按下处无法定位时返回假（不拖）。
    /// </summary>
    public static bool Begin(Window window, MouseButtonEventArgs e, Func<ZBand>? band = null, Action<bool>? done = null)
    {
        if (Active.TryGetValue(window, out var previous))
        {
            previous.End();
        }

        var drag = new Drag { Window = window, Band = band, Done = done };
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (!WindowRects.TryGet(handle, out drag.Base))
        {
            return false;
        }

        // 捕获让窗外的抬起也送回这扇窗。拿不到（没有真的按着键、进程不在前台）也照拖：
        // 抬起落在窗内照样收尾。
        window.CaptureMouse();

        var screen = window.PointToScreen(e.GetPosition(window));
        drag.Start = new Point(screen.X, screen.Y);
        drag.End = Finish;
        window.MouseMove += OnMove;
        window.MouseLeftButtonUp += OnUp;
        window.LostMouseCapture += OnLost;
        Active.AddOrUpdate(window, drag);
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
            args.Handled = true;
            Finish();
        }

        // 捕获被拿走（Alt+Tab、Win 键、窗被收起）：抬起不会再送到这扇窗。不在这里收尾的话，
        // 之后鼠标只是经过窗口，窗口也跟着光标走。子元素丢捕获也冒泡到这里——窗还攥着就不算。
        void OnLost(object? sender, MouseEventArgs args)
        {
            if (!ReferenceEquals(Mouse.Captured, window))
            {
                Finish();
            }
        }

        void Finish()
        {
            if (!Active.TryGetValue(window, out var current) || !ReferenceEquals(current, drag))
            {
                return;
            }

            Active.Remove(window);
            window.MouseMove -= OnMove;
            window.MouseLeftButtonUp -= OnUp;
            window.LostMouseCapture -= OnLost;
            if (ReferenceEquals(Mouse.Captured, window))
            {
                window.ReleaseMouseCapture();
            }

            drag.Done?.Invoke(drag.Moved);
        }
    }
}
