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
/// 文字、留白、头部与底栏——就拖整窗；按在按钮、输入框、滚动条、列表上的照旧归它们自己：
/// 选字、点按钮、拖滚动条一样不少。
///
/// 拖动是手动跟手（<see cref="LiveDrag"/>：按下捕获、移动即 SetWindowPos 实时平移）——
/// 不再走 HTCAPTION 系统移动循环，透明窗上它只画轮廓不实时跟，松手才跳位
/// （用户实录 2026-10-20）。
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
    /// 从这一下按下开始拖（手动跟手）。<paramref name="done"/> 在松手时收到
    /// 「窗口是否真的动过」。
    /// </summary>
    public static void Run(Window window, MouseButtonEventArgs e, Action<bool>? done = null)
        => LiveDrag.Begin(window, e, done: done);

    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
