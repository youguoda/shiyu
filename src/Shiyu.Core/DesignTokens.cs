namespace Shiyu.Core;

/// <summary>How the interface's colours are chosen.</summary>
// Not "ThemeMode": WPF 9 ships its own System.Windows.ThemeMode, and the
// collision would cost every using file an alias.
public enum AppTheme
{
    /// <summary>Follow the Windows personalization setting, live.</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// 内容字号（ADR-0012 排版 2；用户需求 2026-10-09 落地为设置，并加「特大」一档；2026-10-10
/// 在「小」下面再加「较小」）：被阅读的文字——窄条卡片正文、预览、翻译面板的译文与词头、
/// 反向输入框——跟着它变。控件文字（按钮、标签、输入值、导航）固定 14，不在此列：整体放大
/// 控件字号令牌造成过按钮相撞（ADR-0012 排版 3）。声明顺序就是设置里分段按钮的顺序；设置
/// 文件按名字存，插一档不影响谁已存的选择。
/// </summary>
public enum ContentFontSize
{
    /// <summary>14：与控件文字一样大，正文不会比按钮的字更小。</summary>
    Smaller,

    /// <summary>16。</summary>
    Small,

    /// <summary>18，默认。</summary>
    Standard,

    /// <summary>20。</summary>
    Large,

    /// <summary>22。</summary>
    Larger,
}

/// <summary>
/// One content size's type ramp: the reading size and its line height (the ~1.72 ratio of
/// 18/31), the mono variant (paths, file rows), the dictionary headword (two above the
/// content, so it stays the larger), and the compact variant — one step down — that the
/// reverse input box uses: a small surface beside the caret（用户需求 2026-10-09：反向输入框
/// 有些大）. Standard reproduces the fixed tokens exactly (ContentRampTests holds it).
/// </summary>
public readonly record struct ContentRamp(
    double Content, double ContentLine,
    double Mono, double MonoLine,
    double Headword,
    double Compact, double CompactLine)
{
    public static ContentRamp For(ContentFontSize size) => size switch
    {
        // 等宽字照规律该是 11，但路径在 11 读不清：守在说明文字的 12（ContentRampTests）。
        ContentFontSize.Smaller => new(14, 24, 12, 21, 16, 12, 21),
        ContentFontSize.Small => new(16, 28, 13, 23, 18, 14, 24),
        ContentFontSize.Large => new(20, 34, 17, 29, 22, 18, 31),
        ContentFontSize.Larger => new(22, 38, 19, 33, 24, 20, 34),
        _ => new(18, 31, 15, 26, 20, 16, 28),
    };
}

/// <summary>
/// One theme's colours by semantic slot. Both shipped palettes define exactly
/// the same slots — see DesignTokenTests for the enforcement.
/// </summary>
public sealed record ThemePalette(string Name, IReadOnlyDictionary<string, string> Colors);

/// <summary>
/// Every visual value the interface is allowed to draw with, in one place.
///
/// Tuned for Chinese-first text rather than inherited from English defaults:
/// body line height 1.7 (not 1.5) so unspaced characters do not clump;
/// hierarchy carried by size and colour rather than weight, because Chinese
/// 500/600 are near-indistinguishable at UI sizes; font chains that name a
/// CJK fallback so mixed text does not jump baseline.
/// </summary>
public static class DesignTokens
{
    // --- colour slots ---------------------------------------------------------

    public const string Background = "Background";
    public const string Surface = "Surface";
    public const string SurfaceInput = "SurfaceInput";
    public const string SurfaceSubtle = "SurfaceSubtle";

    /// <summary>
    /// The translucent brand tint drawn over a DWM material (ticket 03's
    /// recipe): the material paints behind the window, this tint keeps text
    /// readable and the brand present. Alpha carries the meaning — an opaque
    /// value here would switch the material off.
    /// </summary>
    public const string SurfaceMaterial = "SurfaceMaterial";

    /// <summary>The elevated, opaque surface for menus and popups.</summary>
    public const string LayerFlyout = "LayerFlyout";

    /// <summary>Interaction-state overlays: drawn over whatever is beneath.</summary>
    public const string StateHover = "StateHover";
    public const string StatePressed = "StatePressed";

    public const string Border = "Border";

    /// <summary>卡片与分区的低对比描边（票 19 / UI 报告 §4.5）：半透明墨色，浮在材质上也成立。</summary>
    public const string CardStroke = "CardStroke";

    /// <summary>承担"识别控件"的描边（输入框底边、开关关态），须过非文本 3:1。</summary>
    public const string StrokeStrong = "StrokeStrong";

    /// <summary>分隔线 = Border 的 50% 透明度——不需要新颜色，只需要一档轻。</summary>
    public const string Divider = "Divider";

    public const string Text = "Text";
    public const string TextSecondary = "TextSecondary";
    public const string TextTertiary = "TextTertiary";
    public const string Accent = "Accent";
    public const string AccentHover = "AccentHover";
    public const string AccentPressed = "AccentPressed";
    public const string AccentFloating = "AccentFloating";
    public const string TextOnAccent = "TextOnAccent";
    public const string Danger = "Danger";

    /// <summary>Danger 的 8% 叠层：危险钮 hover 的底，不引入第五种红色。</summary>
    public const string DangerSubtle = "DangerSubtle";

    /// <summary>文字压 Danger 实底时的颜色（深色主题必须用深字：白字只有 3.09:1）。</summary>
    public const string TextOnDanger = "TextOnDanger";

    /// <summary>列表/导航的选中底：Accent 的一成透明度，配 3×16 指示条（UI 报告 §4.7）。</summary>
    public const string Selected = "Selected";

    /// <summary>键盘焦点外环（双层焦点环的外层；内环用反色防花底）。</summary>
    public const string FocusOuter = "FocusOuter";
    public const string FocusInner = "FocusInner";

    /// <summary>成功反馈（"连接成功""已保存"）。</summary>
    public const string Success = "Success";

    /// <summary>警示反馈（云同步目录、磁盘余量——此前误用 Danger 夸大了等级）。</summary>
    public const string Caution = "Caution";

    /// <summary>品牌色（标识、关于页、引导首屏）。只用于识别时刻，永不用于交互态——与 Danger 同属红系，按钮上会打架。</summary>
    public const string Brand = "Brand";

    /// <summary>The slots every palette must define, and the only slots any window may use.</summary>
    public static readonly string[] Slots =
    [
        Background, Surface, SurfaceInput, SurfaceSubtle, SurfaceMaterial, LayerFlyout,
        StateHover, StatePressed, Border, CardStroke, StrokeStrong, Divider,
        Text, TextSecondary, TextTertiary,
        Accent, AccentHover, AccentPressed, AccentFloating, TextOnAccent,
        AccentSubtle, Selected, Danger, DangerSubtle, TextOnDanger,
        FocusOuter, FocusInner, Success, Caution, Brand,
    ];

    /// <summary>选中态的轻底（分段/列表选中用底色变体，UI 报告 §4.7）。</summary>
    public const string AccentSubtle = "AccentSubtle";

    /// <summary>
    /// 半透明"墨色叠层"槽——alpha 不为 FF 是它们的契约（测试钉住）：
    /// CardStroke/Divider/DangerSubtle/AccentSubtle/Selected 压在材质上才有意义。
    /// </summary>
    public static readonly string[] OverlaySlots =
    [
        CardStroke, Divider, DangerSubtle, AccentSubtle, Selected, StateHover, StatePressed,
    ];

    /// <summary>
    /// Foreground/background combinations the interface actually draws.
    /// Every pair must stay at WCAG AA (4.5) in every theme — the test turns
    /// a palette tweak that quietly ruins a label into a red build.
    /// </summary>
    public static readonly (string Foreground, string Background)[] ReadablePairs =
    [
        (Text, Background),
        (TextSecondary, Background),
        (TextTertiary, Background),
        (Text, Surface),
        (TextSecondary, Surface),
        (TextTertiary, Surface),
        (Text, SurfaceInput),
        (TextSecondary, SurfaceSubtle),

        // 票 18（评审 §3.6）：词典卡例句/同义词曾用 TextTertiary 压在
        // SurfaceSubtle 上，实测 4.33:1——界面已改画次级色，这一对同时入表，
        // 让调色板再也不能悄悄把它调回不足。
        (TextTertiary, SurfaceSubtle),

        // The material tint is translucent: the test blends it over the theme's
        // Background, which is what the acrylic behind the window effectively is.
        (Text, SurfaceMaterial),
        (TextSecondary, SurfaceMaterial),

        (TextOnAccent, Accent),
        (TextOnAccent, AccentHover),
        (TextOnAccent, AccentPressed),
        (Danger, Background),
        (Danger, Surface),

        // 票 19：状态与反馈色上同样要读得出字（TextOnDanger 深浅两版由此各得其所）。
        (TextOnDanger, Danger),
        (Success, Background),
        (Success, Surface),
        (Caution, Background),
        (Caution, Surface),
        (TextSecondary, SurfaceInput),
    ];

    /// <summary>
    /// 非文本对比（WCAG 1.4.11，≥3:1）：控件识别性描边与焦点环。Brand 是
    /// 标识不是信息，刻意不入表。
    /// </summary>
    public static readonly (string Stroke, string Background)[] NonTextPairs =
    [
        (StrokeStrong, Surface),
        (FocusOuter, Background),
    ];

    public static ThemePalette Light { get; } = new("Light", new Dictionary<string, string>
    {
        [Background] = "#FFF7F8FA",
        [Surface] = "#FFFFFFFF",
        [SurfaceInput] = "#FFEAECEF",
        [SurfaceSubtle] = "#FFE9EDF1",
        [SurfaceMaterial] = "#D9FCFCFD",
        [LayerFlyout] = "#FFFDFDFE",
        [StateHover] = "#0D1F2328",
        [StatePressed] = "#171F2328",
        [Border] = "#FFD0D7DE",
        [CardStroke] = "#0F000000",
        [StrokeStrong] = "#FF848D97",
        [Divider] = "#80D0D7DE",
        [Text] = "#FF1F2328",
        [TextSecondary] = "#FF57606A",
        // 票 18：原 #FF62707B 在 SurfaceSubtle 上只有 4.33:1；加深一档到
        // 4.59:1，让上面新入表的 (TextTertiary, SurfaceSubtle) 过 AA。
        [TextTertiary] = "#FF5E6C77",
        [Accent] = "#FF1A66DB",
        [AccentHover] = "#FF2A6FD8",
        [AccentPressed] = "#FF1557C4",
        [AccentFloating] = "#EE1A66DB",
        [TextOnAccent] = "#FFFFFFFF",
        [Danger] = "#FFC0392B",
        [DangerSubtle] = "#14C0392B",
        [TextOnDanger] = "#FFFFFFFF",
        [Selected] = "#1A1A66DB",
        [AccentSubtle] = "#0F1A66DB",
        [FocusOuter] = "#FF1F2328",
        [FocusInner] = "#FFFFFFFF",
        [Success] = "#FF1A7F37",
        [Caution] = "#FF9A6700",
        [Brand] = "#FFFF1A66",
    });

    public static ThemePalette Dark { get; } = new("Dark", new Dictionary<string, string>
    {
        [Background] = "#FF1C2128",
        [Surface] = "#FF22272E",
        [SurfaceInput] = "#FF2A3038",
        [SurfaceSubtle] = "#FF262C34",
        [SurfaceMaterial] = "#E01E242C",
        [LayerFlyout] = "#FF252B33",
        [StateHover] = "#0DE6E8EB",
        [StatePressed] = "#17E6E8EB",
        [Border] = "#FF3D444D",
        [CardStroke] = "#19FFFFFF",
        [StrokeStrong] = "#FF768390",
        [Divider] = "#803D444D",
        [Text] = "#FFE6E8EB",
        [TextSecondary] = "#FFB5BCC4",
        [TextTertiary] = "#FF9BA4AD",
        [Accent] = "#FF4C8DFF",
        [AccentHover] = "#FF669DFF",
        [AccentPressed] = "#FF3B7DF2",
        [AccentFloating] = "#EE4C8DFF",
        [TextOnAccent] = "#FF0B1220",
        [Danger] = "#FFF0675C",
        [DangerSubtle] = "#14F0675C",
        // 深色 Danger 上的白字只有 3.09:1——深字才有 6.1。
        [TextOnDanger] = "#FF0B1220",
        [Selected] = "#294C8DFF",
        [AccentSubtle] = "#144C8DFF",
        [FocusOuter] = "#FFE6E8EB",
        [FocusInner] = "#FF0B1220",
        [Success] = "#FF57AB5A",
        [Caution] = "#FFC69026",
        [Brand] = "#FFFF4C8D",
    });

    // --- typography -----------------------------------------------------------

    /// <summary>Latin-first with an explicit CJK fallback, so fallback is deterministic, not linked per glyph.</summary>
    public const string FamilyUi = "Segoe UI, Microsoft YaHei UI";

    /// <summary>Mono-first for code, colours, hotkeys, paths — then CJK for the mixed cases.</summary>
    public const string FamilyMono = "Cascadia Mono, Consolas, Microsoft YaHei UI";

    /// <summary>
    /// The system symbol font, Windows 11's answer to what SF Symbols is on the
    /// Mac: glyphs that ship with the OS and follow its design language. The
    /// fallback keeps Windows 10 on the same codepoints (Segoe MDL2 Assets)
    /// without a version branch anywhere in the interface code.
    /// </summary>
    public const string FamilyIcon = "Segoe Fluent Icons, Segoe MDL2 Assets";

    // --- typography v2（票 19 / ADR-0012：控件回 14，内容字号交给用户）-----------
    // 旧 Font*（Hint/Caption/Secondary/Body/BodyLarge）与 Icon*（Small/Medium/Large）
    // 档已随全仓迁移退役：控件 14、内容 18、图标取 Fluent 设计尺寸 12/16/20/24/48。
    // 规则：行高落 4px 网格、多行中文≈1.7；SemiBold 只给拉丁词头与键帽；
    // "再大一号"只走 TypeContent 的设置项与系统文本缩放，不再动控件档。

    /// <summary>元信息、时间、计数、键帽、设置说明。</summary>
    public const double TypeCaption = 12;

    /// <summary>一切控件文字：标签、按钮、输入值、导航。</summary>
    public const double TypeBody = 14;

    /// <summary>设置分组标题、当前导航项——唯一允许加粗的 14。</summary>
    public const double TypeBodyStrong = 14;

    /// <summary>被阅读的剪贴板正文/译文，默认 18，可在设置里调 16/18/20。</summary>
    public const double TypeContent = 18;

    /// <summary>代码/路径/颜色子类型专用。</summary>
    public const double TypeContentMono = 15;

    /// <summary>设置页标题、对话框标题、空态标题。</summary>
    public const double TypeSubtitle = 20;

    /// <summary>关于页、引导首屏。</summary>
    public const double TypeTitle = 28;

    /// <summary>键帽专用（只出现拉丁字母/数字/箭头，中文最小字号规则豁免）。</summary>
    public const double TypeKeyCap = 12;

    /// <summary>词头（拉丁 SemiBold）专用档。</summary>
    public const double TypeHeadword = 20;

    public static double LineForCaption => 16;       // 单行紧行高
    public static double LineForCaptionMulti => 20;  // 多行中文
    public static double LineForBody => 20;
    public static double LineForBodyMulti => 24;
    public static double LineForContent => 31;
    public static double LineForContentMono => 26;

    /// <summary>
    /// 图标尺寸 v2：只取 Segoe Fluent 的设计尺寸（官方 16/20/24/48）。旧的
    /// 14/16/19 三档是跟着旧字号被推出来的、小尺寸发虚，已随迁移退役。
    /// </summary>
    public const double IconXs = 12;
    public const double IconS = 16;
    public const double IconM = 20;
    public const double IconL = 24;
    public const double IconXl = 48;

    /// <summary>Chinese body line height. A floor, not a suggestion — see DesignTokenTests.</summary>
    public const double BodyLineRatio = 1.7;

    public static double LineHeightFor(double fontSize)
        => Math.Round(fontSize * BodyLineRatio);

    // --- shape ----------------------------------------------------------------
    //
    //（Radius 挪到控件尺寸常量之后：Pill 由 ControlHeight 推导，而静态
    // 字段初始化按声明顺序执行。）

    // --- 控件尺寸（UI 报告 §4.2）------------------------------------------------
    /// <summary>按钮/输入/下拉/数值/开关行的标准高。</summary>
    public const double ControlHeight = 32;
    /// <summary>命令栏图标钮（紧凑档）。</summary>
    public const double ControlHeightCompact = 28;
    /// <summary>窄条头部（现状密度，保持）。</summary>
    public const double ControlHeightDense = 24;
    public const double TitleBarHeight = 32;
    public const double NavItemHeight = 36;
    /// <summary>两行列表行 / 单行列表行。</summary>
    public const double ListRowHeight = 56;
    public const double ListRowHeightSingle = 40;
    /// <summary>带缩略图的列表行（管理窗图片行，§6.4）。</summary>
    public const double ListRowHeightImage = 72;
    public const double SettingsCardMinHeight = 64;

    /// <summary>
    /// Corner radii, named by where they belong rather than by pixel value.
    /// Control 4 / Overlay 8 是 Windows 11 的 ControlCornerRadius /
    /// OverlayCornerRadius（ADR-0012）：窗口级交还 DWM 画 8，页内元素 4。
    /// Pill = 标准控件高（32）的一半——胶囊一律按"高度的一半"推导，
    /// 非标准高度的胶囊在使用点现算，不取常量（ADR-0012 §5）。
    /// Window 与 Card 是旧分层窗口时代的值，窗口迁移到 Overlay/Control 后
    /// 在本票末尾删除（迁移期并存，避免 DynamicResource 静默落空）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> Radius = new Dictionary<string, double>
    {
        ["Thumb"] = 3,
        ["Small"] = 4,
        ["Control"] = 4,
        ["Card"] = 8,
        ["Overlay"] = 8,
        ["Window"] = 12,
        ["Pill"] = ControlHeight / 2,
    };

    // --- 语义间距（4px 网格，UI 报告 §4.2；SpacingScale 迁移后退役）------------
    /// <summary>图标↔文字、键帽内边距、设置卡之间。</summary>
    public const double Space1 = 4;
    /// <summary>相关控件之间、按钮组间距、浮层外壳内边距（列表型）。</summary>
    public const double Space2 = 8;
    /// <summary>卡片纵向内边距、浮层外壳内边距（内容型）。</summary>
    public const double Space3 = 12;
    /// <summary>卡片横向内边距、对话框内边距。</summary>
    public const double Space4 = 16;
    public const double Space5 = 20;
    /// <summary>分组之间、页标题上方。</summary>
    public const double Space6 = 24;
    /// <summary>设置内容区左右边距。</summary>
    public const double Space8 = 32;
    /// <summary>空态上下留白。</summary>
    public const double Space12 = 48;

    /// <summary>
    /// Drop shadows: (blur radius, depth, opacity). Two strengths, no more —
    /// tuned to the Windows 11 flyout look: wide, soft, low.
    /// </summary>
    public static readonly (double Blur, double Depth, double Opacity) ShadowFloating = (26, 5, 0.2);
    public static readonly (double Blur, double Depth, double Opacity) ShadowBadge = (14, 3, 0.3);

    /// <summary>
    /// The spacing scale. Anything not on it is layout, not spacing — values
    /// tied to a control's width (a label's indent) stay where they are used.
    /// </summary>
    public static readonly double[] SpacingScale = [2, 4, 6, 8, 10, 12, 14, 16, 18, 24];

    // --- motion -----------------------------------------------------------------

    /// <summary>State changes. Fast enough to read as response, not as movement.</summary>
    public const double MotionFastMs = 160;

    /// <summary>Feature moments. Slower, never slow enough to wait for.</summary>
    public const double MotionSlowMs = 300;

    public static TimeSpan MotionFast => TimeSpan.FromMilliseconds(MotionFastMs);
    public static TimeSpan MotionSlow => TimeSpan.FromMilliseconds(MotionSlowMs);

    /// <summary>
    /// Content-layer entrance for transient surfaces: the window's own opacity
    /// stays at 1 (a layered window fading from transparent never composites —
    /// ticket 04's badge finding), so the entrance moves the content instead.
    /// </summary>
    public const double EntranceShift = 8;
}

/// <summary>
/// The two motion curves. A new effect that fits neither curve is a new
/// effect that should not exist.
/// </summary>
public enum MotionCurve
{
    /// <summary>Ordinary state changes: calm, quick, out of the way.</summary>
    Standard,

    /// <summary>The two or three "watch this" moments: a little more personality, same family.</summary>
    Feature,
}

/// <summary>
/// Decides how long a transition takes. Every animation in the app asks here,
/// which is what makes reduced motion all-or-nothing rather than a sieve: one
/// call site forgetting to consult this is one effect that ignores the user's
/// accessibility preference.
/// </summary>
public static class MotionPlan
{
    /// <param name="animationsAllowed">What the system says; supplied live by the platform layer.</param>
    /// <param name="feature">True for the two or three moments that earn the slower tier.</param>
    public static TimeSpan Duration(bool animationsAllowed, bool feature = false)
        => !animationsAllowed ? TimeSpan.Zero
         : feature ? DesignTokens.MotionSlow
         : DesignTokens.MotionFast;
}
