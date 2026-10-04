using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 公共通道的应答搬运：单次 JSON 换成逐句吐出的"流"。
///
/// 线上事实是：上游非流式、中转一次性回完。这里钉住的是客户端对这条
/// 事实的化妆——面板看到的仍是多个片段按序到达，且片段拼回去必须与
/// 服务端译文一字不差（丢掉换行或修剪空白都算破坏）。
/// </summary>
public class RelayStreamingTests
{
    [Fact]
    public void Pieces_rebuild_the_translation_exactly_and_there_are_several_of_them()
    {
        const string translation = "Hello there. How are you? 我很好，谢谢。";

        var pieces = RelayBackend.SplitForStreaming(translation);

        Assert.True(pieces.Count >= 3, $"expected several pieces, got {pieces.Count}");
        Assert.Equal(translation, string.Concat(pieces));
    }

    [Fact]
    public void Newlines_bound_pieces_and_survive_the_rebuild()
    {
        const string translation = "第一行没有句号\nSecond line ends. And one more\n\n尾行";

        var pieces = RelayBackend.SplitForStreaming(translation);

        Assert.Equal(translation, string.Concat(pieces));
        Assert.Contains(pieces, piece => piece.Contains('\n'));
    }

    [Fact]
    public void An_unpunctuated_translation_arrives_as_one_piece()
    {
        var pieces = RelayBackend.SplitForStreaming("一句话没有标点");

        Assert.Equal(["一句话没有标点"], pieces);
    }

    [Fact]
    public void Empty_text_yields_no_pieces()
        => Assert.Empty(RelayBackend.SplitForStreaming(""));

    [Theory]
    [InlineData("3.14 is not two sentences. Nor is 1,000.25!")]
    [InlineData("你好？！……真的吗。")]
    [InlineData("Quote stays.” Then ends.")]
    [InlineData("No punctuation at all, just words")]
    public void Whatever_the_text_the_rebuild_is_exact(string translation)
        => Assert.Equal(translation, string.Concat(RelayBackend.SplitForStreaming(translation)));

    [Fact]
    public void A_very_long_unpunctuated_stretch_is_capped_into_pieces()
    {
        // 2000 字的单句（服务器单请求上限内的极端形状）不能憋成一大块，
        // 否则"流式观感"名存实亡。
        var translation = new string('好', 2000);

        var pieces = RelayBackend.SplitForStreaming(translation);

        Assert.True(pieces.Count > 1);
        Assert.All(pieces, piece => Assert.True(piece.Length <= RelayBackend.MaxPieceChars));
        Assert.Equal(translation, string.Concat(pieces));
    }
}

/// <summary>公共通道的线上纪律：请求形状、零密钥、错误人话、退避重试。</summary>
public class RelayBackendTests
{
    private static RelayBackendOptions Configured()
        => new("https://relay.example.com", "client-1234");

    private static HttpResponseMessage Translation(string text)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JsonObject { ["translation"] = text }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage RelayError(int status, string body)
        => new((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static async Task<List<string>> Drain(
        RelayBackend backend, TranslationRequest? request = null)
    {
        var pieces = new List<string>();
        await foreach (var piece in backend.TranslateAsync(
            request ?? new TranslationRequest("hello", "Chinese"), CancellationToken.None))
        {
            pieces.Add(piece);
        }

        return pieces;
    }

    private static Task<TranslationFailedException> Fails(
        RelayBackend backend, TranslationRequest? request = null)
        => Assert.ThrowsAsync<TranslationFailedException>(() => Drain(backend, request));

    [Fact]
    public void The_client_owns_no_credential()
        => Assert.False(
            typeof(RelayBackendOptions).GetProperties()
                .Any(property => property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)),
            "公共通道的选项里不允许出现任何密钥字段");

    [Fact]
    public async Task An_empty_endpoint_is_refused_up_front()
    {
        var backend = new RelayBackend(new RelayBackendOptions("", "client-1234"));

        var failure = await Assert.ThrowsAsync<TranslationFailedException>(
            () => Drain(backend));

        Assert.Contains("公共通道", failure.Message);
    }

    [Fact]
    public async Task Oversized_text_is_refused_before_any_request_is_sent()
    {
        var handler = new ScriptedHandler(_ => Translation("永不返回"));
        var backend = new RelayBackend(Configured(), new HttpClient(handler));

        var failure = await Fails(backend, new(new string('a', 2001), "Chinese"));

        Assert.Contains("2000", failure.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_request_carries_the_relay_shape_and_nothing_secret()
    {
        var handler = new ScriptedHandler(_ => Translation("你好。世界。"));
        var backend = new RelayBackend(Configured(), new HttpClient(handler));

        await Drain(backend);

        var (message, body) = handler.Requests.Single();

        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal(new Uri("https://relay.example.com/translate"), message.RequestUri);
        Assert.Null(message.Headers.Authorization);

        var sent = JsonNode.Parse(body!)!;
        Assert.Equal("client-1234", (string?)sent["clientId"]);
        Assert.Equal("hello", (string?)sent["text"]);
        Assert.Equal("Chinese", (string?)sent["to"]);
        Assert.Null(sent["from"]);
    }

    [Fact]
    public async Task A_declared_source_language_travels_as_from()
    {
        var handler = new ScriptedHandler(_ => Translation("你好"));
        var backend = new RelayBackend(Configured(), new HttpClient(handler));

        await Drain(backend, new TranslationRequest("hello", "Chinese") { SourceLanguage = "English" });

        var sent = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal("English", (string?)sent["from"]);
    }

    [Fact]
    public async Task The_whole_answer_is_delivered_as_paced_pieces()
    {
        var asked = new List<TimeSpan>();
        var handler = new ScriptedHandler(_ => Translation("第一句。第二句！第三句？"));
        var backend = new RelayBackend(
            Configured(), new HttpClient(handler), pieceDelay: Record(asked));

        var pieces = await Drain(backend);

        Assert.True(pieces.Count >= 3);
        Assert.Equal("第一句。第二句！第三句？", string.Concat(pieces));

        // 节奏出现在片段之间，最后一片之后不再拖时间。
        Assert.Equal(pieces.Count - 1, asked.Count);

        static Func<TimeSpan> Record(List<TimeSpan> into)
            => () =>
            {
                into.Add(TimeSpan.FromMilliseconds(1));
                return TimeSpan.FromMilliseconds(1);
            };
    }

    [Fact]
    public async Task A_quota_denial_is_humanised_with_remaining_and_reset_and_not_retried()
    {
        var handler = new ScriptedHandler(_ => RelayError(429, """
            {"error":{"code":"QUOTA_DEVICE","message":"今日设备免费额度已用完。","remaining":42,"resetAt":"2026-09-28T00:00:00Z"}}
            """));
        var backend = new RelayBackend(Configured(), new HttpClient(handler));

        var failure = await Fails(backend);

        Assert.Contains("额度已用完", failure.Message);
        Assert.Contains("42", failure.Message);
        Assert.Contains("恢复", failure.Message);

        // 配额类拒绝重试一万次也不会多出一个字。
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_bad_request_surfaces_the_servers_own_words()
    {
        var handler = new ScriptedHandler(_ => RelayError(400, """
            {"error":{"code":"TEXT_TOO_LONG","message":"单次最多 2000 字。","limit":2000}}
            """));
        var backend = new RelayBackend(Configured(), new HttpClient(handler));

        var failure = await Fails(backend);

        Assert.Contains("单次最多 2000 字", failure.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Transient_outages_are_retried_with_the_validated_backoff()
    {
        var asked = new List<TimeSpan>();
        var handler = new ScriptedHandler(call => call switch
        {
            1 or 2 => RelayError(503, """{"error":{"code":"UPSTREAM_ERROR","message":"上游暂时不可用。"}}"""),
            _ => Translation("你好。"),
        });
        var backend = new RelayBackend(
            Configured(), new HttpClient(handler), transientBackoff: ZeroBackoff(asked));

        var pieces = await Drain(backend);

        Assert.Equal(["你好。"], pieces);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public async Task A_refusal_that_persists_is_reported_after_its_retries()
    {
        var handler = new ScriptedHandler(_ => RelayError(502, """
            {"error":{"code":"UPSTREAM_ERROR","message":"翻译服务暂时不可用。"}}
            """));
        var backend = new RelayBackend(Configured(), new HttpClient(handler), transientBackoff: ZeroBackoff());

        var failure = await Fails(backend);

        Assert.Contains("翻译服务暂时不可用", failure.Message);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_network_failure_is_a_plain_message()
    {
        var handler = new ThrowingHandler();
        var backend = new RelayBackend(Configured(), new HttpClient(handler), transientBackoff: ZeroBackoff());

        var failure = await Fails(backend);

        Assert.Contains("连接公共通道失败", failure.Message);
    }

    [Fact]
    public async Task An_unparseable_success_is_reported_rather_than_shown_empty()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>maintenance</html>", Encoding.UTF8, "text/html"),
        });
        var backend = new RelayBackend(Configured(), new HttpClient(handler));

        var failure = await Fails(backend);

        Assert.Contains("公共通道", failure.Message);
    }

    [Fact]
    public async Task An_empty_translation_yields_no_pieces_without_failing()
    {
        var handler = new ScriptedHandler(_ => Translation(""));
        var backend = new RelayBackend(Configured(), new HttpClient(handler));

        Assert.Empty(await Drain(backend));
    }

    private static Func<TimeSpan> ZeroBackoff(List<TimeSpan>? asked = null)
        => () =>
        {
            asked?.Add(TimeSpan.Zero);
            return TimeSpan.Zero;
        };

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellation)
            => throw new HttpRequestException("name resolution failed");
    }
}

/// <summary>设置面：第三种后端成为一等选择，但一期不替存量用户改道。</summary>
public class RelaySettingsTests
{
    [Fact]
    public void The_first_run_default_is_still_the_users_own_key()
    {
        // 二期才默认走公共通道；一期擅自改道等于替存量用户决定了数据流向。
        Assert.Equal(TranslationBackendKind.OwnKey, new AppSettings().TranslationBackend);
    }

    [Fact]
    public void The_relay_endpoint_ships_a_default_and_a_client_id_starts_blank()
    {
        var settings = new AppSettings();

        Assert.Equal(RelayBackend.DefaultEndpoint, settings.RelayEndpoint);
        Assert.True(Uri.TryCreate(settings.RelayEndpoint, UriKind.Absolute, out _)
            && settings.RelayEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(string.Empty, settings.RelayClientId);
    }

    [Fact]
    public void The_backend_is_built_for_the_chosen_kind()
    {
        var relay = new AppSettings
        {
            TranslationBackend = TranslationBackendKind.Relay,
            RelayEndpoint = "https://relay.example.com",
            RelayClientId = "client-1234",
        };
        var ownKey = new AppSettings
        {
            TranslationBackend = TranslationBackendKind.OwnKey,
            BackendBaseUrl = "https://api.example.com/v1",
            BackendModel = "some-model",
            BackendApiKey = "some-key",
        };

        // 公共通道未上线（ADR-0009 的闸门关着）：选中它也落到自备密钥后端，
        // 不再架一条注定连不上的路。闸门翻开的那天，这里翻回 RelayBackend。
        var backend = relay.BuildTranslationBackend();
        Assert.IsType<OpenAiCompatibleBackend>(backend);
        ((IDisposable)backend).Dispose();

        var own = ownKey.BuildTranslationBackend();
        Assert.IsType<OpenAiCompatibleBackend>(own);
        ((IDisposable)own).Dispose();
    }

    [Fact]
    public void The_schema_offers_the_relay_as_a_backend_choice()
    {
        var item = SettingsSchema.Find("service.backend-kind");

        Assert.NotNull(item);
        Assert.Equal(SettingsControl.Segmented, item!.Control);

        // 票 41：第三项是免费引擎，追加在末尾，前两项的下标不动。
        Assert.Equal(3, item.ChoiceList.Length);
        Assert.Contains("公共通道", item.ChoiceList[0]);
        Assert.Contains("自备密钥", item.ChoiceList[1]);
        Assert.Contains("免费引擎", item.ChoiceList[2]);
    }

    [Fact]
    public void Each_choice_index_lands_on_its_own_enum_value()
    {
        // 分段控件存的是下标、设置存的是枚举；两端靠"声明顺序一致"对上。
        // 这条不变量坏了，选"公共通道"会悄悄存成自备密钥——钉死它。
        var item = SettingsSchema.Find("service.backend-kind")!;

        Assert.Contains("公共通道", item.ChoiceList[(int)TranslationBackendKind.Relay]);
        Assert.Contains("自备密钥", item.ChoiceList[(int)TranslationBackendKind.OwnKey]);
        Assert.Contains("免费引擎", item.ChoiceList[(int)TranslationBackendKind.Free]);

        // 枚举有几个值，分段就有几项——多一个少一个都是错位的前兆。
        Assert.Equal(Enum.GetValues<TranslationBackendKind>().Length, item.ChoiceList.Length);
    }

    [Fact]
    public void The_disclosure_says_where_the_text_goes_and_where_it_stays()
    {
        var item = SettingsSchema.Find("service.backend-kind");

        // 隐私披露的两半都要在：出机器的是被翻译的文本，不出机器的是历史。
        Assert.NotNull(item);
        Assert.Contains("中转", item!.Hint);
        Assert.Contains("历史", item.Hint);
    }
}
