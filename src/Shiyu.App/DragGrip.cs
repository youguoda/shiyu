using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 浮窗的拖动（用户需求 2026-10-09：翻译框与反向输入框都能拖）。按在窗体的"非控件"处——
/// 文字、留白、头部与底栏——就进系统的标题栏移动循环；按在按钮、输入框、滚动条、列表上的
/// 照旧归它们自己：选字、点按钮、拖滚动条一样不少。
///
/// 走 HTCAPTION 而不是 DragMove（同窄条品牌钮，见 <see cref="WindowRects.RunCaptionDrag"/>）：
/// 它在松手时返回，调用方比一比前后的位置就知道窗口动没动。
/// </summary>
internal static class DragGrip
{
    /// <summary>
    /// 这一下按在可拖的地方吗。从按下的元素往上走到 <paramref name="root"/>，途中遇到会自己
    /// 处理按下的控件就不是；<paramref name="excluded"/> 是窗口另外圈出的区域（例如模板列表）。
    /// </summary>
    public static bool IsGrip(object? source, DependencyObject root, params DependencyObject?[] excluded)
    {
        for (var node = source as DependencyObject; node is not null; node = ParentOf(node))
        {
            if (ReferenceEquals(node, root))
            {
                return true;
            }

            if (node is ButtonBase or TextBoxBase or PasswordBox or ScrollBar or Thumb or Selector or Hyperlink
                || Array.IndexOf(excluded, node) >= 0)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 从这一下按下开始拖，松手后返回；窗口真的换了位置时为真。
    /// </summary>
    public static bool Run(Window window, MouseButtonEventArgs e)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !WindowRects.TryGet(handle, out var before))
        {
            return false;
        }

        var point = window.PointToScreen(e.GetPosition(window));
        WindowRects.RunCaptionDrag(handle, (int)point.X, (int)point.Y);

        return WindowRects.TryGet(handle, out var after)
            && (after.Left != before.Left || after.Top != before.Top);
    }

    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
