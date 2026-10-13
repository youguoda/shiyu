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
}
#endif
