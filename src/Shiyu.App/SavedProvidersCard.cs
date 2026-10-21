using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 已保存的服务商（用户需求 2026-10-10：配过的一目了然，点一下就切过去）。每家一行：名字、模型与打码的
/// 凭据；正在用的那家标「当前使用」，别家给「切换」；都能删。跟着设置走——存了、删了、切了，设置窗
/// 把新设置交给 <see cref="Refresh"/> 重画。切换与删除要写设置、要问人，由设置窗做。
/// </summary>
internal sealed class SavedProvidersCard
{
    private readonly Action<SavedProvider> _switchTo;
    private readonly Action<SavedProvider> _remove;

    public StackPanel Element { get; } = new();

    public SavedProvidersCard(AppSettings baseline, Action<SavedProvider> switchTo, Action<SavedProvider> remove)
    {
        _switchTo = switchTo;
        _remove = remove;
        Refresh(baseline);
    }

    public void Refresh(AppSettings settings)
    {
        Element.Children.Clear();
        if (settings.SavedProviders.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "还没有保存过凭据。在上面选好服务商、填好凭据并保存后，会出现在这里。",
                TextWrapping = TextWrapping.Wrap,
            };
            empty.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            Element.Children.Add(empty);
            return;
        }

        foreach (var provider in settings.SavedProviders)
        {
            Element.Children.Add(Line(
                provider, current: KeyOrigin.Same(settings.BackendBaseUrl, provider.Origin)));
        }
    }

    private FrameworkElement Line(SavedProvider provider, bool current)
    {
        var line = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var marker = new TextBlock
        {
            Text = current ? "●" : "○",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        marker.SetResourceReference(TextBlock.ForegroundProperty, current ? "Brush.Accent" : "Brush.TextTertiary");
        line.Children.Add(marker);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = provider.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = new TextBlock
        {
            Text = provider.Model.Length > 0
                ? $"{provider.Model}  ·  {CredentialMask.Of(provider.ApiKey)}"
                : CredentialMask.Of(provider.ApiKey),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        detail.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        words.Children.Add(detail);
        Grid.SetColumn(words, 1);
        line.Children.Add(words);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (current)
        {
            var using_ = new TextBlock
            {
                Text = "当前使用",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            using_.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
            using_.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
            actions.Children.Add(using_);
        }
        else
        {
            var switchTo = new Button
            {
                Content = "切换",
                Padding = new Thickness(10, 3, 10, 3),
                Cursor = Cursors.Hand,
                ToolTip = $"改用 {provider.DisplayName}：地址、模型和凭据一起切过去",
            };
            switchTo.Click += (_, _) => _switchTo(provider);
            actions.Children.Add(switchTo);
        }

        var remove = new Button
        {
            Content = "删除",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        remove.Click += (_, _) => _remove(provider);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 2);
        line.Children.Add(actions);

        System.Windows.Automation.AutomationProperties.SetName(
            line, current ? $"{provider.DisplayName}，当前使用" : provider.DisplayName);
        return line;
    }
}
