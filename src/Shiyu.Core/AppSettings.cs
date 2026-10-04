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
    /// own floating layers (preview, connector) degrade with it so they never
    /// hover alone above windows the bar is under. The header pin button and
    /// the settings page write this one value.
    /// </summary>
    public bool BarAlwaysOnTop { get; init; } = true;

    /// <summary>Whether copied file lists are recorded.</summary>
    public bool RecordFiles { get; init; } = true;

    /// <summary>
    /// How long the pointer must rest on a card before the full preview
    /// appears, in milliseconds. Zero disables hover previews — hold Space
    /// still works. The default is a deliberate beat: fast enough to feel
    /// like an answer, slow enough that a pass-through never summons it.
    /// </summary>
    public int PreviewHoverDelayMs { get; init; } = 500;

    /// <summary>
    /// Whether hovering a card shows its tooltip (full text + drag teaching)
    /// during the beat before the preview opens. On by default — the drag
    /// hint is the only place that interaction is taught; users who find the
    /// tip noisy turn it off, and the hover preview is unaffected either way.
    /// </summary>
    public bool BarCardTooltips { get; init; } = true;

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

    [JsonIgnore]
    public TranslationBackendOptions Backend => new(BackendBaseUrl, BackendModel, BackendApiKey);

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
    /// </summary>
    public string ToBackupJson(bool includeKey)
        => includeKey
            ? JsonSerializer.Serialize(this, Format)
            : JsonSerializer.Serialize(this with { BackendApiKey = string.Empty }, Format);

    public ExclusionPolicy BuildExclusionPolicy()
        => new(ExclusionPolicy.Presets.Concat(
            ExclusionRules.Select(rule => new ExclusionRule(rule.Kind, rule.Value))));
}

/// <param name="Kind">Whether the rule matches the source application or the text.</param>
public sealed record StoredExclusionRule(ExclusionRuleKind Kind, string Value);
