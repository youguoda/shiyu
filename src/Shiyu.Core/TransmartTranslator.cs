using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiyu.Core;

/// <summary>
/// 腾讯交互翻译（TranSmart）网页接口的整段翻译器（票 41）。POST
/// <c>https://transmart.qq.com/api/imt</c>，JSON 体：
/// <c>{header:{fn:"auto_translation", client_key}, type:"plain", model_category:"normal",
/// source:{lang, text_list}, target:{lang}}</c>。
///
/// 按行切分：<c>text_list</c> 是逐行的数组，返回的 <c>auto_translation[]</c> 用 <c>\n</c> 拼回。
/// 2026-10-04 的探针直连实测给出两条结论，这里照它们实现：
/// <list type="bullet">
/// <item><b>空行</b>：text_list 里的空串原位返回、数组长度不变——空行直接发，
/// 不必本地剔除再回填。</item>
/// <item><b>源语言</b>：<c>source.lang</c> 接受 <c>"auto"</c>。用户没指定源语言就发 auto，
/// 所以没有"按文字系统推断源语言"那一支，也就没有"拉丁文字一律当英文"的已知局限。</item>
/// </list>
/// 这两条若哪天变了，探针会先红（见 tools/probes/probe-free-engines.ps1）。
/// </summary>
internal sealed class TransmartTranslator(HttpClient http) : IWholeTextTranslator
{
    private const string Endpoint = "https://transmart.qq.com/api/imt";

    /// <summary>每个进程生成一次：<c>browser-chrome-130.0.0-Windows_10-{8 位随机}-{毫秒时间戳}</c>。</summary>
    internal static string ClientKey { get; } = MakeClientKey();

    public async Task<string> TranslateAsync(
        string text, FreeEngineLanguage? from, FreeEngineLanguage to, CancellationToken cancellation)
    {
        var lines = new JsonArray();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            lines.Add(line);
        }

        var body = new JsonObject
        {
            ["header"] = new JsonObject { ["fn"] = "auto_translation", ["client_key"] = ClientKey },
            ["type"] = "plain",
            ["model_category"] = "normal",
            ["source"] = new JsonObject { ["lang"] = from?.Tencent ?? "auto", ["text_list"] = lines },
            ["target"] = new JsonObject { ["lang"] = to.Tencent },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // 伪造的 UA/Referer 只在这条请求上，不进共享客户端的默认头。
        message.Headers.UserAgent.ParseAdd(WebEngineHeaders.ChromeUserAgent);
        message.Headers.Referrer = new Uri("https://transmart.qq.com/");

        using var response = await http.SendAsync(message, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw new FreeEngineException($"腾讯返回 HTTP {(int)response.StatusCode}");
        }

        return Interpret(await response.Content.ReadAsStringAsync(cancellation));
    }

    /// <summary>
    /// 成功的标志是有 <c>auto_translation</c> 数组且都是字符串；错误应答只有 header
    /// （<c>ret_code</c> 非成功），没有这个数组。行数对不上不算失败——译文照样是译文。
    /// </summary>
    private static string Interpret(string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["auto_translation"] is JsonArray { Count: > 0 } list)
            {
                return string.Join("\n", list.Select(item => item?.GetValue<string>()
                    ?? throw new FreeEngineException("腾讯的应答里有空行项")));
            }
        }
        catch (JsonException unreadable)
        {
            throw new FreeEngineException("腾讯的应答不是 JSON", unreadable);
        }
        catch (InvalidOperationException shape)
        {
            throw new FreeEngineException("腾讯的应答形状不对", shape);
        }
        catch (FormatException shape)
        {
            throw new FreeEngineException("腾讯的应答形状不对", shape);
        }

        throw new FreeEngineException("腾讯的应答里没有 auto_translation");
    }

    private static string MakeClientKey()
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var random = string.Create(8, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
            }
        });

        return $"browser-chrome-130.0.0-Windows_10-{random}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }
}
