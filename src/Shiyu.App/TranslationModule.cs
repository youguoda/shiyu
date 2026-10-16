using System.IO;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>
/// 翻译面板模块（O-40 拆自 App.xaml.cs）：面板单例的建造与翻译、词典与
/// 后端工厂（票 08 的"一切模型路径从同一个门进"）、译文的存档。
/// </summary>
internal sealed class TranslationModule
{
    private AppShell? _shell;
    private PanelWindow? _panel;

    /// <summary>朗读服务与面板同寿命：一条专用 STA 线程，懒得起、起一次用到底。</summary>
    private SpeechSynthesis? _speech;

    public void Attach(AppShell shell)
    {
        _shell = shell;

        // The panel's languages follow without a restart (原单体应用函数的
        // 一段，O-20)：面板是复用实例，下一次翻译就用新语言。
        shell.SettingsChanged += s => _panel?.ApplySettings(s);

        // 面板在场时写设置，热键注册表随之整体重建，面板的 Esc 要在新注册表上重挂
        // （票 41）。听的是"重建完成"而不是 SettingsChanged：本模块比热键模块先订阅，
        // 直接听 SettingsChanged 会在重建之前就重挂——挂上的是即将退役的旧注册表。
        shell.HotkeysRebuilt += () => _panel?.ReclaimEscape();
    }

    /// <summary>
    /// Opens the panel on the given text. One panel, reused: a second copy
    /// would be two translations of two different things competing for the
    /// same corner of the screen.
    ///
    /// <paramref name="onDisplayed"/> 在面板上屏的那一刻触发；面板出不来时
    /// 立刻触发——挂着不执行的还原就是白借。
    /// </summary>
    public async void ShowPanel(string text, Action? onDisplayed = null)
    {
        var shell = _shell!;

        if (shell.Hotkeys is null || shell.Writer is null)
        {
            onDisplayed?.Invoke();
            return;
        }

        // async void 里逃出去的异常是进程级崩溃（O-05）；同时面板出不来时
        // 挂着的剪贴板还原就是白借——onDisplayed 用一次性闸门包住，异常
        // 路径也要保证债被还上（且只还一次）。
        var displayed = false;
        void Displayed()
        {
            if (!displayed)
            {
                displayed = true;
                onDisplayed?.Invoke();
            }
        }

        try
        {
            _speech ??= new SpeechSynthesis();

            _panel ??= new PanelWindow(
                // O-43：面板持注册表取用口而非一次性捕获——注册表随设置
                // 保存整体重建，退役实例现在会大声拒绝而不是悄悄失灵。
                shell.Hotkeys, shell.Writer, BuildPanelBackend, shell.Settings,
                SaveTranslationToHistory,
                dictionary: BuildDictionary,
                speech: _speech,

                // 未配置时三条路（复制徽标、划词热键、翻译剪贴板）都落进
                // 面板的引导卡，而不是异常文本（票 08）。
                backendReady: () => shell.Settings.IsTranslationConfigured,
                openSettings: () => shell.OpenSettingsAt?.Invoke("service.preset"),

                // 引导卡上的「用免费引擎」（票 41）：点击就是同意，在最新设置上增量写一项；
                // 写失败时 TryUpdateSettings 已向托盘说了人话，翻译方式没变，返回 false。
                enableFreeEngine: () => shell.TryUpdateSettings(
                    latest => latest with { TranslationBackend = TranslationBackendKind.Free }),

                // 「自动复制译文」（用户需求 2026-10-05）：译完即存，不出声。
                keepTranslation: KeepTranslation,

                // 翻译记录（用户需求 2026-10-09）：开关与排除名单在 shell 那一头把关。
                logTranslation: (original, translated, template)
                    => shell.LogTranslation(original, translated, TranslationOrigin.Panel, template),

                // 生词本（评审 2026-10-16 F-09）：词典卡上的星标收进翻译记录的那一列。
                vocabulary: new PanelVocabulary(
                    (original, translated, template, card) => shell.TranslationLog.StarWord(original, translated, template, card),
                    headword => shell.TranslationLog.UnstarWord(headword),
                    headword => shell.TranslationLog.IsStarred(headword)));
            await _panel.TranslateAsync(text, Displayed);
        }
        catch (Exception failure)
        {
            // 翻译失败本身已被面板收敛成状态；走到这里的是面板之外的意外。
            Displayed();
            Log.Event(LogEvent.TranslationFailed, failure, ("panel", 1));
            shell.TellUser("翻译面板没能打开，已记录到日志。");
        }
    }

    /// <summary>
    /// 单词词典的路（票 35 + 2026-09-27 回退修正 + 评审 2026-10-16 F-08）：英文单词**先查本地词典**——
    /// 随程序发布的 ECDICT 常用词子集，中文释义、音标、考试标签，零延迟、断网可用；本地卡的例句由免费词典
    /// 后补（3s 预算，补不上就没有）。本地没有的词照旧：免费词典（600ms 预算），不可达或没查到时回退
    /// LLM 词典化（8s 预算）——实测 dictionaryapi.dev 在本机网络完全不可达。中文单词直接 LLM 词典化；
    /// 其余文本不吃词典卡。回退只认自备密钥；没有自己的后端时慢路缺席。
    /// </summary>
    private IDictionaryApi? BuildDictionary(string word)
    {
#if DEBUG
        // 探针：假后端配假词典（SHIYU_FAKE_BACKEND=1），单词态的探针同样不碰网络。
        if (DebugOverrides.FakeBackend)
        {
            // SHIYU_LOCAL_DICTIONARY=1：离线词典照真的查，查不到的交给假词典（评审 2026-10-16 F-08 的探针）。
            return DebugOverrides.LocalDictionary
                ? new LocalFirstDictionary(LocalWords, new DebugFakeDictionary(), null)
                : new DebugFakeDictionary();
        }
#endif

        return DictionaryWord.IsEnglishWord(word)
            ? new LocalFirstDictionary(
                LocalWords,
                new FallbackDictionary(
                    new BudgetedDictionary(new FreeDictionaryApi(HttpClients.Shared)),
                    OwnKeyDictionary()),
                () => new BudgetedDictionary(new FreeDictionaryApi(HttpClients.Shared), TimeSpan.FromSeconds(3)))
            : DictionaryWord.IsChineseWord(word)
                ? OwnKeyDictionary()
                : null;
    }

    /// <summary>随程序发布的离线词典（评审 2026-10-16 F-08）：安装目录的 Assets\dictionary.db，只读。</summary>
    internal static LocalDictionary LocalWords { get; } =
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "dictionary.db"));

    /// <summary>
    /// 自备密钥后端的唯一组装点（票 08）：预设解析出的附加字段与温度规则
    /// 在这里生效——面板的词典、动作、批量翻译都从这一个门进，预设对
    /// 所有模型路径一视同仁。
    /// </summary>
    private OpenAiCompatibleBackend BuildOwnKeyBackend()
    {
        var settings = _shell!.Settings;
        var preset = ProviderPresets.ResolveFor(settings);
        return new OpenAiCompatibleBackend(
            settings.Backend,
            httpClient: HttpClients.Shared,
            extraBody: preset?.ExtraBody,
            maxTemperature: preset?.MaxTemperature,
            sendTemperature: preset?.SendTemperature ?? true);
    }

    /// <summary>
    /// 动作与批量翻译的模型工厂，与面板共用同一条路（票 08）：公共通道
    /// 未上线时它就是自备密钥后端；哪天通道上线而中转还不支持流式对话，
    /// 这里回落自备密钥，而不是把窗口架在一条没有的路上。
    /// </summary>
    public IStreamingModel BuildStreamingModel()
        => _shell!.Settings.BuildTranslationBackend() as IStreamingModel
            ?? BuildOwnKeyBackend();

    /// <summary>
    /// 自备密钥的 LLM 词典路：有能用的自备后端才有路——"能用"问
    /// Backend.IsConfigured，它已经算上了密钥的来源（票 29：密钥属于别家时
    /// 这条路缺席，而不是带着空密钥白跑一趟）。8s 预算罩住流式取卡——比免费
    /// 路宽一个数量级，因为它是兜底，慢到也仍然胜过没有卡。
    /// </summary>
    private IDictionaryApi? OwnKeyDictionary()
        => !_shell!.Settings.Backend.IsConfigured
            ? null
            : new BudgetedDictionary(
                new LlmDictionaryApi(BuildOwnKeyBackend()),
                TimeSpan.FromSeconds(8));

    /// <summary>
    /// The panel's 「存入历史」: files the translation and says so once.
    /// </summary>
    public void SaveTranslationToHistory(string original, string translated)
    {
        if (_shell!.Pipeline is null)
        {
            return;
        }

        KeepTranslation(original, translated);
        _shell.TellUser("译文已存入历史。");
    }

    private ITranslationBackend BuildPanelBackend()
    {
#if DEBUG
        // 探针：按真实模型节奏出一篇长 Markdown（SHIYU_FAKE_BACKEND=stream，用户实录 2026-10-15）。
        if (DebugOverrides.FakeStream)
        {
            return new DebugStreamingBackend();
        }

        // 探针：确定性的假后端（"[EN] " + 原文），与反向输入框同一个开关 SHIYU_FAKE_BACKEND=1。
        if (DebugOverrides.FakeBackend)
        {
            return new DebugFakeBackend();
        }
#endif

        return _shell!.Settings.BuildTranslationBackend();
    }

#if DEBUG
    /// <summary>探针命令 panel-templates 的入口（见 PanelWindow.ProbeTemplatePicker）。</summary>
    internal async void ProbeTemplatePicker(string text)
    {
        ShowPanel(text);
        for (var waited = 0; waited < 50 && _panel is not { IsVisible: true }; waited++)
        {
            await Task.Delay(100);
        }

        var log = _panel is null ? "no-panel" + Environment.NewLine : await _panel.ProbeTemplatePicker();

        // 单词态也能换模板（用户需求 2026-10-13）：同一扇面板换成一个词再走一遍。
        if (_panel is not null)
        {
            ShowPanel("setup");
            log += await _panel.ProbeWordTemplates();
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(DebugOverrides.ProbeDirectory!, "panel-templates.log"), log + "done" + Environment.NewLine);
    }

    /// <summary>探针命令 translation-log 的面板把手一段（见 PanelWindow.ProbeGrips）。</summary>
    internal string ProbePanelGrips() => _panel?.ProbeGrips() ?? "panelGrip|missing" + Environment.NewLine;

    /// <summary>探针命令 stream-jank 的入口（见 PanelWindow.ProbeStreamJank）。配 SHIYU_FAKE_BACKEND=stream。</summary>
    internal async void ProbeStreamJank(string text)
    {
        ShowPanel(text);
        for (var waited = 0; waited < 50 && _panel is not { IsVisible: true }; waited++)
        {
            await Task.Delay(100);
        }

        var log = _panel is null ? "no-panel" + Environment.NewLine : await _panel.ProbeStreamJank();
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(DebugOverrides.ProbeDirectory!, "stream-jank.log"), log + "done" + Environment.NewLine);
    }

    /// <summary>
    /// 探针命令 panel-dictionary 的入口（评审 2026-10-16 F-08，见 PanelWindow.ProbeDictionaryCard）：先量离线查词
    /// 本身（第一次要开文件，第二次是热的），再让面板开在 example 与 went 上看卡。配 SHIYU_FAKE_BACKEND=1 与
    /// SHIYU_LOCAL_DICTIONARY=1。
    /// </summary>
    internal async void ProbeDictionaryCard()
    {
        var log = new System.Text.StringBuilder();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cold = LocalWords.Lookup("example") is not null;
        var coldMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var warm = LocalWords.Lookup("ubiquitous") is not null;
        var warmMs = watch.Elapsed.TotalMilliseconds;
        log.AppendLine(FormattableString.Invariant(
            $"lookup|cold={cold}|coldMs={coldMs:F1}|warm={warm}|warmMs={warmMs:F1}"));

        foreach (var (word, phonetic, meaning, meta) in new (string, string?, string, string)[]
                 {
                     ("example", "/igˈzæmpl/", "例子", "高考"),
                     ("went", null, "去", "go 的变形"),
                 })
        {
            ShowPanel(word);
            for (var waited = 0; waited < 50 && _panel is not { IsVisible: true }; waited++)
            {
                await Task.Delay(100);
            }

            log.Append(_panel is null ? $"{word}|missing{Environment.NewLine}" : await _panel.ProbeDictionaryCard(word, phonetic, meaning, meta, star: word == "example"));
        }

        // 生词本里只有刚点过星的那一个（评审 2026-10-16 F-09）。
        var words = _shell!.Store.TranslationLog(vocabularyOnly: true);
        log.AppendLine(FormattableString.Invariant(
            $"vocabulary|count={words.Count}|headword={words.FirstOrDefault()?.Word?.Headword}"));

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(DebugOverrides.ProbeDirectory!, "panel-dictionary.log"), log + "done" + Environment.NewLine);
    }
#endif

    /// <summary>
    /// Files a kept translation, silently — 「自动复制译文」 keeps every result
    /// (用户需求 2026-10-05), and a sentence each time would be noise. The link
    /// is made only when the original was itself recorded — a selection captured
    /// straight off the screen never entered history, and inventing a link
    /// would be pointing at nothing. A result identical to the newest entry is
    /// not filed twice: switching template or retrying can land the same text
    /// again, and the auto path plus a later click on the button would too.
    /// </summary>
    /// <returns>Whether a new entry was written.</returns>
    public bool KeepTranslation(string original, string translated)
    {
        var shell = _shell!;

        if (shell.Pipeline is null || string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        if (shell.Store.MostRecent() is { Kind: EntryKind.Text } newest && newest.Text == translated)
        {
            return false;
        }

        var linked = shell.Store.Recent(limit: 200)
            .FirstOrDefault(entry => entry.Text == original && entry.TranslatedFrom is null)
            ?.Id;

        return shell.Pipeline.RecordTranslation(translated, linked);
    }

    /// <summary>面板先关，朗读服务随行——原 OnExit 中间的两步。</summary>
    public void Shutdown()
    {
        _panel?.CloseForGood();
        _speech?.Dispose();
    }
}
