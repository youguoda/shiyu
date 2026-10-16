using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 键帽录制器（§5.1/§5.3，票 25）：当前组合键以一串键帽显示，点一下、
/// 按下组合键，即录即校验——与其它全局键的撞车在录制时就点名，而不是等
/// 注册失败后托盘里冒一句。Esc 取消；「清除」把键位拿掉（空串 = 不注册）。
/// 键帽文本由 <see cref="KeyMap.Chips"/> 拆分，与速查区同一渲染。
/// </summary>
internal sealed class KeyCapRecorder
{
    private readonly Window _window;
    private readonly Func<string, string?> _validate;
    private readonly Action<string?> _changed;

    private readonly Border _plate = new();
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly Button _clear = new()
    {
        Content = "清除",
        Padding = new Thickness(10, 3, 10, 3),
        Margin = new Thickness(8, 0, 0, 0),
        Cursor = Cursors.Hand,
        ToolTip = "拿掉这个快捷键（不再注册）",
    };
    private readonly TextBlock _error = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    private bool _listening;

    /// <param name="window">键盘事件挂在窗口层面接（录制器本身不抢焦点）。</param>
    /// <param name="initial">初始组合键文本；空串 = 未设置。</param>
    /// <param name="changed">录制成功或清除后的新值（null = 已清除）。</param>
    /// <param name="validate">录制时的即时校验：返回错误文案或 null。</param>
    public KeyCapRecorder(
        Window window,
        string initial,
        Action<string?> changed,
        Func<string, string?>? validate = null)
    {
        _window = window;
        _changed = changed;
        _validate = validate ?? (_ => null);
        Value = initial.Trim().Length == 0 ? null : initial.Trim();

        _plate.Cursor = Cursors.Hand;
        _plate.Padding = new Thickness(10, 6, 10, 6);
        _plate.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceInput");
        _plate.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _plate.BorderThickness = new Thickness(1);
        _plate.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        _plate.ToolTip = "点击后直接按下组合键（Esc 取消）";
        _plate.Child = _chips;
        _plate.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            BeginCapture();
        };

        _clear.SetResourceReference(FrameworkElement.StyleProperty, "FlyoutButton");
        _clear.Click += (_, _) =>
        {
            Value = null;
            Paint();
            ShowError(null);
            _changed(null);
        };

        _error.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");

        Paint();
    }

    /// <summary>当前组合键文本；null = 未设置。</summary>
    public string? Value { get; private set; }

    /// <summary>是否正在等用户按键（宿主窗口据此把 Esc/Enter 让给录制器）。</summary>
    public bool IsListening => _listening;

    /// <summary>建出可视树（延迟到属性初始化之后）。</summary>
    public FrameworkElement Build()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_plate);
        row.Children.Add(_clear);

        var panel = new StackPanel();
        panel.Children.Add(row);
        panel.Children.Add(_error);
        return panel;
    }

    /// <summary>进入录制：边框亮起，下一次组合键落进来。已在录制则忽略。</summary>
    public void BeginCapture()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        _plate.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        _chips.Children.Clear();
        _chips.Children.Add(Placeholder("按下组合键…"));
        ShowError(null);

        _window.PreviewKeyDown += OnWindowKeyDown;
    }

    private void EndCapture()
    {
        _listening = false;
        _window.PreviewKeyDown -= OnWindowKeyDown;
        _plate.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        Paint();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!_listening)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        EndCapture();

        if (key == Key.Escape)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            ShowError("全局快捷键至少要带一个修饰键（Ctrl/Shift/Alt/Win）。");
            return;
        }

        char? letter = key switch
        {
            >= Key.A and <= Key.Z => (char)('A' + (key - Key.A)),
            >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
            _ => null,
        };

        if (letter is not { } digit)
        {
            ShowError("只支持字母/数字键加修饰键（F 键与标点注册不了）。");
            return;
        }

        // 与 HotkeySpec 的写法同序：Win 在最前（评审 2026-10-16 F-04 起默认键是 Win+Alt 组）。
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(digit.ToString());
        var combination = string.Join("+", parts);

        // 即录即校验（HotkeyPlan 的四键互撞在这里点名，不留到注册失败）。
        var problem = _validate(combination);
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        Value = combination;
        ShowError(null);
        Paint();
        _changed(combination);
    }

    private void Paint()
    {
        _chips.Children.Clear();
        if (Value is null)
        {
            _chips.Children.Add(Placeholder("未设置"));
        }
        else
        {
            foreach (var chip in KeyMap.Chips(Value))
            {
                var cap = new ContentControl { Content = chip };
                cap.SetResourceReference(FrameworkElement.StyleProperty, "KeyCap");
                cap.Margin = new Thickness(0, 0, 2, 0);
                _chips.Children.Add(cap);
            }
        }
    }

    private static TextBlock Placeholder(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(TextElement.FontSizeProperty, "Type.Caption");
        block.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        return block;
    }

    private void ShowError(string? problem)
    {
        if (problem is null)
        {
            _error.Text = string.Empty;
            _error.Visibility = Visibility.Collapsed;
            return;
        }

        _error.Text = problem;
        _error.Visibility = Visibility.Visible;
    }
}
