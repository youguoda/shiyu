using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 设置·翻译的「凭据」一行（用户需求 2026-10-10：要记得保存凭据、可以显示、方便切换）。表单上这
/// 一家存过凭据：打码显示，旁边「显示 / 复制 / 更换 / 删除」；没存过、或正在更换：输入框 +「保存
/// 凭据」。完整的密钥只在点了「显示」时出现，换到别家就收起；它也只是看的——不能选中，复制一律走
/// 「复制」：拾语自己的写入口，带排除标记，不进拾语的历史，也不进 Windows 剪贴板历史与别的剪贴板工具。
/// </summary>
internal sealed class CredentialEditor
{
    private readonly PasswordBox _box;
    private readonly Func<string> _formBaseUrl;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _save;
    private readonly Action<SavedProvider> _remove;
    private readonly Func<string, bool> _copy;

    private readonly StackPanel _root = new();
    private readonly StackPanel _savedRow = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _entryRow = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _shown = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 300,
        Margin = new Thickness(0, 0, 10, 0),
    };

    private readonly Button _reveal = Action("显示", "显示完整的凭据（再点收起）");
    private readonly Button _copyButton = Action("复制", "复制凭据：不进拾语的历史，也不进 Windows 剪贴板历史");
    private readonly Button _replace = Action("更换", "填一把新的凭据替换这一把");
    private readonly Button _delete = Action("删除", "删除这家服务商的凭据");
    private readonly Button _saveButton = Action("保存凭据", null);
    private readonly Button _cancel = Action("取消", "不更换了，继续用已保存的凭据");

    private DispatcherTimer? _copiedReset;
    private bool _revealed;
    private bool _replacing;
    private string? _origin;

    /// <param name="box">凭据输入框（设置窗的 Password 编辑器，表单的"填了什么"读它）。</param>
    /// <param name="formBaseUrl">表单上此刻的服务地址（可能还没落盘）。</param>
    /// <param name="settings">已存的设置。</param>
    /// <param name="save">把框里的凭据存下；存成了返回 true。</param>
    /// <param name="remove">删除一家（宿主负责先问一句）。</param>
    /// <param name="copy">把凭据放上剪贴板；放成了返回 true。</param>
    public CredentialEditor(
        PasswordBox box,
        Func<string> formBaseUrl,
        Func<AppSettings> settings,
        Func<bool> save,
        Action<SavedProvider> remove,
        Func<string, bool> copy)
    {
        _box = box;
        _formBaseUrl = formBaseUrl;
        _settings = settings;
        _save = save;
        _remove = remove;
        _copy = copy;

        _shown.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        _shown.SetResourceReference(TextBlock.FontSizeProperty, "Type.Body");

        _savedRow.Children.Add(_shown);
        _savedRow.Children.Add(_reveal);
        _savedRow.Children.Add(_copyButton);
        _savedRow.Children.Add(_replace);
        _savedRow.Children.Add(_delete);

        _box.Width = 220;
        _saveButton.Margin = new Thickness(6, 0, 0, 0);
        _entryRow.Children.Add(_box);
        _entryRow.Children.Add(_saveButton);
        _entryRow.Children.Add(_cancel);

        _root.Children.Add(_savedRow);
        _root.Children.Add(_entryRow);

        _reveal.Click += (_, _) =>
        {
            _revealed = !_revealed;
            Refresh();
        };
        _copyButton.Click += (_, _) => Copy();
        _replace.Click += (_, _) =>
        {
            _replacing = true;
            _revealed = false;
            Refresh();
            _box.Focus();
        };
        _cancel.Click += (_, _) =>
        {
            _replacing = false;
            _box.Clear();
            Refresh();
        };
        _delete.Click += (_, _) =>
        {
            if (Current() is { } saved)
            {
                _remove(saved);
            }
        };
        _saveButton.Click += (_, _) =>
        {
            if (_save())
            {
                _replacing = false;
                Refresh();
            }
        };

        Refresh();
    }

    public FrameworkElement Element => _root;

    /// <summary>
    /// 按表单此刻的地址与已存的设置重判：这一家存过就打码显示，否则是输入框。换了一家就收起
    /// 已显示的密钥、放弃进行到一半的更换。
    /// </summary>
    public void Refresh()
    {
        var saved = Current();
        if (!string.Equals(saved?.Origin, _origin, StringComparison.OrdinalIgnoreCase))
        {
            _origin = saved?.Origin;
            _revealed = false;
            _replacing = false;
        }

        var showSaved = saved is not null && !_replacing;
        _savedRow.Visibility = showSaved ? Visibility.Visible : Visibility.Collapsed;
        _entryRow.Visibility = showSaved ? Visibility.Collapsed : Visibility.Visible;
        _cancel.Visibility = saved is not null && _replacing ? Visibility.Visible : Visibility.Collapsed;

        if (saved is null)
        {
            return;
        }

        _shown.Text = _revealed ? saved.ApiKey : CredentialMask.Of(saved.ApiKey);
        _reveal.Content = _revealed ? "收起" : "显示";
        System.Windows.Automation.AutomationProperties.SetName(
            _shown, _revealed ? "凭据（已显示）" : "凭据（已打码）");
    }

    private SavedProvider? Current() => _settings().ProviderFor(_formBaseUrl());

    private void Copy()
    {
        if (Current() is not { } saved)
        {
            return;
        }

        _copyButton.Content = _copy(saved.ApiKey) ? "已复制" : "复制失败";
        _copiedReset?.Stop();
        _copiedReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _copiedReset.Tick += (_, _) =>
        {
            _copiedReset?.Stop();
            _copyButton.Content = "复制";
        };
        _copiedReset.Start();
    }

    private static Button Action(string text, string? tip)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tip is not null)
        {
            button.ToolTip = tip;
        }

        return button;
    }
}
