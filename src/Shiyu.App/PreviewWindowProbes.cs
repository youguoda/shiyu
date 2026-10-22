#if DEBUG
using System.Windows;
using System.Windows.Input;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 预览窗的探针钩子（钉住、工作台、缩放与整窗拖动的读数与驱动）：只在调试构建存在，产品路径从不调用。
/// </summary>
internal partial class PreviewWindow
{
    internal void ProbeClickPin() =>
        PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal bool ProbePinned => Pinned;

    internal bool ProbeMetaVisible => WhenText.IsVisible || KindText.IsVisible;

    internal bool ProbeTeachVisible => TeachHost.IsVisible;

    // --- 探针（测试方案 2026-10-20；勿在产品路径调用）--------------------------------
    // 跨显残留 / 草稿 / 芯片 / 缩放的读数与驱动。日志只收 ASCII：中文条目只记布尔与长度。

    /// <summary>图钉此刻的视觉状态（强制收起跳过 UpdatePinVisual 的证据读数）。
    /// 读 Visibility 而非 IsVisible：收起后的窗上后者恒假，读不出残留。</summary>
    internal string ProbePinGlyph => PinGlyphFull.Visibility == Visibility.Visible ? "full" : "empty";

    /// <summary>元信息行两半的可见性（Fill 从不恢复它们——串台证据）。</summary>
    internal bool ProbeWhenVisible => WhenText.IsVisible;

    internal bool ProbeKindVisible => KindText.IsVisible;

    /// <summary>编辑框是否可见（与只读正文叠印的另一半）。</summary>
    internal bool ProbeEditHostVisible => EditHost.Visibility == Visibility.Visible;

    internal bool ProbeTextHostVisible => TextHost.IsVisible;

    internal bool ProbeActionRowVisible => ActionRow.IsVisible;

    internal int ProbeChipCount => ActionRow.Items.Count;

    /// <summary>动作行的动作名串（如 copy,save-as-new——每形态芯片表与残留的读数）。</summary>
    internal string ProbeChipActions
        => string.Join(",", ActionRow.Items.OfType<WorkbenchChip>().Select(chip => chip.Action));

    internal bool ProbeDraftTextIs(string expected) => EditHost.Text == expected;

    internal int ProbeDraftTextLength => EditHost.Text.Length;

    internal void ProbeSetDraft(string text) => EditHost.Text = text;

    /// <summary>按动作名点一枚芯片（真实 Click 路由——遗留芯片误触发、handler 累积都走它）。</summary>
    internal bool ProbeChipClick(string action)
    {
        foreach (var chip in ActionRow.Items.OfType<WorkbenchChip>().Where(item => item.Action == action))
        {
            if (ActionRow.ItemContainerGenerator.ContainerFromItem(chip) is FrameworkElement { } container
                && Tree.FindDescendant<System.Windows.Controls.Button>(container) is { } button)
            {
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return true;
            }
        }

        return false;
    }

    internal double ProbeScale => _imageScale;

    internal string ProbeOrigin => FormattableString.Invariant($"{_imageOrigin.X:F3};{_imageOrigin.Y:F3}");

    /// <summary>当前图源的像素尺寸（等原图落位的轮询读数）。</summary>
    internal string ProbeImagePixelSize => ImageHost.Source is { } source
        ? FormattableString.Invariant($"{(int)Math.Round(source.Width)}x{(int)Math.Round(source.Height)}")
        : "none";

    internal int ProbeOriginalsCount => _originals.Count;

    /// <summary>当前图源真正的像素数（ProbeImagePixelSize 读的是 DIP 尺寸，随图里的 DPI 变）。</summary>
    internal string ProbeSourcePixels => ImageHost.Source is System.Windows.Media.Imaging.BitmapSource bitmap
        ? FormattableString.Invariant($"{bitmap.PixelWidth}x{bitmap.PixelHeight}")
        : "none";

    /// <summary>此刻适应框下放大到一比一要的倍数（双击该落到的位置）。</summary>
    internal double ProbeActualSizeScale
        => Shiyu.Core.PreviewSizing.ActualSizeScale(_shown?.PixelWidth ?? 0, ImageHost.ActualWidth, _deviceScale);

    /// <summary>图片内容坐标系的中心（驱动滚轮/拖拽用的落点）。</summary>
    internal Point ProbeImageCenter => new(ImageHost.ActualWidth / 2, ImageHost.ActualHeight / 2);

    /// <summary>图片中心换算到屏幕物理像素（锚点漂移用的固定屏幕点）。</summary>
    internal Point ProbeImageCenterOnScreen => ImageHost.PointToScreen(ProbeImageCenter);

    /// <summary>光标下的图片内容坐标（与处理器同一套映射——锚点漂移的读数）。</summary>
    internal string ProbeLocalUnderCursor
    {
        get
        {
            var at = Mouse.GetPosition(ImageHost);
            return FormattableString.Invariant($"{at.X:F3};{at.Y:F3}");
        }
    }

    internal void ProbeWheel(int delta)
        => ImageHost.RaiseEvent(new MouseWheelEventArgs(
            InputManager.Current.PrimaryMouseDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
        });

    internal async Task<bool> ProbeWheelAtAsync(double x, double y, int delta)
    {
        if (!ProbeCursorTo(x, y))
        {
            return false;
        }

        await ProbePumpAsync();
        ProbeWheel(delta);
        return true;
    }

    internal async Task<bool> ProbePressAtAsync(double x, double y)
    {
        if (!ProbeCursorTo(x, y))
        {
            return false;
        }

        await ProbePumpAsync();
        this.RaiseEvent(new MouseButtonEventArgs(
            InputManager.Current.PrimaryMouseDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
        });
        return true;
    }

    internal async Task<bool> ProbeMoveToAsync(double x, double y)
    {
        if (!ProbeCursorTo(x, y))
        {
            return false;
        }

        await ProbePumpAsync();
        this.RaiseEvent(new MouseEventArgs(
            InputManager.Current.PrimaryMouseDevice, Environment.TickCount)
        {
            RoutedEvent = UIElement.MouseMoveEvent,
        });
        return true;
    }

    internal async Task ProbeReleaseAsync()
    {
        await ProbePumpAsync();
        this.RaiseEvent(new MouseButtonEventArgs(
            InputManager.Current.PrimaryMouseDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });
    }

    /// <summary>连点两下（真实事件路径；合成事件给不了 ClickCount=2，复位从未接线，
    /// 两下也只会落进拖拽——复现不受影响）。</summary>
    internal async Task<bool> ProbeDoubleClickAtAsync(double x, double y)
    {
        if (!ProbeCursorTo(x, y))
        {
            return false;
        }

        await ProbePumpAsync();
        this.RaiseEvent(new MouseButtonEventArgs(
            InputManager.Current.PrimaryMouseDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
        });
        await Task.Delay(60);
        this.RaiseEvent(new MouseButtonEventArgs(
            InputManager.Current.PrimaryMouseDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
        });
        return true;
    }

    /// <summary>编辑框里按 Esc（今日无 KeyDown 处理——「Esc 退出编辑」缺失的忠实复现）。</summary>
    internal void ProbeEscapeInEdit()
        => EditHost.RaiseEvent(new KeyEventArgs(
            InputManager.Current.PrimaryKeyboardDevice,
            PresentationSource.FromVisual(EditHost), Environment.TickCount, Key.Escape)
        {
            RoutedEvent = UIElement.KeyDownEvent,
        });

    /// <summary>把真光标挪到图片内容坐标 (x, y) 的屏幕点（合成事件读的是真实光标位置）。</summary>
    private bool ProbeCursorTo(double x, double y)
    {
        var screen = ImageHost.PointToScreen(new Point(x, y));
        return SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
    }

    internal bool ProbeCursorToScreen(double x, double y)
        => SetCursorPos((int)Math.Round(x), (int)Math.Round(y));

    /// <summary>窗口当前物理位置（整窗拖动的读数）。</summary>
    internal string ProbeWindowXY
        => WindowRects.TryGet(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var rect)
            ? FormattableString.Invariant($"{rect.Left};{rect.Top}")
            : "none";

    internal static async Task ProbePumpAsync()
    {
        for (var round = 0; round < 4; round++)
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        }

        await Task.Delay(30);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}
#endif
