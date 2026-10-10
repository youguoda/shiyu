using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 服务页与首次引导共用的「服务商预设」行（票 08）：预设下拉（默认四家、
/// 「更多」里的 Kimi、以及「自定义」）、「申请密钥」直达服务商、「测试
/// 连接」三态人话。设置窗与引导窗渲染同一棵树、同一批编辑器——这一行
/// 也只有这一份定义，谁也不许抄走。
///
/// <paramref name="state"/>.Text 承载当前预设 Id（空串=自定义），随保存
/// 走 <see cref="SettingsBindings.Apply"/>；地址与模型由各自的行自己写，
/// 这里只负责选中即代填、手改即降级为自定义（数据层另有
/// <see cref="ProviderPresets.ResolveFor"/> 兜底）。
///
/// 票 29：表单的值经 <see cref="ServiceForm"/> 交进来——"凭据留空 = 沿用已
/// 存的"只沿用属于这个来源的那把。已存的密钥属于别家时，<see cref="KeyHint"/>
/// 在凭据框下方说明，「测试连接」给出第四种结果而不发请求。
/// </summary>
internal sealed class ServicePresetRow
{
    private const string CustomTag = "";

    private readonly ItemState _state;
    private readonly Func<ServiceForm> _readForm;
    private readonly Action<ProviderPreset> _applyPreset;

    private readonly ComboBox _picker = new() { MinWidth = 230, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _keyLink = new()
    {
        Content = "申请密钥 ↗",
        Padding = new Thickness(10, 3, 10, 3),
        Margin = new Thickness(8, 0, 0, 0),
        Cursor = Cursors.Hand,
        Visibility = Visibility.Collapsed,
        ToolTip = "到服务商的控制台申请一个 API 密钥（在浏览器里打开）",
    };
    private readonly Button _test = new()
    {
        Content = "测试连接",
        Padding = new Thickness(10, 3, 10, 3),
        Cursor = Cursors.Hand,
        ToolTip = "发一条极小的真实请求，验证地址、密钥与模型",
    };
    private readonly TextBlock _result = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    // 凭据框下方的提示（票 29）：话 + 该预设的「申请密钥」链接。
    private readonly TextBlock _keyHintText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };
    private readonly Button _keyHintLink = new()
    {
        Content = "申请密钥 ↗",
        Padding = new Thickness(10, 3, 10, 3),
        Margin = new Thickness(0, 6, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        Cursor = Cursors.Hand,
        Visibility = Visibility.Collapsed,
        ToolTip = "到服务商的控制台申请一个 API 密钥（在浏览器里打开）",
    };

    private bool _syncing;

    public FrameworkElement Element { get; }

    /// <summary>
    /// 已存的密钥与表单上的服务商对不上时的提示："已保存的密钥属于 {旧主机}，
    /// 请填写 {当前服务商} 的密钥"，外加该预设的「申请密钥」链接；对得上就收起。
    /// 宿主窗口把它放在自己的凭据框下面，并在凭据框内容或已存设置变化时调
    /// <see cref="RefreshKeyHint"/>（地址、预设的变化这一行自己知道）。
    /// </summary>
    public FrameworkElement KeyHint { get; }

    public ServicePresetRow(
        AppSettings baseline,
        ItemState state,
        Func<ServiceForm> readForm,
        Action<ProviderPreset> applyPreset)
    {
        _state = state;
        _readForm = readForm;
        _applyPreset = applyPreset;

        BuildPicker(baseline);
        _keyLink.Click += (_, _) => OpenKeyUrl();
        _keyHintLink.Click += (_, _) => OpenKeyUrl();
        _test.Click += async (_, _) => await TestConnectionAsync();
        Element = Build();
        KeyHint = BuildKeyHint();
    }

    private FrameworkElement BuildKeyHint()
    {
        _keyHintText.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
        _keyHintText.SetResourceReference(TextBlock.LineHeightProperty, "Line.CaptionMulti");
        _keyHintText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        // 宽度封顶：凭据卡的控件列是 Auto 宽，不封顶这句话会把卡片撑出窗外。
        var panel = new StackPanel
        {
            MaxWidth = 320,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(_keyHintText);
        panel.Children.Add(_keyHintLink);
        return panel;
    }

    /// <summary>
    /// 按表单此刻的值重判提示：密钥框里正填着东西、已存的密钥配得上这个
    /// 来源、或根本没有已存的密钥，都不出声。
    /// </summary>
    public void RefreshKeyHint()
    {
        var preset = ProviderPresets.Find(_state.Text);
        var hint = _readForm().KeyHint(preset?.DisplayName);

        KeyHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        if (hint is null)
        {
            return;
        }

        _keyHintText.Text = hint;

        // 自定义地址没有"申请密钥"可指——链接只属于预设。
        _keyHintLink.Visibility = preset is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement Build()
    {
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal };
        pickRow.Children.Add(_picker);
        pickRow.Children.Add(_keyLink);

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        actionRow.Children.Add(_test);

        var panel = new StackPanel();
        panel.Children.Add(pickRow);
        panel.Children.Add(actionRow);
        panel.Children.Add(_result);
        return panel;
    }

    private void BuildPicker(AppSettings baseline)
    {
        _picker.Items.Add(Header("常用"));
        foreach (var preset in ProviderPresets.Primary)
        {
            _picker.Items.Add(Option(preset));
        }

        if (ProviderPresets.More.Count > 0)
        {
            _picker.Items.Add(Header("更多"));
            foreach (var preset in ProviderPresets.More)
            {
                _picker.Items.Add(Option(preset));
            }
        }

        _picker.Items.Add(new ComboBoxItem { Content = "自定义", Tag = CustomTag });

        _picker.SelectionChanged += OnPicked;

        // 初选按地址与模型的现状反查，而不是文件里的 Id：手改过的配置
        // 打开窗就该看见「自定义」，别拿一个失效的预设名糊弄人。提示此刻还没
        // 建出来（构造的后一步），只同步下拉。
        SyncPicker(baseline);

        static ComboBoxItem Option(ProviderPreset preset) => new()
        {
            Content = preset.DisplayName,
            Tag = preset.Id,
            ToolTip = $"{preset.BaseUrl} · {preset.DefaultModel}",
        };

        static ComboBoxItem Header(string text) => new()
        {
            Content = text,
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        };
    }

    /// <summary>
    /// 跟着已存的设置走（用户需求 2026-10-10）：存过凭据的预设名后标「✓ 已保存」；别处切换了
    /// 服务商（「已保存的服务商」里的「切换」、导入备份），选中项随之换过来。不触发选中预设的代填。
    /// </summary>
    public void Follow(AppSettings settings)
    {
        SyncPicker(settings);
        RefreshKeyHint();
    }

    private void SyncPicker(AppSettings settings)
    {
        _syncing = true;
        foreach (var item in _picker.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string id && ProviderPresets.Find(id) is { } preset)
            {
                item.Content = settings.ProviderFor(preset.BaseUrl) is null
                    ? preset.DisplayName
                    : $"{preset.DisplayName}  ✓ 已保存";
            }
        }

        var current = Match(settings.BackendBaseUrl, settings.BackendModel);
        _state.Text = current?.Id ?? CustomTag;

        // 改过内容的选中项不会自己重画选择框：先放开再选回来。
        _picker.SelectedItem = null;
        SelectByTag(_state.Text);
        _keyLink.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        _syncing = false;
    }

    /// <summary>地址或模型被改动后由宿主窗口调来：与所选预设不符就降级为「自定义」。</summary>
    public void NoteAddressEdited()
    {
        // 地址一变，已存的密钥还配不配得上它就要重判（票 29）。
        RefreshKeyHint();

        if (_syncing || _state.Text.Length == 0)
        {
            return;
        }

        var preset = ProviderPresets.Find(_state.Text);
        var form = _readForm();
        if (preset is null
            || form.BaseUrl.TrimEnd('/') != preset.BaseUrl
            || (form.Model.Trim() != preset.DefaultModel
                && !preset.AltModels.Contains(form.Model.Trim(), StringComparer.Ordinal)))
        {
            DemoteToCustom();
        }
    }

    private void OnPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || e.AddedItems.Count == 0)
        {
            return;
        }

        if (e.AddedItems[0] is not ComboBoxItem { IsEnabled: true } item
            || item.Tag is not string id)
        {
            return;
        }

        if (id.Length == 0)
        {
            _state.Text = CustomTag;
            _keyLink.Visibility = Visibility.Collapsed;
            RefreshKeyHint();
            return;
        }

        if (ProviderPresets.Find(id) is not { } preset)
        {
            return;
        }

        _state.Text = id;
        _keyLink.Visibility = Visibility.Visible;
        _applyPreset(preset);

        // 换了服务商：已存的密钥配不配得上它，在预设落定之后重判（票 29）。
        RefreshKeyHint();
    }

    private void DemoteToCustom()
    {
        _syncing = true;
        _state.Text = CustomTag;
        SelectByTag(CustomTag);
        _keyLink.Visibility = Visibility.Collapsed;
        _syncing = false;

        // 提示里的"当前服务商"从预设名换成了地址里的主机。
        RefreshKeyHint();
    }

    private void SelectByTag(string tag)
    {
        foreach (var item in _picker.Items.OfType<ComboBoxItem>()
                     .Where(item => item.IsEnabled && item.Tag is string))
        {
            if ((string)item.Tag == tag)
            {
                _picker.SelectedItem = item;
                return;
            }
        }
    }

    private static ProviderPreset? Match(string baseUrl, string model)
        => ProviderPresets.All.FirstOrDefault(preset =>
            baseUrl.TrimEnd('/') == preset.BaseUrl
            && (model.Trim() == preset.DefaultModel
                || preset.AltModels.Contains(model.Trim(), StringComparer.Ordinal)));

    private void OpenKeyUrl()
    {
        if (ProviderPresets.Find(_state.Text) is not { } preset)
        {
            return;
        }

        try
        {
            // .NET 9 的 Process 持有操作系统句柄直到终结器回收（O-43）：
            // 打开即弃，浏览器照常活，句柄当场还。
            using var opened = Process.Start(
                new ProcessStartInfo(preset.ApiKeyUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // expected: 打不开浏览器——这行链接是配置流程的一部分，界面上
            // 说清去哪里就够了，不需要日志。
            ShowResult("打不开浏览器，请手动访问服务商的控制台申请密钥。", "Brush.TextSecondary");
        }
    }

    private async Task TestConnectionAsync()
    {
        // 第四种结果（已存的密钥属于别家、框又空着）有自己的话，且不算失败：
        // 不在"没填好"的老话之列，交给探针去答。
        var form = _readForm();
        if (!form.NeedsOwnKey
            && (string.IsNullOrWhiteSpace(form.BaseUrl)
                || string.IsNullOrWhiteSpace(form.Model)
                || string.IsNullOrWhiteSpace(form.Key)))
        {
            ShowResult("请先填好服务地址、模型与密钥，再测试连接。", "Brush.TextSecondary");
            return;
        }

        _test.IsEnabled = false;
        ShowResult("正在测试……", "Brush.TextSecondary");

        // 与真翻译同一条路：预设的附加字段与温度规则一并生效（票 08）。
        // 密钥只进 Authorization 头，不进结果文案。默认首字节 15 秒恰好是
        // 测试连接想要的等待上限（O-23 之后不再有总时长语义）。
        var outcome = await ConnectionProbe.TestAsync(
            form,
            ProviderPresets.Find(_state.Text),
            httpClient: HttpClients.Shared);

        _test.IsEnabled = true;
        ShowResult(
            Describe(outcome),
            outcome.Verdict switch
            {
                ConnectionTestVerdict.Success => "Brush.Accent",
                ConnectionTestVerdict.NeedsKey => "Brush.TextSecondary",
                ConnectionTestVerdict.InvalidKey => "Brush.Danger",
                _ => "Brush.Danger",
            });
    }

    private static string Describe(ConnectionTestOutcome outcome)
    {
        if (outcome.Message is { Length: > 0 })
        {
            // 百炼免费额度耗尽之类的"成功但有话要说"。
            return outcome.Verdict == ConnectionTestVerdict.Success
                ? $"连接成功（{(int)outcome.Elapsed!.Value.TotalMilliseconds:0} ms）。{outcome.Message}"
                : outcome.Message;
        }

        return outcome.Verdict == ConnectionTestVerdict.Success
            ? $"连接成功（{(int)outcome.Elapsed!.Value.TotalMilliseconds:0} ms），配置可用。"
            : "测试失败。";
    }

    private void ShowResult(string text, string brush)
    {
        _result.Text = text;
        _result.Visibility = Visibility.Visible;
        _result.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
