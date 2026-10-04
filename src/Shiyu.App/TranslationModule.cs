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
                shell.Hotkeys, shell.Writer, () => shell.Settings.BuildTranslationBackend(), shell.Settings,
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
                    latest => latest with { TranslationBackend = TranslationBackendKind.Free }));
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
    /// 单词词典的路（票 35 + 2026-09-27 回退修正）：英文单词先走免费词典
    /// （600ms 预算），不可达或没查到时**回退 LLM 词典化**（8s 预算，秒级
    /// 迟到也照常上屏）——实测 dictionaryapi.dev 在本机网络完全不可达，无
    /// 回退等于没有卡。中文单词直接 LLM 词典化；其余文本不吃词典卡。
    /// 回退只认自备密钥——公共通道只有 /translate 一张脸；没有自己的后端
    /// 时慢路缺席，行为退回票 35 原状。
    /// </summary>
    private IDictionaryApi? BuildDictionary(string word)
        => DictionaryWord.IsEnglishWord(word)
            ? new FallbackDictionary(
                new BudgetedDictionary(new FreeDictionaryApi(HttpClients.Shared)),
                OwnKeyDictionary())
            : DictionaryWord.IsChineseWord(word)
                ? OwnKeyDictionary()
                : null;

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
    /// 自备密钥的 LLM 词典路：有 key 才有路。8s 预算罩住流式取卡——比免费
    /// 路宽一个数量级，因为它是兜底，慢到也仍然胜过没有卡。
    /// </summary>
    private IDictionaryApi? OwnKeyDictionary()
        => string.IsNullOrWhiteSpace(_shell!.Settings.BackendApiKey)
            ? null
            : new BudgetedDictionary(
                new LlmDictionaryApi(BuildOwnKeyBackend()),
                TimeSpan.FromSeconds(8));

    /// <summary>
    /// Files a kept translation. The link is made only when the original was
    /// itself recorded — a selection captured straight off the screen never
    /// entered history, and inventing a link would be pointing at nothing.
    /// </summary>
    public void SaveTranslationToHistory(string original, string translated)
    {
        var shell = _shell!;

        if (shell.Pipeline is null)
        {
            return;
        }

        var linked = shell.Store.Recent(limit: 200)
            .FirstOrDefault(entry => entry.Text == original && entry.TranslatedFrom is null)
            ?.Id;

        shell.Pipeline.RecordTranslation(translated, linked);
        shell.TellUser("译文已存入历史。");
    }

    /// <summary>面板先关，朗读服务随行——原 OnExit 中间的两步。</summary>
    public void Shutdown()
    {
        _panel?.CloseForGood();
        _speech?.Dispose();
    }
}
