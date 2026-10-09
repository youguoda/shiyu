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
    /// Stored as written. The history beside it is not encrypted either, so
    /// pretending this one field is protected would be theatre — what it does
    /// get is never being shown in the interface or written to a log.
    /// </summary>
    public string BackendApiKey { get; init; } = string.Empty;

    /// <summary>
    /// 上面这把密钥是为哪家服务商保存的：保存时服务地址的 scheme + host +
    /// port（票 29，见 <see cref="KeyOrigin"/>）。不是秘密，明文存。它与密钥
    /// 是一对——只经 <see cref="WithApiKey"/> 写入，只经 <see cref="KeyFor"/>
    /// 取出：来源与服务地址不一致时密钥不会被带上。
    /// </summary>
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
    /// the API key, which is protected when a protector is installed. The key
    /// in memory is always plain — protection is a property of the file, not
    /// of the running app. Protection failing refuses the save rather than
    /// quietly writing the plaintext key the user believed was protected.
    /// </summary>
    private System.Text.Json.Nodes.JsonObject ToStorableNode()
    {
        var node = System.Text.Json.Nodes.JsonObject.Create(
            JsonSerializer.SerializeToElement(this, Format))!;
        if (BackendApiKey.Length == 0)
        {
            return node;
        }

        if (SecretProtector is { } protector
            && protector.Protect(BackendApiKey) is { } stored)
        {
            node["BackendApiKey"] = stored;
        }
        else if (SecretProtector is not null)
        {
            throw new InvalidOperationException("API 密钥保护失败，已放弃写入设置文件。");
        }

        return node;
    }

    /// <summary>
    /// The settings copy a backup carries (ADR-0011). Without the key: the
    /// key field is empty — a backup leaving this machine must not carry a
    /// working credential. With it: the plain key, because the protected form
    /// is bound to this machine's user; the caller only ever embeds it inside
    /// an encrypted archive, and the import re-protects on first save.
    /// The key's origin (票 29) is not a secret and travels either way; a
    /// restore that finds no key keeps this machine's key and origin
    /// (<see cref="KeepingKeyOf"/>), so the origin in a keyless backup is moot.
    /// </summary>
    public string ToBackupJson(bool includeKey)
        => includeKey
            ? JsonSerializer.Serialize(this, Format)
            : JsonSerializer.Serialize(this with { BackendApiKey = string.Empty }, Format);

    // --- 密钥只发给它所属的服务商（票 29） -----------------------------------------

    /// <summary>
    /// 发给 <paramref name="baseUrl"/> 的密钥：来源与它一致才返回已存的那把，
    /// 否则是空串。没有记下来源的密钥也是空串——失败即关闭：谁绕开
    /// <see cref="WithApiKey"/> 只写了密钥，得到的是"没配置"，不是泄露。
    /// </summary>
    public string KeyFor(string baseUrl)
        => BackendApiKey is { Length: > 0 }
            && BackendApiKeyOrigin is { Length: > 0 } stored
            && KeyOrigin.Of(baseUrl) is { Length: > 0 } wanted
            && string.Equals(KeyOrigin.Of(stored), wanted, StringComparison.OrdinalIgnoreCase)
                ? BackendApiKey
                : string.Empty;

    /// <summary>
    /// 已存了密钥，但它不属于 <paramref name="baseUrl"/> 这个来源——界面据此
    /// 说"请填写这家的密钥"，而不是"还没填"。没有地址就谈不上"这家"。
    /// </summary>
    public bool HasKeyForOtherOrigin(string baseUrl)
        => BackendApiKey is { Length: > 0 }
            && KeyOrigin.Of(baseUrl).Length > 0
            && KeyFor(baseUrl).Length == 0;

    /// <summary>
    /// 保存一把密钥——唯一的写入口：密钥与它的来源（此刻的服务地址）成对
    /// 写下。设置窗的「保存凭据」、引导里填的密钥都走这里。空密钥连来源一并
    /// 清掉。
    /// </summary>
    public AppSettings WithApiKey(string key)
        => key.Length == 0
            ? this with { BackendApiKey = string.Empty, BackendApiKeyOrigin = string.Empty }
            : this with { BackendApiKey = key, BackendApiKeyOrigin = KeyOrigin.Of(BackendBaseUrl) };

    /// <summary>
    /// 备份恢复落地时的密钥取舍（票 11 的语义不变，加上来源）：备份没带密钥
    /// （默认，ADR-0011）就保留<paramref name="local"/>——本机现有的——密钥，
    /// 连同它的来源：两者是一对，拆开就是把 A 家的密钥配上备份里 B 家的地址。
    /// 备份自己带了密钥就照单全收，它的来源随设置一起来。
    /// </summary>
    public AppSettings KeepingKeyOf(AppSettings local)
        => BackendApiKey.Length > 0
            ? this
            : this with
            {
                BackendApiKey = local.BackendApiKey,
                BackendApiKeyOrigin = local.BackendApiKeyOrigin,
            };

    public ExclusionPolicy BuildExclusionPolicy()
        => new(ExclusionPolicy.Presets.Concat(
            ExclusionRules.Select(rule => new ExclusionRule(rule.Kind, rule.Value))));
}

/// <param name="Kind">Whether the rule matches the source application or the text.</param>
public sealed record StoredExclusionRule(ExclusionRuleKind Kind, string Value);
