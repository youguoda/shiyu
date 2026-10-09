using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// Owns the application-level resource dictionaries.
///
/// Two dictionaries are merged at startup: <see cref="ValuesDictionaryName"/>
/// holds every token VALUE, compiled from Core's DesignTokens, and
/// Themes/Controls.xaml holds the control styles that reference those tokens
/// dynamically. Switching themes swaps only the value dictionary;
/// DynamicResource references then resolve to the new brushes in place — no
/// window is rebuilt and nothing flickers.
///
/// Merging is done in code rather than in App.xaml on purpose: this machine's
/// PresentationBuildTasks emits an App.g.cs without InitializeComponent, where
/// App.xaml markup — including declared resources — never loads (recorded in
/// ticket 02).
/// </summary>
internal sealed class ThemeManager : IDisposable
{
    private const string ControlsSource = "/Themes/Controls.xaml";

    /// <summary>
    /// The brand marks (Brand.Mark, Brand.Mark.Large): vector DrawingImages drawn
    /// from the same source as app.ico by tools/icon/render-icon.ps1 -Xaml. The
    /// mark reads on light and dark alike, so it is merged once rather than swapped
    /// per palette like the brushes.
    /// </summary>
    private const string BrandMarkSource = "/Assets/BrandMark.xaml";

    private readonly MessageWindow _messages;

    private AppTheme _mode;
    private string _applied;
    private ThemePalette _palette = DesignTokens.Light;

    /// <summary>Whether the palette currently applied is Dark — Backdrop reads it.</summary>
    public static bool CurrentIsDark { get; private set; }

    /// <summary>
    /// The content type ramp in force（内容字号，ADR-0012 排版 2）. XAML reaches it through
    /// the Type.Content* / Line.Content* resources; code that measures reading text with
    /// FormattedText or clamps by whole lines reads it here, so the arithmetic matches what
    /// the resources render.
    /// </summary>
    public static ContentRamp Content { get; private set; } = ContentRamp.For(ContentFontSize.Standard);

    /// <summary>
    /// While the mode is System, Windows itself is watched — through the
    /// <c>WM_SETTINGCHANGE</c> broadcasts the hidden window already receives
    /// (O-37), the same channel the clipboard notifications ride on. The old
    /// five-second registry poll kept the process waking at two ticks a
    /// minute to learn, almost always, that nothing had changed; the
    /// broadcast arrives only when there is news, and it is delivered on
    /// this dispatcher's own thread, which is where a palette swap must
    /// happen anyway.
    /// </summary>
    public ThemeManager(MessageWindow messages)
    {
        _messages = messages;
        _applied = string.Empty;

        Application.Current.Resources.MergedDictionaries.Add(BuildValues(DesignTokens.Light));
        Application.Current.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri(ControlsSource, UriKind.Relative) });
        Application.Current.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri(BrandMarkSource, UriKind.Relative) });

        _messages.MessageReceived += OnSystemMessage;
    }

    private void OnSystemMessage(WindowMessage message)
    {
        if (message.Id != MessageWindow.SettingChangeMessage
            || !ThemeFollowPolicy.ShouldRecheck(_mode, Area(message.LParam)))
        {
            return;
        }

        // Reading the registry once per broadcast is cheap; swapping only on
        // a real change keeps an idle theme alone. Some broadcasts name no
        // area at all — the policy treats those as possibly ours rather than
        // ignoring a theme flip the sender forgot to label.
        FollowSystemIfItMoved();

        static string? Area(IntPtr lParam)
            => lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);
    }

    /// <summary>Applies the mode now; while it is System, keeps following Windows live.</summary>
    public void Apply(AppTheme mode)
    {
        _mode = mode;
        SwapTo(Resolve(mode));
    }

    /// <summary>
    /// 内容字号即时生效（用户需求 2026-10-09）：只换内容这一族令牌，控件字号原样——
    /// 值字典整份重建，与换调色板同一条路，每个 DynamicResource 一次拿到新值。
    /// </summary>
    public void ApplyContentSize(ContentFontSize size)
    {
        var ramp = ContentRamp.For(size);
        if (ramp == Content)
        {
            return;
        }

        Content = ramp;
        Application.Current.Resources.MergedDictionaries[0] = BuildValues(_palette);
    }

    private void FollowSystemIfItMoved()
    {
        var effective = Resolve(_mode);

        // Reading the registry every few seconds is cheap; swapping only on a
        // real change keeps an idle theme alone.
        if (effective.Name != _applied)
        {
            SwapTo(effective);
        }
    }

    private static ThemePalette Resolve(AppTheme mode)
        => mode == AppTheme.Dark || (mode == AppTheme.System && SystemPrefersDark())
            ? DesignTokens.Dark
            : DesignTokens.Light;

    /// <summary>
    /// Windows 10 1607+ records the user's app colour preference here; a missing
    /// value is treated as light, which is the historical default.
    /// </summary>
    internal static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

        return key?.GetValue("AppsUseLightTheme") is int useLight && useLight == 0;
    }

    private void SwapTo(ThemePalette palette)
    {
        _applied = palette.Name;
        _palette = palette;
        CurrentIsDark = palette.Name == "Dark";

        var dictionaries = Application.Current.Resources.MergedDictionaries;

        // Index 0 is the value dictionary this class installed; the control
        // style dictionary after it stays put. Replacing the reference (rather
        // than clearing keys in place) lets WPF hand every DynamicResource the
        // new brushes atomically.
        dictionaries[0] = BuildValues(palette);

        // The DWM backdrop's dark flag rides along with every palette swap:
        // a Mica/Acrylic surface left in the wrong mode is instantly wrong.
        Backdrop.SyncToTheme(CurrentIsDark);
    }

    private static ResourceDictionary BuildValues(ThemePalette palette)
    {
        var values = new ResourceDictionary();

        foreach (var slot in DesignTokens.Slots)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[slot]));
            brush.Freeze();
            values[$"Brush.{slot}"] = brush;
        }

        values["Font.Ui"] = new FontFamily(DesignTokens.FamilyUi);
        values["Font.Mono"] = new FontFamily(DesignTokens.FamilyMono);
        values["Font.Icon"] = new FontFamily(DesignTokens.FamilyIcon);

        // 字阶 v2（票 19 / ADR-0012：控件回 14，内容 18 可调；旧 Font*/Size.* 档已退役）。
        // 内容这一族（正文、等宽、词头、紧凑）来自当下的内容字号（Content），其余固定。
        var content = Content;
        values["Type.Caption"] = DesignTokens.TypeCaption;
        values["Type.Body"] = DesignTokens.TypeBody;
        values["Type.BodyStrong"] = DesignTokens.TypeBodyStrong;
        values["Type.Content"] = content.Content;
        values["Type.ContentMono"] = content.Mono;
        values["Type.ContentCompact"] = content.Compact;
        values["Type.Subtitle"] = DesignTokens.TypeSubtitle;
        values["Type.Title"] = DesignTokens.TypeTitle;
        values["Type.KeyCap"] = DesignTokens.TypeKeyCap;
        values["Type.Headword"] = content.Headword;
        values["Line.Caption"] = DesignTokens.LineForCaption;
        values["Line.CaptionMulti"] = DesignTokens.LineForCaptionMulti;
        values["Line.BodyV2"] = DesignTokens.LineForBody;
        values["Line.BodyMulti"] = DesignTokens.LineForBodyMulti;
        values["Line.Content"] = content.ContentLine;
        values["Line.ContentMono"] = content.MonoLine;
        values["Line.ContentCompact"] = content.CompactLine;
        values["Size.IconXs"] = DesignTokens.IconXs;
        values["Size.IconS"] = DesignTokens.IconS;
        values["Size.IconM"] = DesignTokens.IconM;
        values["Size.IconL"] = DesignTokens.IconL;
        values["Size.IconXl"] = DesignTokens.IconXl;

        // 控件尺寸与语义间距（票 19，UI 报告 §4.2）。
        values["Control.Height"] = DesignTokens.ControlHeight;
        values["Control.HeightCompact"] = DesignTokens.ControlHeightCompact;
        values["Control.HeightDense"] = DesignTokens.ControlHeightDense;
        values["TitleBar.Height"] = DesignTokens.TitleBarHeight;
        values["NavItem.Height"] = DesignTokens.NavItemHeight;
        values["ListRow.Height"] = DesignTokens.ListRowHeight;
        values["ListRow.HeightSingle"] = DesignTokens.ListRowHeightSingle;
        values["SettingsCard.MinHeight"] = DesignTokens.SettingsCardMinHeight;
        values["Space.1"] = new Thickness(DesignTokens.Space1);
        values["Space.2"] = new Thickness(DesignTokens.Space2);
        values["Space.3"] = new Thickness(DesignTokens.Space3);
        values["Space.4"] = new Thickness(DesignTokens.Space4);
        values["Space.5"] = new Thickness(DesignTokens.Space5);
        values["Space.6"] = new Thickness(DesignTokens.Space6);
        values["Space.8"] = new Thickness(DesignTokens.Space8);
        values["Space.12"] = new Thickness(DesignTokens.Space12);

        values["Weight.Emphasis"] = FontWeights.SemiBold;

        foreach (var (name, radius) in DesignTokens.Radius)
        {
            values[$"Radius.{name}"] = new CornerRadius(radius);
        }

        values["Shadow.Floating"] = Shadow(DesignTokens.ShadowFloating);
        values["Shadow.Badge"] = Shadow(DesignTokens.ShadowBadge);

        // The brand mark used to be a palette value here (ticket 39: a light and
        // a dark PNG). It is now one vector mark for both, merged from
        // BrandMarkSource — see the constructor.
        return values;

        static DropShadowEffect Shadow((double Blur, double Depth, double Opacity) spec)
        {
            var effect = new DropShadowEffect
            {
                BlurRadius = spec.Blur,
                ShadowDepth = spec.Depth,
                Opacity = spec.Opacity,
            };
            effect.Freeze();
            return effect;
        }
    }

    public void Dispose() => _messages.MessageReceived -= OnSystemMessage;
}
