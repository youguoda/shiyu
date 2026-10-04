using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 腾讯交互翻译（TranSmart）整段翻译器（票 41）。探针 2026-10-04 直连实测给出
/// 两条结论，这里的测试按它们写：source.lang 接受 "auto"（所以没有按文字系统
/// 推断源语言的那一支，也就没有"拉丁=英文"的已知局限）；text_list 里的空串
/// 原位返回、数组长度不变（所以空行直接发，不必本地剔除再回填）。
/// </summary>
public class TransmartTranslatorTests
{
    private static FreeEngineLanguage Lang(string name) => FreeEngineLanguages.Find(name)!;

    private static (TransmartTranslator Translator, ScriptedHandler Handler) Open(
        Func<JsonNode, HttpResponseMessage> respond)
    {
        var handler = FreeEngineWeb.Routed((_, body, _) => respond(JsonNode.Parse(body!)!));
        return (new TransmartTranslator(new HttpClient(handler)), handler);
    }

    private static JsonNode Sent(ScriptedHandler handler)
        => JsonNode.Parse(handler.Requests.Single().Body!)!;

    [Fact]
    public async Task The_request_has_the_shape_TranSmart_expects()
    {
        var (translator, handler) = Open(_ => FreeEngineWeb.TencentTranslation("I'm leaving now."));

        var translated = await translator.TranslateAsync(
            "我先撤了哈", Lang("Chinese"), Lang("English"), CancellationToken.None);

        Assert.Equal("I'm leaving now.", translated);

        var (message, _) = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal(new Uri("https://transmart.qq.com/api/imt"), message.RequestUri);

        var sent = Sent(handler);
        Assert.Equal("auto_translation", (string?)sent["header"]!["fn"]);
        Assert.Equal("plain", (string?)sent["type"]);
        Assert.Equal("normal", (string?)sent["model_category"]);
        Assert.Equal("zh", (string?)sent["source"]!["lang"]);
        Assert.Equal("en", (string?)sent["target"]!["lang"]);
        Assert.Equal(["我先撤了哈"], sent["source"]!["text_list"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task An_unspecified_source_is_sent_as_auto()
    {
        // 探针实测：TranSmart 接受 auto。不猜文字系统，也就不会把法语当英语送。
        var (translator, handler) = Open(_ => FreeEngineWeb.TencentTranslation("Hallo"));

        await translator.TranslateAsync("Bonjour", null, Lang("German"), CancellationToken.None);

        Assert.Equal("auto", (string?)Sent(handler)["source"]!["lang"]);
        Assert.Equal("de", (string?)Sent(handler)["target"]!["lang"]);
    }

    [Theory]
    [InlineData("Chinese", "zh")]
    [InlineData("English", "en")]
    [InlineData("Japanese", "ja")]
    [InlineData("French", "fr")]
    public async Task A_declared_source_travels_as_its_tencent_code(string source, string code)
    {
        var (translator, handler) = Open(_ => FreeEngineWeb.TencentTranslation("x"));

        await translator.TranslateAsync("text", Lang(source), Lang("Spanish"), CancellationToken.None);

        Assert.Equal(code, (string?)Sent(handler)["source"]!["lang"]);
        Assert.Equal("es", (string?)Sent(handler)["target"]!["lang"]);
    }

    [Fact]
    public async Task Lines_are_split_on_newlines_and_the_answer_is_joined_back_with_them()
    {
        var (translator, handler) = Open(request => FreeEngineWeb.TencentTranslation(
            request["source"]!["text_list"]!.AsArray().Select(node => ((string?)node)!.ToUpperInvariant()).ToArray()));

        var translated = await translator.TranslateAsync(
            "first\nsecond\nthird", null, Lang("English"), CancellationToken.None);

        Assert.Equal(
            ["first", "second", "third"],
            Sent(handler)["source"]!["text_list"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal("FIRST\nSECOND\nTHIRD", translated);
    }

    [Fact]
    public async Task Blank_lines_keep_their_place()
    {
        // 探针实测：text_list 含空串时返回的数组长度不变、空串原位。段落间的空行
        // 因此原样往返，不必本地剔除再回填。
        var (translator, handler) = Open(request => FreeEngineWeb.TencentTranslation(
            request["source"]!["text_list"]!.AsArray().Select(node => ((string?)node)!.ToUpperInvariant()).ToArray()));

        var translated = await translator.TranslateAsync(
            "para one\n\npara two", null, Lang("English"), CancellationToken.None);

        Assert.Equal(
            ["para one", "", "para two"],
            Sent(handler)["source"]!["text_list"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal("PARA ONE\n\nPARA TWO", translated);
    }

    [Fact]
    public async Task Windows_line_endings_are_normalised_before_splitting()
    {
        var (translator, handler) = Open(_ => FreeEngineWeb.TencentTranslation("a", "b"));

        await translator.TranslateAsync("one\r\ntwo", null, Lang("English"), CancellationToken.None);

        Assert.Equal(
            ["one", "two"],
            Sent(handler)["source"]!["text_list"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task The_client_key_has_the_browser_shape_and_is_made_once_per_process()
    {
        var (first, handlerA) = Open(_ => FreeEngineWeb.TencentTranslation("x"));
        var (second, handlerB) = Open(_ => FreeEngineWeb.TencentTranslation("x"));

        await first.TranslateAsync("a", null, Lang("English"), CancellationToken.None);
        await second.TranslateAsync("b", null, Lang("English"), CancellationToken.None);

        var keyA = (string?)Sent(handlerA)["header"]!["client_key"];
        var keyB = (string?)Sent(handlerB)["header"]!["client_key"];

        // browser-chrome-130.0.0-Windows_10-{8 位随机}-{毫秒时间戳}
        Assert.Matches(new Regex(@"^browser-chrome-130\.0\.0-Windows_10-[a-z0-9]{8}-\d{13}$"), keyA);
        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public async Task A_different_number_of_lines_back_is_joined_as_it_comes()
    {
        // 行数对不上不是失败：译文照样是译文，不值得为它整块作废。
        var (translator, _) = Open(_ => FreeEngineWeb.TencentTranslation("only one"));

        var translated = await translator.TranslateAsync(
            "a\nb\nc", null, Lang("English"), CancellationToken.None);

        Assert.Equal("only one", translated);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Http_errors_are_failures(HttpStatusCode status)
    {
        var (translator, handler) = Open(_ => FreeEngineWeb.Json(string.Empty, status));

        await Assert.ThrowsAsync<FreeEngineException>(() => translator.TranslateAsync(
            "text", null, Lang("English"), CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"header":{"ret_code":"error"}}""")]
    [InlineData("""{"auto_translation":"not an array"}""")]
    [InlineData("""{"auto_translation":[1,2]}""")]
    [InlineData("""{"auto_translation":[]}""")]
    public async Task A_body_of_the_wrong_shape_is_a_failure(string body)
    {
        var (translator, _) = Open(_ => FreeEngineWeb.Json(body));

        await Assert.ThrowsAsync<FreeEngineException>(() => translator.TranslateAsync(
            "text", null, Lang("English"), CancellationToken.None));
    }
}
