using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiyu.Core;

/// <summary>
/// 翻译走哪条路：免费引擎零配置、自备密钥是"更好的质量 + AI 动作"的升级路径，
/// 公共通道未上线（ADR-0009）。
/// 声明顺序即设置界面分段选择的下标顺序（Relay=0、OwnKey=1、Free=2），两者由
/// 测试钉在一起——改一边不改另一边会把"公共通道"接到自备密钥上。新增的
/// 一律追加在末尾：设置文件存的是枚举名，下标只在界面分段里用。
/// </summary>
public enum TranslationBackendKind
{
    /// <summary>拾语公共通道：零配置、零密钥、每日免费字数。</summary>
    Relay,

    /// <summary>用户自己的 OpenAI 兼容接口与凭据。</summary>
    OwnKey,

    /// <summary>
    /// 免费引擎（票 41、ADR-0013）：必应网页接口为主、腾讯交互翻译兜底，无账号无密钥。
    /// 它没有 prompt，不是通用模型——Agent 动作、批量翻译、LLM 词典仍只走自备密钥。
    /// </summary>
    Free,
}

/// <summary>
/// Everything the user can change.
///
/// Deliberately small. A settings screen with thirty knobs on it is the
/// opposite of the tool this is meant to be; each new entry here should have to
/// argue for itself.
/// </summary>
public sealed record AppSettings
{
    /// <summary>What translations are produced in.</summary>
    public string TargetLanguage { get; init; } = "Chinese";

    /// <summary>Null lets the backend work it out from the text.</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>
    /// 翻译走哪条路。类型默认值仍是自备密钥（ADR-0013 决策 4），尽管首次引导
    /// 预选的是免费引擎：文本发往哪里是一个关于数据去向的决定，只属于看过披露
    /// 的新用户（走完引导翻译屏才写入 Free）或用户自己的手（设置里改、未配置
    /// 引导卡上点「用免费引擎」）——绝不是一次静默升级。所以存量用户的文件
    /// 原样读回，选「跳过，用默认设置」的新用户也仍是自备密钥。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TranslationBackendKind>))]
    public TranslationBackendKind TranslationBackend { get; init; } = TranslationBackendKind.OwnKey;

    /// <summary>公共通道地址；默认指向官方部署，自建者可改指自己的 Worker。</summary>
    public string RelayEndpoint { get; init; } = RelayBackend.DefaultEndpoint;

    /// <summary>
    /// 匿名安装 ID：公共通道按它给每台设备计每日免费额度。首次启动生成，
    /// 之后终身稳定——它只是配额身份，不是凭据，伪造它绕过的也只是
    /// 每天 2 万字的份。
    /// </summary>
    public string RelayClientId { get; init; } = string.Empty;

    public string BackendBaseUrl { get; init; } = string.Empty;

    public string BackendModel { get; init; } = string.Empty;

    /// <summary>
    /// 选中的服务商预设（<see cref="ProviderPresets"/> 的 Id）；空表示自定义。
    /// 请求时按它解析出附加字段与温度规则——但地址或模型一旦手改得与预设
    /// 不符，解析就当作没有预设（见 <see cref="ProviderPresets.ResolveFor"/>），
    /// 存量文件里的旧 Id 因此不构成风险。
    /// </summary>
    public string BackendPresetId { get; init; } = string.Empty;

    /// <summary>
    /// 面板启动时用的提示词模板 id（票 42）。默认标准：与模板落地之前逐字相同。
    /// 指向不存在的模板（被删、文件被手改坏）时由 <see cref="Shiyu.Core.PromptTemplates"/>
    /// 在数据层回落到标准，界面不必处理。
    /// </summary>
    public string DefaultPromptTemplateId { get; init; } = PromptTemplate.StandardId;

    /// <summary>
    /// 面板模板按钮的循环列表（票 42），存模板 id，有序；反向输入框（票 43）的 Ctrl+E
    /// 共用同一份。未知 id 在读取时滤掉。
    /// </summary>
    public IReadOnlyList<string> TemplateCycle { get; init; } = PromptTemplate.DefaultCycle;

    /// <summary>
    /// 反向输入框（票 43）启动时用的提示词模板 id，默认口语：回英文帖、写给人看的话，口语比标准
    /// 自然。与面板的 <see cref="DefaultPromptTemplateId"/> 各管各的；运行中在输入框里换模板
    /// 只改运行时状态、不写这里。指向不存在的模板时由 <see cref="Shiyu.Core.PromptTemplates"/>
    /// 在数据层回落到标准。
    /// </summary>
    public string ReverseInputTemplateId { get; init; } = PromptTemplate.ColloquialId;

    /// <summary>
    /// 用户自建的提示词模板（票 42 阶段二），形状同 <see cref="ExclusionRules"/>。读取时
    /// 缺字段、占了保留 id、id 重复的条目一律当作不存在（见 <see cref="Shiyu.Core.PromptTemplates"/>）。
    /// </summary>
    public IReadOnlyList<StoredPromptTemplate> PromptTemplates { get; init; } = [];

    /// <summary>
    /// 「公共通道暂未开放」的一次性提示是否已经给过（票 08 迁移）。存量
    /// 用户选中公共通道时启动迁移要说一次话；每次启动都说就成了骚扰。
    /// </summary>
    public bool RelayUnavailableNoticed { get; init; }

    /// <summary>
    /// 自备密钥的各家服务商（用户需求 2026-10-10：每家各存一份地址、模型与凭据，选中即切换）。
    /// 凭据的唯一归宿：只经 <see cref="WithApiKey"/> 写入，只经 <see cref="KeyFor"/> 按来源取出
    /// （票 29：密钥只发给它所属的服务商）。凭据在文件里受保护，备份默认不带（ADR-0011）。
    /// </summary>
    public IReadOnlyList<SavedProvider> SavedProviders { get; init; } = [];

    /// <summary>
    /// 旧版的单把密钥（2026-10-10 之前）。只为读旧文件而留：读进来即并入
    /// <see cref="SavedProviders"/> 并清空，之后谁也不读它——<see cref="KeyFor"/> 不看这里，
    /// 绕过写入口直接填它的，得到的是"没配置"，不是泄露。
    /// </summary>
    public string BackendApiKey { get; init; } = string.Empty;

    /// <summary>旧版单把密钥的来源（票 29），与 <see cref="BackendApiKey"/> 一起只在读旧文件时用到。</summary>
    public string BackendApiKeyOrigin { get; init; } = string.Empty;

    /// <summary>How long image originals are kept before being cleaned up.</summary>
    public int ImageRetentionDays { get; init; } = 30;

    /// <summary>
    /// The master switch of delete protection — children name who is spared.
    /// Off means protection is entirely off, whichever child stays checked.
    /// </summary>
    public bool ProtectEntries { get; init; } = true;

    /// <summary>
    /// Favourites survive retention sweeps and bulk deletes. On by default:
    /// a starred entry is a promise the user made to themselves, and the
    /// delete entry points for it disappear rather than grey out.
    /// </summary>
    public bool ProtectFavorites { get; init; } = true;

    /// <summary>Pins survive retention sweeps and bulk deletes, like favourites.</summary>
    public bool ProtectPinned { get; init; } = true;

    /// <summary>
    /// When the bar hides, drop its realised cards and trim the process —
    /// a resident tray tool should cost pennies while idle. Recording never
    /// pauses: the listener and the pipeline do not live in the window.
    /// </summary>
    public bool LightweightWhenHidden { get; init; } = true;

    /// <summary>Whether copied images are recorded. Text always is — without it there is no tool.</summary>
    public bool RecordImages { get; init; } = true;

    /// <summary>
    /// Summon the bar beside the cursor, like the system's Win+V panel, rather
    /// than at a fixed remembered spot. On by default: near where you are
    /// typing is where you are about to paste. Off restores the resident
    /// window's remembered geometry.
    /// </summary>
    public bool BarAtCursor { get; init; } = true;

    /// <summary>
    /// Whether the resident bar stays pinned above every other window. On by
    /// default — that is the working posture of a tool consulted dozens of
    /// times a day. Turning it off lets other windows cover the bar; the bar's
    /// own floating layer (the preview) degrades with it so it never hovers
    /// alone above windows the bar is under. The header pin button and
    /// the settings page write this one value.
    /// </summary>
    public bool BarAlwaysOnTop { get; init; } = true;

    /// <summary>Whether copied file lists are recorded.</summary>
    public bool RecordFiles { get; init; } = true;

    /// <summary>
    /// Whether resting the pointer on a card opens its full preview by itself
    /// （悬停自动预览，用户需求 2026-10-05）. Off, the preview opens only while
    /// Space is held — the switch the user asked for in place of "type a zero
    /// into the delay". Off by default（用户需求 2026-10-09：预览弹窗默认关闭）:
    /// a panel that pops up wherever the pointer happens to rest gets in the way
    /// more than it teaches; Space and the preview teach row cover discovery.
    /// A settings file that already says true keeps saying it.
    /// </summary>
    public bool PreviewOnHover { get; init; }

    /// <summary>
    /// How long the pointer must rest on a card before the full preview
    /// appears, in milliseconds — read only while <see cref="PreviewOnHover"/>
    /// is on. The default is a deliberate beat: fast enough to feel like an
    /// answer, slow enough that a pass-through never summons it. A stored zero
    /// is the old way of saying "no hover previews"; loading turns it into the
    /// switch (see <see cref="TryParse"/>).
    /// </summary>
    public int PreviewHoverDelayMs { get; init; } = 500;

    /// <summary>
    /// Whether the preview panel ends with its teach row（悬停教学提示）: key
    /// caps for double-click and Enter, and where this card's drag lands
    /// (<see cref="PreviewTeaching"/>). The card tooltip that once carried it
    /// is retired. On by default — the row is the only place those gestures
    /// are taught; users who know them turn it off, and the preview itself is
    /// unaffected either way.
    /// </summary>
    public bool BarCardTooltips { get; init; } = true;

    /// <summary>
    /// 自动复制译文（用户需求 2026-10-05）：一次翻译结算后，译文自动上剪贴板并存入历史，
    /// 在窄条里就能找到——面板译完即存；反向输入框贴回后译文留在剪贴板上，不再还原成
    /// 原来的内容。只管翻译：提示词优化等改写类的结果照旧只是"运输"。默认关：自动改写
    /// 用户的剪贴板，得由用户自己点头。
    /// </summary>
    public bool AutoCopyTranslation { get; init; }

    /// <summary>
    /// 保存翻译记录（用户需求 2026-10-09）：翻译框与反向输入框的每一次翻译记一笔「原文 → 译文」，
    /// 在管理窗口的「翻译记录」页找回。默认开——用户要的就是"能找回"；记录只在本机、与剪贴板
    /// 历史同库，不想留的人关掉即可（已有的记录照旧按 <see cref="TranslationLogRetentionDays"/> 清）。
    /// </summary>
    public bool TranslationLogEnabled { get; init; } = true;

    /// <summary>
    /// 翻译记录留多少天，到期自动清空；0 是永不。设置页只给四档（<see cref="TranslationLogRetention"/>），
    /// 不在档上的值按"不早于它删"归到档上。
    /// </summary>
    public int TranslationLogRetentionDays { get; init; } = 30;

    /// <summary>Set once the first-run guide has run or been skipped; it never returns on its own.</summary>
    public bool OnboardingCompleted { get; init; }

    /// <summary>
    /// Take Win+V away from the system clipboard panel. Off by default: a
    /// system key is being borrowed, so only the user's explicit choice does
    /// it — and the hook lives in-process, so closing or killing Shiyu hands
    /// the key back to Windows by itself.
    /// </summary>
    public bool TakeOverWinV { get; init; }

    /// <summary>
    /// 拖选文字后光标旁浮"译"徽标，点击才翻译（划词徽章模式，票 37）。
    /// 默认关闭：它要常驻一个全局低级鼠标钩子，每一次鼠标事件都会多绕
    /// 一段本进程——这笔开销不为用户决定，只有用户自己点头才花。钩子
    /// 活在本进程里，关闭即刻摘钩，退出或被强杀时系统自动还原。
    /// </summary>
    public bool SelectionBadge { get; init; }

    /// <summary>
    /// Look for a newer release shortly after startup. On by default: for a
    /// resident tool, an update channel nobody checks is an update channel
    /// that does not exist. A found update only raises a tray notification —
    /// installing stays a click away, never automatic.
    /// </summary>
    public bool UpdateAutoCheck { get; init; } = true;

    /// <summary>
    /// A tray tool nobody starts is a tray tool nobody has; on by default,
    /// and the user can switch it off.
    /// </summary>
    public bool StartWithWindows { get; init; } = true;

    /// <summary>Colours follow Windows when this is System; changing it applies live.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>
    /// 内容字号（ADR-0012 排版 2，用户需求 2026-10-09）：被阅读的文字跟着它变，控件文字不变；
    /// 改了即时生效（<see cref="ContentRamp"/> 给出每一档的字号与行高）。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ContentFontSize>))]
    public ContentFontSize ContentFontSize { get; init; } = ContentFontSize.Standard;

    public string CaptureHotkey { get; init; } = "Ctrl+Shift+Z";

    public string ClipboardTranslateHotkey { get; init; } = "Ctrl+Shift+X";

    public string QuickBarHotkey { get; init; } = "Ctrl+Shift+V";

    /// <summary>Summons and hides the resident narrow bar.</summary>
    public string BarHotkey { get; init; } = "Ctrl+Shift+B";

    /// <summary>
    /// 打开历史管理窗（§5.1 快捷键页新增）。默认不设：与四个默认键不同，
    /// 管理窗有托盘与窄条入口，不设键不缺路；空串 = 不注册。
    /// </summary>
    public string LibraryHotkey { get; init; } = string.Empty;

    /// <summary>
    /// 反向输入框（票 43）：在当前输入框旁呼出小框，打中文出英文，Enter 贴回原处。默认 Alt+Q（Xtranslate
    /// 同款）。注意 Microsoft 365（Word、Excel、PowerPoint、Outlook）里 Alt+Q 是"跳到搜索框"：这是应用内
    /// 快捷键，RegisterHotKey 不会报冲突，注册后会悄悄把它盖掉——设置项的说明里写明，用户可以自己改键。
    /// </summary>
    public string ReverseInputHotkey { get; init; } = "Alt+Q";

    // --- narrow bar density ---
    // Density is how much content each card clamps, never how small the text
    // gets: sizes and paddings stay fixed so the list can never look cramped
    // or empty, only show more or less of each entry.

    /// <summary>How many lines of text a card shows before clamping.</summary>
    public int BarTextLines { get; init; } = 4;

    /// <summary>The tallest an image card may be, in device-independent units.</summary>
    public int BarImageHeight { get; init; } = 120;

    /// <summary>How many files a file card lists before clamping. Reserved until file entries exist.</summary>
    public int BarFileCount { get; init; } = 3;

    /// <summary>
    /// Which hover actions a card offers, in the user's chosen order, as ids
    /// from <see cref="HoverActions"/>. Sanitised on use, so a hand-edited
    /// file degrades to fewer buttons rather than to a broken tray.
    /// </summary>
    public IReadOnlyList<string> BarActions { get; init; } = HoverActions.All;

    /// <summary>
    /// Whether completed actions also play a sound. Off by default: the
    /// one-second tick on the button is the feedback; a sound on every copy
    /// is a toy piano.
    /// </summary>
    public bool ActionSound { get; init; }

    // --- narrow bar geometry, remembered between sessions ---
    // Null means "never placed yet"; the width is fixed by design and not stored.

    public double? BarLeft { get; init; }

    public double? BarTop { get; init; }

    public double? BarHeight { get; init; }

    /// <summary>
    /// Blank means the default beside the application data. Changing it needs a
    /// restart, and a synced folder needs a warning first — the history is not
    /// encrypted, so syncing it puts plaintext on someone else's servers.
    /// </summary>
    public string DataDirectoryOverride { get; init; } = string.Empty;

    /// <summary>Source-app and content-pattern rules the user added themselves.</summary>
    public IReadOnlyList<StoredExclusionRule> ExclusionRules { get; init; } = [];

    // 密钥经 KeyFor 取：来源与服务地址不一致时是空串，Backend.IsConfigured
    // 随之为 false——请求不带 Authorization，面板走"还没有配置"的引导卡（票 29）。
    [JsonIgnore]
    public TranslationBackendOptions Backend => new(BackendBaseUrl, BackendModel, KeyFor(BackendBaseUrl));

    /// <summary>
    /// 翻译此刻是否真的有一条能走的路。公共通道在上线条件满足前
    /// （<see cref="RelayChannel.Available"/> 为 false）不构成可用的路：
    /// 选中它的用户只有配好自备密钥才算配置完成——面板据此决定显示
    /// 译文还是配置引导卡。免费引擎无需任何配置，恒为 true（票 41）。
    /// </summary>
    [JsonIgnore]
    public bool IsTranslationConfigured
        => TranslationBackend == TranslationBackendKind.Free
            || (TranslationBackend == TranslationBackendKind.Relay && RelayChannel.Available
                ? RelayEndpoint.Trim().Length > 0
                : Backend.IsConfigured);

    /// <summary>
    /// The live backend for the chosen road, in the same spirit as
    /// <see cref="BuildExclusionPolicy"/>: settings stay data, and the
    /// translation port gets built here so every window shares one truth.
    /// 公共通道未上线（<see cref="RelayChannel.Available"/> 为 false）时
    /// 一律落到自备密钥后端——没配密钥的话后端自己会给出人话。
    /// 免费引擎（票 41）每次翻译都新建一个后端，状态（必应会话缓存、"必应不健康"
    /// 的冷却）放在进程级持有者里，不在实例上；它不是 <see cref="IStreamingModel"/>，
    /// 动作与批量翻译拿不到它。
    /// </summary>
    public ITranslationBackend BuildTranslationBackend()
    {
        if (TranslationBackend == TranslationBackendKind.Free)
        {
            return new FreeEngineBackend();
        }

        if (TranslationBackend == TranslationBackendKind.Relay && RelayChannel.Available)
        {
            return new RelayBackend(
                new RelayBackendOptions(RelayEndpoint, RelayClientId),
                httpClient: HttpClients.Shared);
        }

        var preset = ProviderPresets.ResolveFor(this);
        return new OpenAiCompatibleBackend(
            Backend,
            httpClient: HttpClients.Shared,
            extraBody: preset?.ExtraBody,
            maxTemperature: preset?.MaxTemperature,
            sendTemperature: preset?.SendTemperature ?? true);
    }

    /// <summary>
    /// 提示词模板（票 42）此刻是否生效：与 <see cref="BuildTranslationBackend"/> 走同
    /// 一套分支，实际建出 <see cref="OpenAiCompatibleBackend"/>（自备密钥的大模型）
    /// 时为真。公共通道未上线时选 Relay 也会落到自备密钥，同样为真；公共通道与
    /// 免费引擎的 prompt 不在客户端，为假。面板与设置都读这一个判断，日后新增的
    /// 后端种类不必回头改这里。
    /// </summary>
    [JsonIgnore]
    public bool PromptTemplatesApply => BuildTranslationBackend() is OpenAiCompatibleBackend;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Marks a protected <see cref="BackendApiKey"/> in settings.json. Anything
    /// without the marker is plaintext from an older Shiyu and loads as-is —
    /// the next save re-protects it.
    /// </summary>
    public const string SecretMarker = "dpapi:";

    /// <summary>
    /// The one protector instance, installed by the app before any settings
    /// IO. Ambient rather than passed around: Load/Save are static and called
    /// from places that have no business knowing about DPAPI. Tests install a
    /// fake; null means no protection at all (which is also the honest state
    /// for tools that read settings.json outside the app).
    /// </summary>
    public static ISecretProtector? SecretProtector { get; set; }

    /// <summary>
    /// Reads the settings, falling back to defaults for anything missing or
    /// unreadable. A corrupt settings file must not stop Shiyu from starting:
    /// with no window to show an error in, that would look like a tool that
    /// simply died.
    /// </summary>
    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) && TryParse(File.ReadAllText(path), out var loaded)
                ? loaded
                : new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Parses settings JSON, refusing anything that would not come back as
    /// usable settings: empty, malformed, or not an object all say false.
    /// A restore refuses to touch the settings file until the backup's copy
    /// passes here — writing it first and discovering garbage on load would
    /// silently reset the user to defaults (O-02).
    /// </summary>
    public static bool TryParse(string json, [NotNullWhen(true)] out AppSettings? settings)
    {
        settings = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            if (JsonSerializer.Deserialize<AppSettings>(json, Format) is not { } parsed)
            {
                return false;
            }

            // A saved action list that exactly matches a former default is a
            // default that predates newer actions, not a choice — upgrade it,
            // while respecting anything the user actually reordered or pruned.
            if (parsed.BarActions.SequenceEqual(FormerDefaultActions)
                || parsed.BarActions.SequenceEqual(HoverActions.FormerDefaultWithoutGroup))
            {
                parsed = parsed with { BarActions = HoverActions.All };
            }

            // Before the hover switch existed, a delay of zero was how hover
            // previews were turned off. Keep that choice, as the switch it now
            // is, and give the delay back its default for the day the switch
            // goes on again.
            if (parsed.PreviewHoverDelayMs <= 0)
            {
                parsed = parsed with
                {
                    PreviewOnHover = false,
                    PreviewHoverDelayMs = new AppSettings().PreviewHoverDelayMs,
                };
            }

            // A protected key comes back exactly as it was stored — the marker
            // plus opaque bytes. Turn it into the plain key the runtime uses;
            // a blob this machine cannot open (moved from another install,
            // switched user, broken protector) is an empty key, which asks to
            // be re-entered, never a mystery string sent as a credential.
            if (parsed.BackendApiKey.StartsWith(SecretMarker, StringComparison.Ordinal))
            {
                var plain = SecretProtector?.Unprotect(parsed.BackendApiKey);
                parsed = parsed with { BackendApiKey = plain ?? string.Empty };
            }

            // 票 29：旧文件有密钥、没有来源——以此刻的服务地址补上，维持现有的
            // 配对，下次保存时落盘。只补空缺：已记下的来源是用户当时的事实，
            // 不能被"现在的地址"改写（那会把旧密钥悄悄洗给新地址）。
            if (parsed.BackendApiKey.Length > 0 && string.IsNullOrEmpty(parsed.BackendApiKeyOrigin))
            {
                parsed = parsed with { BackendApiKeyOrigin = KeyOrigin.Of(parsed.BackendBaseUrl) };
            }

            // 各家各存的凭据同样是标记 + 不透明字节：逐份还原成明文。本机打不开的那份（换了机器、
            // 换了用户）与上面同理当作没有——整份不留，等用户重新填，绝不把一串乱码当凭据发出去。
            parsed = parsed with
            {
                SavedProviders = [.. (parsed.SavedProviders ?? []).Select(Readable).OfType<SavedProvider>()],
            };

            // 用户需求 2026-10-10：一把密钥 → 每家一份。旧文件里的那一对（密钥 + 来源）并入
            // SavedProviders，旧字段随即清空，之后再没有人读它们。
            if (parsed.BackendApiKey.Length > 0 || parsed.BackendApiKeyOrigin.Length > 0)
            {
                parsed = parsed.MigratingLegacyKey();
            }

            settings = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The default action list before favourites and notes existed.</summary>
    private static readonly string[] FormerDefaultActions =
        ["copy", "paste", "plain", "open", "locate", "pin", "delete"];

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside the target and moved into place, so an interrupted
        // save leaves the previous settings rather than half a file.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(ToStorableNode(), Format));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// This instance as it appears in settings.json: every field plain except
    /// the saved providers' keys, each protected when a protector is installed.
    /// Keys in memory are always plain — protection is a property of the file,
    /// not of the running app. Protection failing refuses the save rather than
    /// quietly writing a plaintext key the user believed was protected. The
    /// legacy single-key fields are read-only history and never written again.
    /// </summary>
    private System.Text.Json.Nodes.JsonObject ToStorableNode()
    {
        var node = System.Text.Json.Nodes.JsonObject.Create(
            JsonSerializer.SerializeToElement(this, Format))!;
        node.Remove(nameof(BackendApiKey));
        node.Remove(nameof(BackendApiKeyOrigin));

        if (SecretProtector is not { } protector
            || node[nameof(SavedProviders)] is not System.Text.Json.Nodes.JsonArray providers)
        {
            return node;
        }

        foreach (var provider in providers.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            if (provider[nameof(SavedProvider.ApiKey)]?.GetValue<string>() is { Length: > 0 } key)
            {
                provider[nameof(SavedProvider.ApiKey)] = protector.Protect(key)
                    ?? throw new InvalidOperationException("API 密钥保护失败，已放弃写入设置文件。");
            }
        }

        return node;
    }

    /// <summary>
    /// The settings copy a backup carries (ADR-0011). Without the keys: no
    /// saved provider at all — a backup leaving this machine must not carry a
    /// working credential, and a provider without its key is nothing to
    /// restore. With them: the plain keys, because the protected form is bound
    /// to this machine's user; the caller only ever embeds them inside an
    /// encrypted archive, and the import re-protects on first save.
    /// </summary>
    public string ToBackupJson(bool includeKey)
        => includeKey
            ? JsonSerializer.Serialize(this, Format)
            : JsonSerializer.Serialize(this with { SavedProviders = [], BackendApiKey = string.Empty }, Format);

    // --- 每家各存一份，凭据只发给它所属的服务商（票 29，用户需求 2026-10-10） ---------------

    /// <summary>
    /// 发给 <paramref name="baseUrl"/> 的密钥：已存的各家里来源与它一致的那一把，否则是空串。
    /// 失败即关闭：认不出来源的地址拿不到任何一把。
    /// </summary>
    public string KeyFor(string baseUrl) => ProviderFor(baseUrl)?.ApiKey ?? string.Empty;

    /// <summary>来源与 <paramref name="baseUrl"/> 一致的那家已存服务商；没有就是 null。</summary>
    public SavedProvider? ProviderFor(string baseUrl)
        => SavedProviders.FirstOrDefault(provider =>
            provider.ApiKey.Length > 0 && KeyOrigin.Same(baseUrl, provider.Origin));

    /// <summary>
    /// 存了别家的凭据，却没有 <paramref name="baseUrl"/> 这一家的——界面据此说"这家还没有
    /// 凭据"，而不是"还没填过任何凭据"。没有地址就谈不上"这家"。
    /// </summary>
    public bool HasKeyForOtherOrigin(string baseUrl)
        => SavedProviders.Count > 0
            && KeyOrigin.Of(baseUrl).Length > 0
            && ProviderFor(baseUrl) is null;

    /// <summary>
    /// 保存一把密钥——唯一的写入口：记在此刻服务地址的来源名下，连同地址、模型与预设；同一家
    /// 再存就是更换（位置不变），别家的一份也不碰。设置窗的「保存凭据」、引导里填的密钥都走这里。
    /// 空密钥是删掉这一家。没有服务地址就无处可记，什么也不变。
    /// </summary>
    public AppSettings WithApiKey(string key)
    {
        var origin = KeyOrigin.Of(BackendBaseUrl);
        if (origin.Length == 0)
        {
            return this;
        }

        if (key.Length == 0)
        {
            return WithoutProvider(origin);
        }

        var entry = new SavedProvider(origin, BackendBaseUrl.Trim(), BackendModel.Trim(), BackendPresetId, key);
        var providers = SavedProviders.ToList();
        var index = providers.FindIndex(provider => KeyOrigin.Same(provider.Origin, origin));
        if (index >= 0)
        {
            providers[index] = entry;
        }
        else
        {
            providers.Add(entry);
        }

        return this with { SavedProviders = providers };
    }

    /// <summary>删掉一家已存的服务商（连同它的凭据）。</summary>
    public AppSettings WithoutProvider(string origin)
        => this with
        {
            SavedProviders = [.. SavedProviders.Where(provider => !KeyOrigin.Same(provider.Origin, origin))],
        };

    /// <summary>切到一家已存的服务商：地址、模型、预设回到它上次的样子，凭据随来源自然找到。</summary>
    public AppSettings SwitchedTo(SavedProvider provider)
        => this with
        {
            BackendBaseUrl = provider.BaseUrl,
            BackendModel = provider.Model,
            BackendPresetId = provider.PresetId,
        };

    /// <summary>
    /// 在下拉里挑了一家预设：存过这家，就回到上次用的地址与模型；没存过就用预设的默认。
    /// </summary>
    public AppSettings SwitchedToPreset(ProviderPreset preset)
        => ProviderFor(preset.BaseUrl) is { } saved
            ? this with
            {
                BackendPresetId = preset.Id,
                BackendBaseUrl = saved.BaseUrl.Length > 0 ? saved.BaseUrl : preset.BaseUrl,
                BackendModel = saved.Model.Length > 0 ? saved.Model : preset.DefaultModel,
            }
            : this with
            {
                BackendPresetId = preset.Id,
                BackendBaseUrl = preset.BaseUrl,
                BackendModel = preset.DefaultModel,
            };

    /// <summary>
    /// 当前这家的地址、模型或预设改了：它在已存的里面，就跟着记下（凭据不动），下次切回来是
    /// 上次的样子。不在已存里的地址什么也不记——没有凭据就谈不上"这家的一份"。
    /// </summary>
    public AppSettings RememberingActiveProvider()
    {
        var providers = SavedProviders.ToList();
        var index = providers.FindIndex(provider => KeyOrigin.Same(BackendBaseUrl, provider.Origin));
        if (index < 0)
        {
            return this;
        }

        var remembered = providers[index] with
        {
            BaseUrl = BackendBaseUrl.Trim(),
            Model = BackendModel.Trim(),
            PresetId = BackendPresetId,
        };
        if (remembered == providers[index])
        {
            return this;
        }

        providers[index] = remembered;
        return this with { SavedProviders = providers };
    }

    /// <summary>
    /// 备份恢复落地时的凭据取舍（票 11 的语义不变）：备份没带凭据（默认，ADR-0011）就保留
    /// <paramref name="local"/>——本机现有的——各家服务商；备份自己带了就照单全收。
    /// </summary>
    public AppSettings KeepingKeyOf(AppSettings local)
        => SavedProviders.Count > 0 ? this : this with { SavedProviders = local.SavedProviders };

    /// <summary>
    /// 读进来的一份：受保护的凭据还原成明文，来源规整；凭据打不开、或认不出来源的，整份不留。
    /// 缺字段的旧写法（null）一律当空串。
    /// </summary>
    private static SavedProvider? Readable(SavedProvider? provider)
    {
        if (provider is null)
        {
            return null;
        }

        var key = provider.ApiKey ?? string.Empty;
        if (key.StartsWith(SecretMarker, StringComparison.Ordinal))
        {
            key = SecretProtector?.Unprotect(key) ?? string.Empty;
        }

        var origin = KeyOrigin.Of(provider.Origin is { Length: > 0 } recorded ? recorded : provider.BaseUrl);
        return key.Length > 0 && origin.Length > 0
            ? new SavedProvider(origin, provider.BaseUrl ?? string.Empty, provider.Model ?? string.Empty,
                provider.PresetId ?? string.Empty, key)
            : null;
    }

    /// <summary>
    /// 旧版的单把密钥并入各家各存的一份（用户需求 2026-10-10）。来源与此刻的服务地址一致，就连
    /// 地址、模型、预设一起记下；不一致（换过服务商、还没填新密钥）就按来源认出预设，认不出用
    /// 来源本身当地址。没有来源的旧密钥从来发不出去（票 29），也不会因迁移而复活。已经存了这一家
    /// 的，以已存的为准。
    /// </summary>
    private AppSettings MigratingLegacyKey()
    {
        var origin = KeyOrigin.Of(BackendApiKeyOrigin);
        var key = BackendApiKey;
        var migrated = this with { BackendApiKey = string.Empty, BackendApiKeyOrigin = string.Empty };
        if (key.Length == 0 || origin.Length == 0
            || SavedProviders.Any(provider => KeyOrigin.Same(provider.Origin, origin)))
        {
            return migrated;
        }

        SavedProvider entry;
        if (KeyOrigin.Same(BackendBaseUrl, origin))
        {
            entry = new SavedProvider(origin, BackendBaseUrl.Trim(), BackendModel.Trim(), BackendPresetId, key);
        }
        else
        {
            var preset = ProviderPresets.All.FirstOrDefault(candidate => KeyOrigin.Same(candidate.BaseUrl, origin));
            entry = new SavedProvider(
                origin, preset?.BaseUrl ?? origin, preset?.DefaultModel ?? string.Empty, preset?.Id ?? string.Empty, key);
        }

        return migrated with { SavedProviders = [.. SavedProviders, entry] };
    }

    public ExclusionPolicy BuildExclusionPolicy()
        => new(ExclusionPolicy.Presets.Concat(
            ExclusionRules.Select(rule => new ExclusionRule(rule.Kind, rule.Value))));
}

/// <param name="Kind">Whether the rule matches the source application or the text.</param>
public sealed record StoredExclusionRule(ExclusionRuleKind Kind, string Value);
