using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 翻译记录（用户需求 2026-10-09）的「立即清空」一行：现有几条、去管理窗口看、一键清空。清空不可撤销，走危险
/// 确认（§6.5，取消拿默认焦点）；剪贴板历史一条不碰。条数跟着库变（两个框在用时也会涨）。
/// </summary>
internal static class TranslationLogRow
{
    /// <param name="shell">设置窗建页时壳还没接上，上屏与点击时才取。</param>
    /// <param name="showError">把话说在这一行所在的设置卡上。</param>
    public static FrameworkElement Build(Window owner, Func<AppShell?> shell, Action<string> showError)
    {
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        count.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        var open = new Button
        {
            Content = "查看翻译记录",
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        open.Click += (_, _) => shell()?.ShowTranslationLog?.Invoke();

        var clear = new Button
        {
            Content = "立即清空",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
        };

        void Refresh()
        {
            var total = shell()?.Store.CountTranslationLog() ?? 0;
            count.Text = total > 0 ? $"共 {total} 条" : "还没有记录";
            clear.IsEnabled = total > 0;
        }

        void OnLogChanged()
        {
            if (count.Dispatcher.CheckAccess())
            {
                Refresh();
            }
            else
            {
                count.Dispatcher.BeginInvoke(Refresh);
            }
        }

        clear.Click += (_, _) =>
        {
            if (shell()?.Store is not { } store)
            {
                return;
            }

            try
            {
                TranslationLogDialogs.AskClear(owner, store);
            }
            catch (Exception failure)
            {
                Log.Event(LogEvent.TranslationLogFailed, failure, ("clear", 1));
                showError("没能清空翻译记录，可以重试。");
            }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(count);
        row.Children.Add(open);
        row.Children.Add(clear);

        // 页面在构造里就建好了，那时 Shell 还没接上（对象初始化器在构造之后）：上屏时才去读、去订，
        // 下屏时退订——翻页来回，订阅也一来一回（Loaded 可能连来两次，只订一份）。
        EntryStore? watched = null;
        row.Loaded += (_, _) =>
        {
            if (watched is null && shell()?.Store is { } store)
            {
                watched = store;
                store.TranslationLogChanged += OnLogChanged;
            }

            Refresh();
        };
        row.Unloaded += (_, _) =>
        {
            if (watched is not null)
            {
                watched.TranslationLogChanged -= OnLogChanged;
                watched = null;
            }
        };

        return row;
    }
}
