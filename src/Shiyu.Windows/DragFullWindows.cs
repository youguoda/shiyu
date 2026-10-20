namespace Shiyu.Windows;

/// <summary>
/// 系统的「拖动时显示窗口内容」（DragFullWindows）：关着时系统移动循环只画轮廓、
/// 窗口松手才跳位（用户实录 2026-10-20：历史/设置这类系统标题栏的窗口拖不动）。
/// 应用自绘拖动的窗口（窄条、预览、翻译/反向框，走 <see cref="LiveDrag"/>）不受它
/// 影响；系统标题栏的窗口全靠它。
///
/// EnsureOn 只在本次会话生效、不写回注册表——应用卸载或不再自启后，用户原值原样。
/// </summary>
public static class DragFullWindows
{
    /// <summary>关着就打开。返回「本次是否改了」（本就开着返回假）。</summary>
    public static bool EnsureOn()
    {
        if (!NativeMethods.SystemParametersInfo(NativeMethods.SpiGetDragFullWindows, 0, out var on, 0) || on)
        {
            return false;
        }

        return NativeMethods.SystemParametersInfoSet(
            NativeMethods.SpiSetDragFullWindows, 1, IntPtr.Zero, 0);
    }
}
