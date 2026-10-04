using System.Net;
using System.Text.Json;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 免费引擎的设置面（票 41、ADR-0013）：第三种翻译方式、存量用户不动、
/// 隐私披露。分段存的是下标、设置存的是枚举名——下标与枚举对不上，选了
/// 「免费引擎」会悄悄存成别的（票 36 的阻塞级缺陷就是这里错位）。
/// </summary>
public class FreeEngineSettingsTests
{
    /// <summary>
    /// 一份"自备密钥已配齐"的设置，按旧版设置文件的形状解析出来——存量用户真实的样子。
    /// 不在测试里直接写密钥字段：自备密钥的存取方式在别的票里会变（密钥绑定来源），
    /// 而旧文件形状的解析在任何版本里都得到"已配置"。
    /// </summary>
    private static AppSettings OwnKeyConfigured()
    {
        Assert.True(AppSettings.TryParse(
            """{"BackendBaseUrl":"https://api.example.com/v1","BackendModel":"m","BackendApiKey":"k"}""",
            out var settings));
        return settings!;
    }

    [Fact]
    public void The_free_engine_is_the_third_kind_and_the_first_two_keep_their_numbers()
    {
        // 追加在末尾：Relay=0、OwnKey=1 的下标不动，存量设置文件与分段下标都不受影响。
        Assert.Equal(0, (int)TranslationBackendKind.Relay);
        Assert.Equal(1, (int)TranslationBackendKind.OwnKey);
        Assert.Equal(2, (int)TranslationBackendKind.Free);
        Assert.Equal(3, Enum.GetValues<TranslationBackendKind>().Length);
    }

    [Fact]
    public void The_segment_offers_the_free_engine_as_the_third_choice()
    {
        var item = SettingsSchema.Find("service.backend-kind")!;

        Assert.Equal(SettingsControl.Segmented, item.Control);
        Assert.Equal(3, item.ChoiceList.Length);
        Assert.Contains("免费引擎", item.ChoiceList[(int)TranslationBackendKind.Free]);
    }

    [Fact]
    public void Every_kind_lands_on_its_own_segment_label()
    {
        var item = SettingsSchema.Find("service.backend-kind")!;

        foreach (var (kind, label) in new[]
                 {
                     (TranslationBackendKind.Relay, "公共通道"),
                     (TranslationBackendKind.OwnKey, "自备密钥"),
                     (TranslationBackendKind.Free, "免费引擎"),
                 })
        {
            Assert.Contains(label, item.ChoiceList[(int)kind]);
        }
    }

    [Fact]
    public void The_disclosure_says_where_the_text_goes_that_it_is_a_web_interface_and_what_stays_home()
    {
        var hint = SettingsSchema.Find("service.backend-kind")!.Hint!;

        // 票面的披露文案：文本发给谁、没有账号、不是正式 API、历史不出机器。
        Assert.Contains("被翻译的文本发给微软必应翻译的网页接口", hint);
        Assert.Contains("不可用时改发腾讯交互翻译", hint);
        Assert.Contains("无需账号", hint);
        Assert.Contains("这是网页接口而非正式 API，可能随时变更或限流", hint);
        Assert.Contains("剪贴板历史本身不出机器", hint);

        // 升级路径写明：更好的质量与 AI 动作要自备密钥。
        Assert.Contains("更好的质量与 AI 动作需要自备密钥", hint);
    }

    [Fact]
    public void The_free_engine_is_searchable()
    {
        var keywords = SettingsSchema.Find("service.backend-kind")!.KeywordList;

        Assert.Contains("免费引擎", keywords);
        Assert.Contains("必应", keywords);
        Assert.Contains("腾讯", keywords);
    }

    [Fact]
    public void There_is_no_engine_picker_and_no_google_switch()
    {
        // 设置项越少越可信（CONTEXT.md）：引擎的先后由兜底自己定，不给二级选择，
        // 也不加谷歌开关。翻译页的条目里不出现任何引擎名的选项。
        var items = SettingsSchema.Tree.Single(page => page.Id == "translate").Sections
            .SelectMany(section => section.Items)
            .ToList();

        Assert.DoesNotContain(items, item => item.Id.Contains("engine", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, item => item.Label.Contains("谷歌") || item.Label.Contains("Google"));
        Assert.DoesNotContain(
            SettingsSchema.Find("service.backend-kind")!.ChoiceList,
            choice => choice.Contains("谷歌") || choice.Contains("必应") || choice.Contains("腾讯"));
    }

    [Fact]
    public void Free_is_always_configured_even_with_no_key_at_all()
    {
        var settings = new AppSettings { TranslationBackend = TranslationBackendKind.Free };

        Assert.True(settings.IsTranslationConfigured);

        // 而自备密钥的就绪与否与它无关——动作、批量翻译按这一项门控。
        Assert.False(settings.Backend.IsConfigured);
    }

    [Fact]
    public void The_other_kinds_keep_their_old_verdicts()
    {
        Assert.False(new AppSettings().IsTranslationConfigured);
        Assert.False(new AppSettings { TranslationBackend = TranslationBackendKind.Relay }.IsTranslationConfigured);
        Assert.True(OwnKeyConfigured().IsTranslationConfigured);
    }

    [Fact]
    public void Building_for_free_gives_the_free_engine()
    {
        var backend = new AppSettings { TranslationBackend = TranslationBackendKind.Free }
            .BuildTranslationBackend();

        Assert.IsType<FreeEngineBackend>(backend);
    }

    [Fact]
    public void The_type_default_stays_the_users_own_key()
    {
        // ADR-0013 决策 4：只有走完引导翻译屏的新用户才写入 Free；类型默认值不动。
        Assert.Equal(TranslationBackendKind.OwnKey, new AppSettings().TranslationBackend);
    }

    [Fact]
    public void An_existing_users_file_is_not_upgraded_to_the_free_engine()
    {
        // 文本发往哪里，不做静默升级：缺这一项的旧文件、明写自备密钥的文件，都原样读回。
        Assert.True(AppSettings.TryParse("""{"TargetLanguage":"English"}""", out var old));
        Assert.Equal(TranslationBackendKind.OwnKey, old!.TranslationBackend);

        Assert.True(AppSettings.TryParse("""{"TranslationBackend":"OwnKey","BackendApiKey":""}""", out var own));
        Assert.Equal(TranslationBackendKind.OwnKey, own!.TranslationBackend);
    }

    [Fact]
    public void The_choice_is_stored_by_name_and_survives_a_round_trip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "shiyu-free-engine", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            new AppSettings { TranslationBackend = TranslationBackendKind.Free }.Save(path);

            // 存的是枚举名，不是下标：重排枚举也不会让文件改口。
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("Free", document.RootElement.GetProperty("TranslationBackend").GetString());
            Assert.Equal(TranslationBackendKind.Free, AppSettings.Load(path).TranslationBackend);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

/// <summary>
/// 免费引擎之外的三条路维持只走自备密钥（票 41）：它没有 prompt，不是通用模型。
/// 这些测试防的是回归——哪天有人让 FreeEngineBackend 实现了流式模型接口，
/// 批量请求就会悄悄打到网页接口上。
/// </summary>
public class FreeEngineBoundaryTests
{
    [Fact]
    public void The_free_backend_is_not_a_streaming_model_so_the_fallback_to_the_own_key_kicks_in()
    {
        // TranslationModule.BuildStreamingModel：BuildTranslationBackend() as IStreamingModel
        // ?? 自备密钥后端。Free 下前一半是 null，后一半接手。
        var backend = new AppSettings { TranslationBackend = TranslationBackendKind.Free }
            .BuildTranslationBackend();

        Assert.Null(backend as IStreamingModel);
    }

    [Fact]
    public void An_own_key_backend_is_still_a_streaming_model()
    {
        // 默认就是自备密钥；后端类型不取决于密钥配没配齐。
        var backend = new AppSettings().BuildTranslationBackend();

        Assert.IsAssignableFrom<IStreamingModel>(backend);
    }

    [Theory]
    [InlineData(typeof(AgentRun))]
    [InlineData(typeof(LlmDictionaryApi))]
    [InlineData(typeof(TranslationBatch))]
    public void Actions_the_llm_dictionary_and_batch_translation_only_accept_streaming_models(Type consumer)
    {
        // 类型系统就是闸门：这三位的构造函数要的是 IStreamingModel，免费引擎不是，
        // 编译都过不了——批量请求不可能打到网页接口上。
        var constructor = Assert.Single(consumer.GetConstructors());
        var parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToList();

        Assert.Contains(typeof(IStreamingModel), parameters);
        Assert.DoesNotContain(typeof(ITranslationBackend), parameters);
    }

    [Fact]
    public void Choosing_the_free_engine_does_not_light_up_the_ai_entries()
    {
        // 管理窗的 AI 入口按 Backend.IsConfigured 门控：选了免费引擎但没有自备密钥，
        // 它们照旧是灰的，提示「需要自备密钥」。
        var settings = new AppSettings { TranslationBackend = TranslationBackendKind.Free };

        Assert.False(settings.Backend.IsConfigured);
        Assert.True(settings.IsTranslationConfigured);
    }
}

/// <summary>
/// 伪造的 UA 与 Referer 只出现在两个后端自己的请求上（票 41）。写进
/// HttpClients.Shared 的默认头，它就会跟着发给所有大模型服务商。
/// </summary>
public class FreeEngineHeaderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static FreeEngineLanguage Lang(string name) => FreeEngineLanguages.Find(name)!;

    [Fact]
    public async Task The_bing_requests_carry_an_edge_user_agent_and_the_translator_referer()
    {
        var handler = FreeEngineWeb.BingSite(_ => "ok");
        var http = new HttpClient(handler);
        var translator = new BingWebTranslator(http, new BingSessionCache(http, new TestClock(Start)));

        await translator.TranslateAsync("你好", null, Lang("English"), CancellationToken.None);

        var page = handler.Requests.Single(request => FreeEngineWeb.IsPage(request.Message)).Message;
        var post = handler.Requests.Single(request => FreeEngineWeb.IsBingPost(request.Message)).Message;

        Assert.Contains("Edg/130", page.Headers.UserAgent.ToString());
        Assert.Contains("Edg/130", post.Headers.UserAgent.ToString());
        Assert.Equal(new Uri("https://cn.bing.com/translator"), post.Headers.Referrer);
    }

    [Fact]
    public async Task The_tencent_request_carries_a_chrome_user_agent_and_its_own_referer()
    {
        var handler = FreeEngineWeb.Routed((_, _, _) => FreeEngineWeb.TencentTranslation("x"));
        var translator = new TransmartTranslator(new HttpClient(handler));

        await translator.TranslateAsync("hello", null, Lang("Chinese"), CancellationToken.None);

        var message = handler.Requests.Single().Message;
        Assert.Contains("Chrome/130", message.Headers.UserAgent.ToString());
        Assert.DoesNotContain("Edg/", message.Headers.UserAgent.ToString());
        Assert.Equal(new Uri("https://transmart.qq.com/"), message.Headers.Referrer);
    }

    [Fact]
    public async Task The_client_the_translators_use_never_gets_default_headers()
    {
        var handler = FreeEngineWeb.BingSite(_ => "ok");
        var http = new HttpClient(handler);
        var bing = new BingWebTranslator(http, new BingSessionCache(http, new TestClock(Start)));

        await bing.TranslateAsync("你好", null, Lang("English"), CancellationToken.None);

        Assert.Empty(http.DefaultRequestHeaders.UserAgent);
        Assert.Null(http.DefaultRequestHeaders.Referrer);
    }

    [Fact]
    public void The_shared_client_stays_clean_whatever_the_free_engine_builds()
    {
        // 生产路径用 HttpClients.Shared 做连接池；构造后端、取预热都不许碰它的默认头。
        _ = new FreeEngineBackend();
        _ = new AppSettings { TranslationBackend = TranslationBackendKind.Free }.BuildTranslationBackend();

        Assert.Empty(HttpClients.Shared.DefaultRequestHeaders.UserAgent);
        Assert.Null(HttpClients.Shared.DefaultRequestHeaders.Referrer);
        Assert.Empty(HttpClients.Shared.DefaultRequestHeaders);
    }
}

/// <summary>
/// 预热（票 41）：当前翻译方式是免费引擎时，徽标出现或翻译热键按下的那一刻，
/// 会话缺失或快过期就在后台取一次。不设定时器；失败不提示。
/// </summary>
public class FreeEngineWarmUpTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly AppSettings OnFree = new() { TranslationBackend = TranslationBackendKind.Free };

    private static (BingSessionCache Cache, FreeEngineHealth Health, ScriptedHandler Handler, TestClock Clock) Open(
        Func<int, HttpResponseMessage>? respond = null)
    {
        var clock = new TestClock(Start);
        var handler = new ScriptedHandler(respond ?? (_ => FreeEngineWeb.BingPage()));
        return (new BingSessionCache(new HttpClient(handler), clock), new FreeEngineHealth(clock), handler, clock);
    }

    [Fact]
    public async Task A_missing_session_is_fetched_once()
    {
        var (cache, health, handler, _) = Open();

        await FreeEngineBackend.WarmUpAsync(OnFree, cache, health);

        Assert.Single(handler.Requests);
        Assert.False(cache.NeedsFetch);
    }

    [Fact]
    public async Task A_fresh_session_is_left_alone()
    {
        var (cache, health, handler, _) = Open();

        await FreeEngineBackend.WarmUpAsync(OnFree, cache, health);
        await FreeEngineBackend.WarmUpAsync(OnFree, cache, health);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_session_about_to_expire_is_fetched_again()
    {
        var (cache, health, handler, clock) = Open();
        await FreeEngineBackend.WarmUpAsync(OnFree, cache, health);

        clock.Advance(TimeSpan.FromSeconds(3525));
        await FreeEngineBackend.WarmUpAsync(OnFree, cache, health);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(TranslationBackendKind.OwnKey)]
    [InlineData(TranslationBackendKind.Relay)]
    public async Task Other_translation_methods_warm_nothing(TranslationBackendKind kind)
    {
        // 没选免费引擎，就不替用户联系微软——哪怕只是取一个页面。
        var (cache, health, handler, _) = Open();

        await FreeEngineBackend.WarmUpAsync(new AppSettings { TranslationBackend = kind }, cache, health);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Bing_in_its_cooldown_is_not_warmed()
    {
        var (cache, health, handler, _) = Open();
        health.MarkBingUnhealthy();

        await FreeEngineBackend.WarmUpAsync(OnFree, cache, health);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_failed_warm_up_is_silent_and_leaves_the_next_real_fetch_free_to_try()
    {
        var (cache, health, handler, _) = Open(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : FreeEngineWeb.BingPage());

        // 失败不提示：不抛、不弹、不记入冷却（冷却只认真翻译里的失败）。
        await FreeEngineBackend.WarmUpAsync(OnFree, cache, health);
        Assert.True(health.BingHealthy);

        var session = await cache.GetAsync();
        Assert.NotNull(session);
        Assert.Equal(2, handler.Requests.Count);
    }
}

/// <summary>
/// 引导第 4 屏（票 41、ADR-0013）：预选免费引擎；只有走完这一屏（看过披露）的
/// 新用户才写入 Free，"跳过，用默认设置"的仍是自备密钥，重跑引导的存量用户
/// 不被默认选项悄悄改道。
/// </summary>
public class OnboardingBackendChoiceTests
{
    /// <summary>已经配齐自备密钥的设置（按旧版文件形状解析，见 FreeEngineSettingsTests）。</summary>
    private static readonly AppSettings WithKey = Parse(
        """{"BackendBaseUrl":"https://api.example.com/v1","BackendModel":"m","BackendApiKey":"k"}""");

    private static AppSettings Parse(string json)
    {
        Assert.True(AppSettings.TryParse(json, out var settings));
        return settings!;
    }

    [Fact]
    public void A_first_run_with_nothing_configured_starts_on_the_free_engine()
        => Assert.Equal(
            TranslationBackendKind.Free,
            OnboardingBackendChoice.Preselect(new AppSettings(), firstRun: true));

    [Fact]
    public void A_first_run_with_a_key_already_in_place_is_not_moved_off_it()
        => Assert.Equal(
            TranslationBackendKind.OwnKey,
            OnboardingBackendChoice.Preselect(WithKey, firstRun: true));

    [Fact]
    public void A_rerun_keeps_whatever_was_saved()
    {
        // 设置 → 关于 → 新手引导：存量用户一路点「下一步」不该被预选改道。
        Assert.Equal(
            TranslationBackendKind.OwnKey,
            OnboardingBackendChoice.Preselect(new AppSettings(), firstRun: false));
        Assert.Equal(
            TranslationBackendKind.OwnKey,
            OnboardingBackendChoice.Preselect(WithKey, firstRun: false));
        Assert.Equal(
            TranslationBackendKind.Free,
            OnboardingBackendChoice.Preselect(
                new AppSettings { TranslationBackend = TranslationBackendKind.Free }, firstRun: false));
    }

    [Theory]
    [InlineData(false, true, TranslationBackendKind.Free)]
    [InlineData(true, false, TranslationBackendKind.Free)]
    [InlineData(true, true, TranslationBackendKind.Free)]
    public void The_free_engine_is_written_only_when_the_screen_was_completed_or_it_was_picked(
        bool pickedExplicitly, bool completedScreen, TranslationBackendKind expected)
        => Assert.Equal(
            expected,
            OnboardingBackendChoice.KindToWrite(TranslationBackendKind.Free, pickedExplicitly, completedScreen));

    [Fact]
    public void Skipping_on_the_translate_screen_without_picking_writes_nothing_about_the_method()
        => Assert.Null(OnboardingBackendChoice.KindToWrite(
            TranslationBackendKind.Free, pickedExplicitly: false, completedScreen: false));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_own_key_card_keeps_writing_the_own_key_as_before(bool pickedExplicitly, bool completedScreen)
        => Assert.Equal(
            TranslationBackendKind.OwnKey,
            OnboardingBackendChoice.KindToWrite(TranslationBackendKind.OwnKey, pickedExplicitly, completedScreen));
}
