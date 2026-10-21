using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 免费引擎的近况（评审 2026-10-16 F-14）：几行字，窗口开着时翻译一结束就跟着变。与翻译记录那一行
/// 同样上屏才订、下屏退订——<see cref="FreeEngineStatus.Shared"/> 是进程级的，订着不退会把整个设置窗留在内存里。
/// </summary>
internal static class FreeEngineStatusRow
{
    public static FrameworkElement Build()
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        text.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        text.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        void Refresh() => text.Text = string.Join("\n", FreeEngineStatus.Shared.Describe());
        void OnChanged() => text.Dispatcher.BeginInvoke(Refresh);

        var watching = false;
        text.Loaded += (_, _) =>
        {
            if (!watching)
            {
                watching = true;
                FreeEngineStatus.Shared.Changed += OnChanged;
            }

            Refresh();
        };
        text.Unloaded += (_, _) =>
        {
            if (watching)
            {
                watching = false;
                FreeEngineStatus.Shared.Changed -= OnChanged;
            }
        };
        return text;
    }
}
