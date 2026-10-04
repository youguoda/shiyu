using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 服务商预设的合同（票 08）：数值全部出自 2026-10-01 的官方文档调研，
/// 这里逐家钉住——改任何一条都得先重新核实，不许凭记忆填。
/// </summary>
public class ProviderPresetCatalogTests
{
    [Fact]
    public void The_default_dropdown_lists_four_providers_in_the_researched_order()
    {
        Assert.Equal(
            ["bailian", "deepseek", "zhipu", "siliconflow"],
            ProviderPresets.Primary.Select(preset => preset.Id).ToArray());
    }

    [Fact]
    public void Kimi_is_offered_under_more_not_in_the_primary_list()
    {
        // 兼容性差（温度固定值）放「更多」，不该与四家并列。
        Assert.Equal(["kimi"], ProviderPresets.More.Select(preset => preset.Id).ToArray());
        Assert.DoesNotContain(ProviderPresets.Primary, preset => preset.Id == "kimi");
    }

    [Fact]
    public void Bailian_defaults_to_qwen_flash_which_needs_no_extra_fields()
    {
        // 唯一不加字段也不思考的一家，所以排第一。
        var bailian = ProviderPresets.Find("bailian")!;
        Assert.Equal("qwen-flash", bailian.DefaultModel);
        Assert.Equal(["qwen-turbo"], bailian.AltModels);
        Assert.Null(bailian.ExtraBody);
        Assert.Equal(1.99, bailian.MaxTemperature);
        Assert.True(bailian.SendTemperature);
    }

    [Fact]
    public void DeepSeek_disables_thinking_and_allows_up_to_two()
    {
        var deepseek = ProviderPresets.Find("deepseek")!;
        Assert.Equal("deepseek-flash", deepseek.DefaultModel);
        Assert.Equal(
            "disabled",
            deepseek.ExtraBody!["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(2.0, deepseek.MaxTemperature);
    }

    [Fact]
    public void Zhipu_is_free_and_caps_temperature_at_one()
    {
        var zhipu = ProviderPresets.Find("zhipu")!;
        Assert.Equal("glm-4-flash-250414", zhipu.DefaultModel);
        Assert.Null(zhipu.ExtraBody);
        Assert.Equal(1.0, zhipu.MaxTemperature);
    }

    [Fact]
    public void SiliconFlow_disables_thinking_with_its_own_field_name()
    {
        var siliconflow = ProviderPresets.Find("siliconflow")!;
        Assert.Equal("deepseek-ai/DeepSeek-V4-Flash", siliconflow.DefaultModel);
        Assert.False(siliconflow.ExtraBody!["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public void Kimi_sends_no_temperature_because_its_value_is_fixed()
    {
        var kimi = ProviderPresets.Find("kimi")!;
        Assert.False(kimi.SendTemperature);
        Assert.Null(kimi.MaxTemperature);
        Assert.Equal(
            "disabled",
            kimi.ExtraBody!["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Every_preset_has_a_key_application_url_and_a_clean_base_url()
    {
        foreach (var preset in ProviderPresets.All)
        {
            Assert.StartsWith("https://", preset.ApiKeyUrl, StringComparison.Ordinal);
            Assert.StartsWith("https://", preset.BaseUrl, StringComparison.Ordinal);

            // 智谱官方写法带尾斜杠；入库统一不带，拼接层再 TrimEnd 兜底。
            Assert.False(preset.BaseUrl.EndsWith('/'), preset.Id + " 的地址不该带尾斜杠");
            Assert.NotEqual(preset.DefaultModel, string.Empty);
        }

        Assert.Equal(ProviderPresets.All.Count, ProviderPresets.All.Select(p => p.Id).Distinct().Count());
    }
}

/// <summary>
/// 预设的解析规则：地址与模型仍与预设一致才算选着了；手改即自定义，
/// 附加字段与温度规则随之失效。这是数据层的最终裁决，界面只是照着演。
/// </summary>
public class ProviderPresetResolutionTests
{
    private static AppSettings For(string presetId, string baseUrl, string model)
        => new()
        {
            BackendPresetId = presetId,
            BackendBaseUrl = baseUrl,
            BackendModel = model,
            BackendApiKey = "key",
        };

    [Fact]
    public void A_matching_address_and_model_resolves_to_the_preset()
    {
        var settings = For("deepseek", "https://api.deepseek.com", "deepseek-flash");

        Assert.Equal("deepseek", ProviderPresets.ResolveFor(settings)!.Id);
    }

    [Fact]
    public void An_alternate_model_of_the_same_provider_keeps_the_preset()
    {
        var settings = For("bailian",
            "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-turbo");

        Assert.Equal("bailian", ProviderPresets.ResolveFor(settings)!.Id);
    }

    [Fact]
    public void A_hand_edited_model_drops_the_preset()
    {
        var settings = For("bailian",
            "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen3.8-max");

        Assert.Null(ProviderPresets.ResolveFor(settings));
    }

    [Fact]
    public void A_different_address_drops_the_preset_even_with_a_known_id()
    {
        // 手改文件里的地址不该让别家的扩展字段发给这一家。
        var settings = For("deepseek", "https://api.siliconflow.cn/v1", "deepseek-flash");

        Assert.Null(ProviderPresets.ResolveFor(settings));
    }

    [Fact]
    public void An_unknown_or_absent_id_resolves_to_nothing()
    {
        Assert.Null(ProviderPresets.ResolveFor(For("volces", "https://x", "y")));
        Assert.Null(ProviderPresets.ResolveFor(For("", "https://x", "y")));
        Assert.Null(ProviderPresets.ResolveFor(new AppSettings()));
    }

    [Fact]
    public void A_trailing_slash_in_the_stored_address_still_matches()
    {
        var settings = For("zhipu", "https://open.bigmodel.cn/api/paas/v4/", "glm-4-flash-250414");

        Assert.Equal("zhipu", ProviderPresets.ResolveFor(settings)!.Id);
    }
}

/// <summary>
/// 预设落到线上请求体的形状：附加字段在顶层、温度限幅或整段不发、
/// BaseUrl 拼接前 TrimEnd，以及思考内容绝不混进译文。
/// </summary>
public class PresetWireFormatTests
{
    private static async Task<JsonNode> CaptureBody(
        Func<HttpClient, OpenAiCompatibleBackend> build, double temperature = 0.2)
    {
        var handler = new ScriptedHandler(_ =>
            BackendTransport.Stream("""{"choices":[{"delta":{"content":"你好"}}]}""", "[DONE]"));
        var backend = build(new HttpClient(handler));

        await foreach (var _ in backend.TranslateAsync(
            new TranslationRequest("hello", "Chinese") { Temperature = temperature },
            CancellationToken.None))
        {
        }

        return JsonNode.Parse(handler.Requests.Single().Body!)!;
    }

    [Fact]
    public async Task Extra_body_fields_ride_on_top_of_the_request()
    {
        var body = await CaptureBody(http => new OpenAiCompatibleBackend(
            BackendTransport.Configured(), http,
            extraBody: new JsonObject { ["thinking"] = new JsonObject { ["type"] = "disabled" } }));

        Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());

        // 附加是叠加，不是替换：标准字段原样都在。
        Assert.Equal("some-model", body["model"]!.GetValue<string>());
        Assert.NotNull(body["messages"]);
    }

    [Fact]
    public async Task Enable_thinking_false_travels_as_a_json_boolean()
    {
        var body = await CaptureBody(http => new OpenAiCompatibleBackend(
            BackendTransport.Configured(), http,
            extraBody: new JsonObject { ["enable_thinking"] = false }));

        Assert.False(body["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_temperature_above_the_preset_cap_is_clamped()
    {
        var body = await CaptureBody(http => new OpenAiCompatibleBackend(
            BackendTransport.Configured(), http, maxTemperature: 1.0),
            temperature: 1.3);

        Assert.Equal(1.0, (double)body["temperature"]!);
    }

    [Fact]
    public async Task A_temperature_below_the_cap_is_left_alone()
    {
        var body = await CaptureBody(http => new OpenAiCompatibleBackend(
            BackendTransport.Configured(), http, maxTemperature: 1.0),
            temperature: 0.2);

        Assert.Equal(0.2, (double)body["temperature"]!);
    }

    [Fact]
    public async Task A_preset_that_forbids_temperature_omits_it_entirely()
    {
        // Kimi：温度是固定值，发出去就是一次注定失败的请求。
        var body = await CaptureBody(http => new OpenAiCompatibleBackend(
            BackendTransport.Configured(), http, sendTemperature: false));

        Assert.Null(body["temperature"]);
    }

    [Fact]
    public async Task A_trailing_slash_in_the_base_url_does_not_double_up()
    {
        // 智谱官方地址带尾斜杠；拼接前 TrimEnd，别拼出 //chat/completions。
        var handler = new ScriptedHandler(_ =>
            BackendTransport.Stream("""{"choices":[{"delta":{"content":"你好"}}]}""", "[DONE]"));
        var backend = new OpenAiCompatibleBackend(
            new TranslationBackendOptions("https://api.example.com/v1/", "m", "k"),
            new HttpClient(handler));

        await foreach (var _ in backend.TranslateAsync(
            new TranslationRequest("hello", "Chinese"), CancellationToken.None))
        {
        }

        Assert.Equal(
            "https://api.example.com/v1/chat/completions",
            handler.Requests.Single().Message.RequestUri!.ToString());
    }

    [Fact]
    public void Reasoning_content_is_ignored_and_only_content_becomes_the_translation()
    {
        // 关思考失败的模型会先流 reasoning_content：解析只认 delta.content，
        // 思考永远不该混进译文——钉住，别让"顺手也读一下"回来。
        Assert.Null(OpenAiCompatibleBackend.ExtractContent(
            """{"choices":[{"delta":{"reasoning_content":"让我想想…"}}]}"""));

        Assert.Equal("你好", OpenAiCompatibleBackend.ExtractContent(
            """{"choices":[{"delta":{"reasoning_content":"思考","content":"你好"}}]}"""));
    }
}

/// <summary>百炼 403 免费额度耗尽的专门文案（票 08）。</summary>
public class BailianQuotaFailureTests
{
    private static async Task<string> FailureOf(HttpResponseMessage response)
    {
        var handler = new ScriptedHandler(_ => response);
        var backend = new OpenAiCompatibleBackend(
            BackendTransport.Configured(), new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<TranslationFailedException>(
            () => Drain(backend));
        return failure.Message;

        static async Task Drain(OpenAiCompatibleBackend backend)
        {
            await foreach (var _ in backend.TranslateAsync(
                new TranslationRequest("hello", "Chinese"), CancellationToken.None))
            {
            }
        }
    }

    [Fact]
    public async Task FreeTierOnly_is_told_as_quota_exhausted_not_bad_credentials()
    {
        var message = await FailureOf(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{"error":{"code":"AllocationQuota.FreeTierOnly","message":"Free quota exhausted"}}""",
                Encoding.UTF8,
                "application/json"),
        });

        Assert.Contains("免费额度已用完", message);
        Assert.Contains("实名或充值", message);
        Assert.DoesNotContain("凭据无效", message);
    }

    [Fact]
    public async Task A_plain_403_without_that_code_stays_a_credential_problem()
    {
        var message = await FailureOf(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{"error":{"code":"InvalidApiKey","message":"nope"}}""",
                Encoding.UTF8,
                "application/json"),
        });

        Assert.Contains("凭据无效", message);
    }
}

/// <summary>公共通道的上线闸门（ADR-0009）与它对配置判定的影响。</summary>
public class RelayChannelGateTests
{
    [Fact]
    public void The_relay_channel_is_gated_off_until_its_launch_conditions_are_met()
        => Assert.False(RelayChannel.Available);

    [Fact]
    public void A_relay_choice_is_not_a_configured_backend_while_the_channel_is_gated_off()
    {
        var onRelay = new AppSettings
        {
            TranslationBackend = TranslationBackendKind.Relay,
            RelayEndpoint = RelayBackend.DefaultEndpoint,
        };

        // 闸门关着：选中公共通道不等于有一条能走的路。
        Assert.False(onRelay.IsTranslationConfigured);

        // 票 29：自备密钥是"绑着来源的密钥"——经唯一的写入口保存，才算配好。
        var onOwnKey = new AppSettings
        {
            BackendBaseUrl = "https://api.example.com/v1",
            BackendModel = "m",
        }.WithApiKey("k");
        Assert.True(onOwnKey.IsTranslationConfigured);
    }

    [Fact]
    public void Building_a_backend_for_a_relay_user_falls_back_to_their_own_key()
    {
        var settings = new AppSettings
        {
            TranslationBackend = TranslationBackendKind.Relay,
            RelayEndpoint = RelayBackend.DefaultEndpoint,
            BackendBaseUrl = "https://api.example.com/v1",
            BackendModel = "m",
            BackendApiKey = "k",
        };

        Assert.IsType<OpenAiCompatibleBackend>(settings.BuildTranslationBackend());
    }

    [Fact]
    public void Preset_settings_survive_a_round_trip_through_disk()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "shiyu-preset-roundtrip", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            new AppSettings
            {
                BackendPresetId = "deepseek",
                BackendBaseUrl = "https://api.deepseek.com",
                BackendModel = "deepseek-flash",
                RelayUnavailableNoticed = true,
            }.Save(path);

            var loaded = AppSettings.Load(path);

            Assert.Equal("deepseek", loaded.BackendPresetId);
            Assert.True(loaded.RelayUnavailableNoticed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

/// <summary>「测试连接」的三态人话（票 08）。</summary>
public class ConnectionProbeTests
{
    private const string Key = "sk-test-12345";

    private static TranslationBackendOptions Options()
        => new("https://api.example.com/v1", "some-model", Key);

    private static Task<ConnectionTestOutcome> Probe(
        ScriptedHandler handler, ProviderPreset? preset = null, TimeSpan? timeout = null)
        => ConnectionProbe.TestAsync(Options(), preset, new HttpClient(handler));

    [Fact]
    public async Task A_live_endpoint_answers_success_with_the_round_trip_time()
    {
        var handler = new ScriptedHandler(_ => BackendTransport.Stream(
            """{"choices":[{"message":{"content":"你好"}}]}""", "[DONE]"));

        var outcome = await Probe(handler);

        Assert.Equal(ConnectionTestVerdict.Success, outcome.Verdict);
        Assert.NotNull(outcome.Elapsed);
        Assert.True(outcome.Elapsed!.Value >= TimeSpan.Zero);
    }

    [Fact]
    public async Task The_probe_sends_a_tiny_request_not_a_full_translation()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        await Probe(handler);

        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.True(body["max_tokens"]!.GetValue<int>() <= 32);
        Assert.Equal("hi", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_rejected_key_is_a_plain_invalid_key_result()
    {
        var handler = new ScriptedHandler(_ => BackendTransport.Status(401));

        var outcome = await Probe(handler);

        Assert.Equal(ConnectionTestVerdict.InvalidKey, outcome.Verdict);
        Assert.Contains("密钥", outcome.Message);
    }

    [Fact]
    public async Task Bailed_free_tier_exhaustion_reports_reachable_with_a_caveat()
    {
        // 403 FreeTierOnly：地址、密钥、模型全对——说"成功"并附一句额度，
        // 而不是把用户支去改一个没问题的密钥。
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{"error":{"code":"AllocationQuota.FreeTierOnly"}}""",
                Encoding.UTF8,
                "application/json"),
        });

        var outcome = await Probe(handler);

        Assert.Equal(ConnectionTestVerdict.Success, outcome.Verdict);
        Assert.Contains("免费额度已用完", outcome.Message);
    }

    [Fact]
    public async Task A_wrong_address_or_model_is_unreachable()
    {
        var handler = new ScriptedHandler(_ => BackendTransport.Status(404));

        var outcome = await Probe(handler);

        Assert.Equal(ConnectionTestVerdict.Unreachable, outcome.Verdict);
        Assert.Contains("地址或模型", outcome.Message);
    }

    [Fact]
    public async Task A_network_failure_is_unreachable_in_plain_words()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("socket gone"));

        var outcome = await Probe(handler);

        Assert.Equal(ConnectionTestVerdict.Unreachable, outcome.Verdict);
        Assert.Contains("连不上", outcome.Message);
    }

    [Fact]
    public async Task A_genuinely_slow_endpoint_is_reported_as_a_timeout()
    {
        using var slow = new SlowHandler(TimeSpan.FromSeconds(5));

        var outcome = await ConnectionProbe.TestAsync(
            Options(), null, new HttpClient(slow), probeTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(ConnectionTestVerdict.Unreachable, outcome.Verdict);
        Assert.Contains("超时", outcome.Message);
    }

    [Fact]
    public async Task An_incomplete_form_is_refused_before_anything_is_sent()
    {
        var handler = new ScriptedHandler(_ => BackendTransport.Status(200));

        var outcome = await ConnectionProbe.TestAsync(
            new TranslationBackendOptions("https://api.example.com/v1", "", ""),
            null, new HttpClient(handler));

        Assert.Equal(ConnectionTestVerdict.Unreachable, outcome.Verdict);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_key_travels_only_in_the_authorization_header()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        await Probe(handler);

        var request = handler.Requests.Single();
        Assert.Equal(Key, request.Message.Headers.Authorization?.Parameter);
        Assert.DoesNotContain(Key, request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preset_that_forbids_temperature_shapes_the_probe_too()
    {
        // 探针与真翻译同一个请求体形状：Kimi 的温度固定值在这里同样不能发。
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        await Probe(handler, ProviderPresets.Find("kimi"));

        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Null(body["temperature"]);
        Assert.Equal(
            "disabled",
            body["thinking"]!["type"]!.GetValue<string>());
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellation)
        {
            await Task.Delay(delay, cancellation);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
