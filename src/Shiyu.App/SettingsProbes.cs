#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>设置窗的调试探针区（只在 Debug 构建存在，同 App.DebugProbes.cs）。</summary>
public partial class SettingsWindow
{
    /// <summary>
    /// 探针命令 credentials（用户需求 2026-10-10：记得保存凭据、可以显示、配过哪些服务商、方便切换）。
    /// 设置里已经存着 DeepSeek 与智谱两家、当前在智谱上。依次看：凭据卡打码、点「显示」看全、再收起；
    /// 预设下拉的「✓ 已保存」；已保存列表与「当前使用」；在列表里切到 DeepSeek 后表单、下拉、凭据卡
    /// 都跟过来；在下拉里挑一家没存过的，凭据卡变回输入框、提示说这家还没有。日志里不出现任何密钥。
    /// </summary>
    internal async Task<string> ProbeCredentials(string picture)
    {
        var log = new StringBuilder();

        async Task Settle()
        {
            for (var round = 0; round < 4; round++)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
        }

        string Card()
        {
            var editor = _credential!;
            var root = (StackPanel)editor.Element;
            var saved = (StackPanel)root.Children[0];
            var entry = (StackPanel)root.Children[1];
            var shown = (TextBlock)saved.Children[0];
            var reveal = (Button)saved.Children[1];
            var masked = !shown.Text.Contains("probe", StringComparison.Ordinal);
            return FormattableString.Invariant(
                $"saved={saved.Visibility == Visibility.Visible}|entry={entry.Visibility == Visibility.Visible}|masked={masked}|length={shown.Text.Length}|reveal={ButtonWord(reveal)}");
        }

        string Picker()
        {
            var picker = Descendants(this).OfType<ComboBox>()
                .First(box => box.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, "deepseek")));
            var marked = picker.Items.OfType<ComboBoxItem>()
                .Where(item => item.Content is string text && text.EndsWith("✓ 已保存", StringComparison.Ordinal))
                .Select(item => item.Tag)
                .ToList();
            return FormattableString.Invariant(
                $"selected={(picker.SelectedItem as ComboBoxItem)?.Tag}|marked={string.Join("+", marked)}");
        }

        string List()
        {
            var lines = _savedProviders!.Children.OfType<Grid>().ToList();
            var current = lines.Select((line, index) => (line, index))
                .Where(pair => System.Windows.Automation.AutomationProperties.GetName(pair.line).EndsWith("当前使用", StringComparison.Ordinal))
                .Select(pair => pair.index)
                .ToList();
            return FormattableString.Invariant($"lines={lines.Count}|current={string.Join("+", current)}");
        }

        await Settle();
        log.AppendLine("card|" + Card());
        log.AppendLine("picker|" + Picker());
        log.AppendLine("list|" + List());

        // 显示 → 看全；再点 → 收起。
        var savedRow = (StackPanel)((StackPanel)_credential!.Element).Children[0];
        var reveal = (Button)savedRow.Children[1];
        reveal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Settle();
        var shownFull = ((TextBlock)savedRow.Children[0]).Text == _store.Current.KeyFor(_store.Current.BackendBaseUrl);
        log.AppendLine(FormattableString.Invariant($"revealed|full={shownFull}|reveal={ButtonWord(reveal)}"));
        reveal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Settle();
        log.AppendLine("hidden|" + Card());

        // 在列表里切到 DeepSeek。
        var deepSeek = _store.Current.SavedProviders.First(provider => provider.PresetId == "deepseek");
        SwitchToProvider(deepSeek);
        await Settle();
        var url = _textBoxes["service.base-url"].Text;
        var model = _textBoxes["service.model"].Text;
        log.AppendLine(FormattableString.Invariant(
            $"switched|url={url}|model={model}|configured={_store.Current.Backend.IsConfigured}"));
        log.AppendLine("switchedCard|" + Card());
        log.AppendLine("switchedPicker|" + Picker());
        log.AppendLine("switchedList|" + List());

        // 在下拉里挑一家没存过的（百炼）：凭据卡变回输入框，提示说这家还没有。
        var picker = Descendants(this).OfType<ComboBox>()
            .First(box => box.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, "deepseek")));
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, "bailian"));
        await Settle();
        var hint = _presetRow!.KeyHint;
        log.AppendLine(FormattableString.Invariant(
            $"fresh|configured={_store.Current.Backend.IsConfigured}|hint={hint.Visibility == Visibility.Visible}"));
        log.AppendLine("freshCard|" + Card());

        // 切回 DeepSeek，渲一张看得见两家都在的图。
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, "deepseek"));
        await Settle();
        JumpToItem("service.api-key");
        await Settle();
        if (Content is FrameworkElement root)
        {
            App.RenderToPng(root, picture);
        }

        return log.ToString();
    }

    /// <summary>「显示 / 收起」钮此刻的字，写成 ASCII 给探针脚本（脚本只认 ASCII）。</summary>
    private static string ButtonWord(Button reveal) => Equals(reveal.Content, "显示") ? "show" : "hide";

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }
}
#endif
