using System.Net;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 必应翻译页面的取值（票 41）。夹具是 2026-10-04 真实页面裁出的片段：
/// 毫秒 TTL 的防滥用令牌、IG、三个 data-iid（取第一个）、以及脚本里会误导
/// 正则的 getAttribute("data-iid") 字样。
/// </summary>
public class BingPageParsingTests
{
    [Fact]
    public void The_page_yields_key_token_ttl_ig_and_the_first_iid()
    {
        var values = BingPage.Parse(FreeEngineWeb.BingPageHtml);

        Assert.NotNull(values);
        Assert.Equal("1700000000000", values!.Key);
        Assert.Equal("FAKE-TOKEN-bing-fixture-0123456789ab", values.Token);
        Assert.Equal("00000000AAAAAAAA1111111122222222", values.IG);

        // 页面里不止一个 data-iid；翻译请求认的是第一个（rich_tta 容器上那个）。
        Assert.True(FreeEngineWeb.BingPageHtml.Split("data-iid=\"").Length - 1 >= 3);
        Assert.Equal("translator.5023", values.IID);
    }

    [Fact]
    public void The_ttl_is_read_as_milliseconds()
        => Assert.Equal(3_600_000, BingPage.Parse(FreeEngineWeb.BingPageHtml)!.TtlMs);

    [Theory]
    [InlineData("params_AbusePreventionHelper = [", "params_AbusePreventionHelpe = [")]
    [InlineData("IG:\"", "IG=\"")]
    [InlineData("data-iid=\"", "data-xid=\"")]
    public void A_page_that_lost_any_of_the_three_values_is_refused(string original, string mangled)
    {
        // 页面形状变了就是形状变了：缺哪一项都不凑合，由上层改走腾讯。
        var broken = FreeEngineWeb.BingPageHtml.Replace(original, mangled);

        Assert.NotEqual(FreeEngineWeb.BingPageHtml, broken);
        Assert.Null(BingPage.Parse(broken));
    }

    [Theory]
    [InlineData("https://cn.bing.com/translator", "https://cn.bing.com")]
    [InlineData("https://www.bing.com/translator", "https://www.bing.com")]
    [InlineData("https://sg.bing.com/translator?x=1", "https://sg.bing.com")]
    [InlineData("https://bing.com/translator", "https://bing.com")]
    public void The_translate_host_is_the_host_the_redirect_ended_on(string finalUrl, string expectedBase)
        => Assert.Equal(expectedBase, BingPage.TranslateBaseFor(new Uri(finalUrl)));

    [Theory]
    [InlineData("https://captive.portal.example/login")]
    [InlineData("https://bing.com.evil.example/translator")]
    [InlineData("https://notbing.com/translator")]
    [InlineData("http://www.bing.com/translator")]
    public void A_host_outside_bing_falls_back_to_cn(string finalUrl)
        => Assert.Equal("https://cn.bing.com", BingPage.TranslateBaseFor(new Uri(finalUrl)));

    [Fact]
    public void Without_a_final_address_the_fallback_is_cn()
        => Assert.Equal("https://cn.bing.com", BingPage.TranslateBaseFor(null));
}

/// <summary>
/// 会话缓存（票 41）：放在后端实例之外的进程级持有者——BuildTranslationBackend
/// 每次翻译都新建后端，缓存挂在实例上就等于每次都重新下载 620KB 页面。
/// </summary>
public class BingSessionCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static (BingSessionCache Cache, ScriptedHandler Handler, TestClock Clock) Open(
        Func<int, HttpResponseMessage>? respond = null)
    {
        var clock = new TestClock(Start);
        var handler = new ScriptedHandler(respond ?? (_ => FreeEngineWeb.BingPage()));
        return (new BingSessionCache(new HttpClient(handler), clock), handler, clock);
    }

    [Fact]
    public async Task The_session_carries_the_parsed_values_and_its_expiry()
    {
        var (cache, _, _) = Open();

        var session = await cache.GetAsync();

        Assert.Equal("FAKE-TOKEN-bing-fixture-0123456789ab", session.Token);
        Assert.Equal("00000000AAAAAAAA1111111122222222", session.IG);
        Assert.Equal("translator.5023", session.IID);
        Assert.Equal("https://cn.bing.com", session.TranslateBase);

        // exp = 现在 + max(ttl − 60s, 0)；ttl 是毫秒，三千六百秒的那个一小时。
        Assert.Equal(Start + TimeSpan.FromMilliseconds(3_600_000 - 60_000), session.ExpiresAt);
    }

    [Fact]
    public async Task A_second_call_reuses_the_cached_session()
    {
        var (cache, handler, _) = Open();

        var first = await cache.GetAsync();
        var second = await cache.GetAsync();

        Assert.Same(first, second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reuse_stops_fifteen_seconds_before_the_expiry()
    {
        var (cache, handler, clock) = Open();
        await cache.GetAsync();

        // exp = Start + 3540s。仅当 exp > now + 15s 才复用：3524s 时还剩 16s，复用。
        clock.Advance(TimeSpan.FromSeconds(3524));
        await cache.GetAsync();
        Assert.Single(handler.Requests);

        // 3525s 时刚好剩 15s，不再够用（严格大于），提前刷新。
        clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetAsync();
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_ttl_shorter_than_the_safety_margin_is_never_reused()
    {
        // ttl 三十秒：减去一分钟的安全边距后是零（不是负数），exp = now，永不满足 exp > now + 15s。
        var shortLived = FreeEngineWeb.BingPageHtml.Replace(",3600000]", ",30000]");
        var (cache, handler, _) = Open(_ => FreeEngineWeb.BingPage(html: shortLived));

        await cache.GetAsync();
        await cache.GetAsync();

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_page_fetch()
    {
        var gated = new GatedPageHandler();
        var cache = new BingSessionCache(new HttpClient(gated), new TestClock(Start));

        // 八路同时要会话；闸门没开，在途请求恰好一个。
        var callers = Enumerable.Range(0, 8).Select(_ => cache.GetAsync()).ToList();
        Assert.Equal(1, gated.Count);

        gated.Open();
        var sessions = await Task.WhenAll(callers);

        Assert.Equal(1, gated.Count);
        Assert.All(sessions, session => Assert.Same(sessions[0], session));
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_fetch_the_others_wait_on()
    {
        var gated = new GatedPageHandler();
        var cache = new BingSessionCache(new HttpClient(gated), new TestClock(Start));
        using var impatient = new CancellationTokenSource();

        var leaving = cache.GetAsync(cancellation: impatient.Token);
        var staying = cache.GetAsync();
        await impatient.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

        gated.Open();
        var session = await staying;

        Assert.NotNull(session);
        Assert.Equal(1, gated.Count);
    }

    [Fact]
    public async Task A_failed_fetch_is_not_cached_and_the_next_caller_tries_again()
    {
        var (cache, handler, _) = Open(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : FreeEngineWeb.BingPage());

        await Assert.ThrowsAsync<FreeEngineException>(() => cache.GetAsync());
        var session = await cache.GetAsync();

        Assert.NotNull(session);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_page_without_the_expected_values_is_a_failure()
    {
        var (cache, _, _) = Open(_ => FreeEngineWeb.BingPage(html: "<html>maintenance</html>"));

        await Assert.ThrowsAsync<FreeEngineException>(() => cache.GetAsync());
    }

    [Fact]
    public async Task A_page_fetch_that_never_answers_times_out()
    {
        var hanging = new HangingHandler();
        var cache = new BingSessionCache(
            new HttpClient(hanging), new TestClock(Start), fetchTimeout: TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAsync<TimeoutException>(() => cache.GetAsync());
    }

    [Fact]
    public async Task The_translate_host_follows_the_redirect()
    {
        var (cache, _, _) = Open(_ => FreeEngineWeb.BingPage("https://www.bing.com/translator"));

        var session = await cache.GetAsync();

        Assert.Equal("https://www.bing.com", session.TranslateBase);
    }

    [Fact]
    public async Task A_forced_refresh_replaces_the_stale_session()
    {
        var pages = 0;
        var (cache, handler, _) = Open(_ => FreeEngineWeb.BingPage(
            html: FreeEngineWeb.BingPageHtml.Replace("FAKE-TOKEN-bing-fixture-0123456789ab", $"TOKEN-{++pages}")));

        var stale = await cache.GetAsync();
        var fresh = await cache.GetAsync(stale);

        Assert.NotSame(stale, fresh);
        Assert.Equal("TOKEN-2", fresh.Token);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_late_forced_refresh_for_an_already_replaced_session_does_not_fetch_again()
    {
        // 两路请求同时拿到 205：先到的刷新了会话，后到的手里还是旧会话——
        // 它该直接用新的，而不是再下载一遍页面。
        var pages = 0;
        var (cache, handler, _) = Open(_ => FreeEngineWeb.BingPage(
            html: FreeEngineWeb.BingPageHtml.Replace("FAKE-TOKEN-bing-fixture-0123456789ab", $"TOKEN-{++pages}")));

        var stale = await cache.GetAsync();
        var refreshed = await cache.GetAsync(stale);
        var late = await cache.GetAsync(stale);

        Assert.Same(refreshed, late);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task NeedsFetch_is_true_when_missing_and_again_near_the_expiry()
    {
        var (cache, _, clock) = Open();
        Assert.True(cache.NeedsFetch);

        await cache.GetAsync();
        Assert.False(cache.NeedsFetch);

        clock.Advance(TimeSpan.FromSeconds(3525));
        Assert.True(cache.NeedsFetch);
    }
}

/// <summary>必应翻译器（票 41）：请求形状、成功解析、205/401 强刷恰好一次。</summary>
public class BingWebTranslatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static FreeEngineLanguage Lang(string name) => FreeEngineLanguages.Find(name)!;

    private static BingWebTranslator Translator(ScriptedHandler handler)
    {
        var http = new HttpClient(handler);
        return new BingWebTranslator(http, new BingSessionCache(http, new TestClock(Start)));
    }

    private static IEnumerable<(HttpRequestMessage Message, string? Body)> Posts(ScriptedHandler handler)
        => handler.Requests.Where(request => FreeEngineWeb.IsBingPost(request.Message));

    private static IEnumerable<(HttpRequestMessage Message, string? Body)> Pages(ScriptedHandler handler)
        => handler.Requests.Where(request => FreeEngineWeb.IsPage(request.Message));

    [Fact]
    public async Task The_request_is_the_form_the_page_expects()
    {
        var handler = FreeEngineWeb.BingSite(_ => "I'm heading out for now.");
        var translator = Translator(handler);

        var translated = await translator.TranslateAsync(
            "我先撤了哈，明天见", null, Lang("English"), CancellationToken.None);

        Assert.Equal("I'm heading out for now.", translated);

        var (message, body) = Posts(handler).Single();
        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal("https", message.RequestUri!.Scheme);
        Assert.Equal("cn.bing.com", message.RequestUri.Host);
        Assert.Equal("/ttranslatev3", message.RequestUri.AbsolutePath);
        Assert.Equal(
            "?isVertical=1&IG=00000000AAAAAAAA1111111122222222&IID=translator.5023",
            message.RequestUri.Query);

        var form = FreeEngineWeb.Form(body!);
        Assert.Equal(["fromLang", "key", "text", "to", "token"], form.Keys.Order().ToArray());
        Assert.Equal("auto-detect", form["fromLang"]);
        Assert.Equal("en", form["to"]);
        Assert.Equal("我先撤了哈，明天见", form["text"]);
        Assert.Equal("FAKE-TOKEN-bing-fixture-0123456789ab", form["token"]);
        Assert.Equal("1700000000000", form["key"]);
    }

    [Fact]
    public async Task A_declared_source_travels_as_its_bing_code()
    {
        var handler = FreeEngineWeb.BingSite(_ => "你好");
        var translator = Translator(handler);

        await translator.TranslateAsync("hello", Lang("English"), Lang("Chinese"), CancellationToken.None);

        var form = FreeEngineWeb.Form(Posts(handler).Single().Body!);
        Assert.Equal("en", form["fromLang"]);
        Assert.Equal("zh-Hans", form["to"]);
    }

    [Fact]
    public async Task The_session_is_fetched_once_for_many_translations()
    {
        var handler = FreeEngineWeb.BingSite(form => form["text"]);
        var translator = Translator(handler);

        await translator.TranslateAsync("one", null, Lang("Chinese"), CancellationToken.None);
        await translator.TranslateAsync("two", null, Lang("Chinese"), CancellationToken.None);
        await translator.TranslateAsync("three", null, Lang("Chinese"), CancellationToken.None);

        Assert.Single(Pages(handler));
        Assert.Equal(3, Posts(handler).Count());
    }

    [Fact]
    public async Task The_translate_address_uses_the_host_after_the_redirect()
    {
        var handler = FreeEngineWeb.Routed((message, _, _) => FreeEngineWeb.IsPage(message)
            ? FreeEngineWeb.BingPage("https://www.bing.com/translator")
            : FreeEngineWeb.BingTranslation("ok"));

        await Translator(handler).TranslateAsync("你好", null, Lang("English"), CancellationToken.None);

        Assert.Equal("www.bing.com", Posts(handler).Single().Message.RequestUri!.Host);
    }

    [Fact]
    public async Task A_host_outside_bing_never_receives_the_translation_request()
    {
        // 被劫持的页面（强制门户之类）跳到别的主机：翻译请求绝不跟过去。
        var handler = FreeEngineWeb.Routed((message, _, _) => FreeEngineWeb.IsPage(message)
            ? FreeEngineWeb.BingPage("https://captive.portal.example/login")
            : FreeEngineWeb.BingTranslation("ok"));

        await Translator(handler).TranslateAsync("你好", null, Lang("English"), CancellationToken.None);

        Assert.Equal("cn.bing.com", Posts(handler).Single().Message.RequestUri!.Host);
    }

    [Fact]
    public async Task A_205_in_the_body_refreshes_the_session_and_retries_exactly_once()
    {
        // 实测：令牌失效时 HTTP 仍是 200，205 在响应体里。
        var pages = 0;
        var posts = 0;
        var handler = FreeEngineWeb.Routed((message, _, _) =>
        {
            if (FreeEngineWeb.IsPage(message))
            {
                return FreeEngineWeb.BingPage(
                    html: FreeEngineWeb.BingPageHtml.Replace("FAKE-TOKEN-bing-fixture-0123456789ab", $"TOKEN-{++pages}"));
            }

            return ++posts == 1 ? FreeEngineWeb.BingStatus(205) : FreeEngineWeb.BingTranslation("hello");
        });

        var translated = await Translator(handler)
            .TranslateAsync("你好", null, Lang("English"), CancellationToken.None);

        Assert.Equal("hello", translated);
        Assert.Equal(2, Pages(handler).Count());
        var forms = Posts(handler).Select(post => FreeEngineWeb.Form(post.Body!)).ToList();
        Assert.Equal(2, forms.Count);
        Assert.Equal("TOKEN-1", forms[0]["token"]);
        Assert.Equal("TOKEN-2", forms[1]["token"]);
    }

    [Fact]
    public async Task A_401_status_refreshes_the_session_and_retries_exactly_once()
    {
        var posts = 0;
        var handler = FreeEngineWeb.Routed((message, _, _) => FreeEngineWeb.IsPage(message)
            ? FreeEngineWeb.BingPage()
            : ++posts == 1
                ? FreeEngineWeb.Json(string.Empty, HttpStatusCode.Unauthorized)
                : FreeEngineWeb.BingTranslation("hello"));

        var translated = await Translator(handler)
            .TranslateAsync("你好", null, Lang("English"), CancellationToken.None);

        Assert.Equal("hello", translated);
        Assert.Equal(2, Pages(handler).Count());
        Assert.Equal(2, Posts(handler).Count());
    }

    [Fact]
    public async Task A_second_205_is_an_error_and_never_a_loop()
    {
        var handler = FreeEngineWeb.Routed((message, _, _) => FreeEngineWeb.IsPage(message)
            ? FreeEngineWeb.BingPage()
            : FreeEngineWeb.BingStatus(205));

        await Assert.ThrowsAsync<FreeEngineException>(() => Translator(handler)
            .TranslateAsync("你好", null, Lang("English"), CancellationToken.None));

        // 强刷一次、重试一次，到此为止：两次页面、两次翻译请求。
        Assert.Equal(2, Pages(handler).Count());
        Assert.Equal(2, Posts(handler).Count());
    }

    [Fact]
    public async Task Another_status_in_the_body_is_a_failure_without_a_retry()
    {
        var handler = FreeEngineWeb.Routed((message, _, _) => FreeEngineWeb.IsPage(message)
            ? FreeEngineWeb.BingPage()
            : FreeEngineWeb.BingStatus(400));

        await Assert.ThrowsAsync<FreeEngineException>(() => Translator(handler)
            .TranslateAsync("你好", null, Lang("English"), CancellationToken.None));

        Assert.Single(Pages(handler));
        Assert.Single(Posts(handler));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Http_errors_are_failures_without_a_retry(HttpStatusCode status)
    {
        var handler = FreeEngineWeb.Routed((message, _, _) => FreeEngineWeb.IsPage(message)
            ? FreeEngineWeb.BingPage()
            : FreeEngineWeb.Json(string.Empty, status));

        await Assert.ThrowsAsync<FreeEngineException>(() => Translator(handler)
            .TranslateAsync("你好", null, Lang("English"), CancellationToken.None));

        Assert.Single(Posts(handler));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""[{"translations":[]}]""")]
    [InlineData("""{"unexpected":"object"}""")]
    public async Task A_body_of_the_wrong_shape_is_a_failure(string body)
    {
        var handler = FreeEngineWeb.Routed((message, _, _) => FreeEngineWeb.IsPage(message)
            ? FreeEngineWeb.BingPage()
            : FreeEngineWeb.Json(body));

        await Assert.ThrowsAsync<FreeEngineException>(() => Translator(handler)
            .TranslateAsync("你好", null, Lang("English"), CancellationToken.None));
    }

    [Fact]
    public async Task A_dead_network_surfaces_as_an_exception_for_the_backend_to_judge()
    {
        var handler = FreeEngineWeb.Routed((_, _, _) => throw new HttpRequestException("name resolution failed"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Translator(handler)
            .TranslateAsync("你好", null, Lang("English"), CancellationToken.None));
    }
}
