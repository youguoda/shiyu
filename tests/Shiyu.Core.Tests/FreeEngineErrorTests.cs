using System.Reflection;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 免费引擎文案与 <see cref="TranslationUserErrorMapper"/> 的配合（票 41）。
/// 分类器按子串判类别，Auth 类既不给「重试」、还会叫用户去检查 API 密钥；
/// 「改用自备密钥」这类说法含"密钥"二字，会被误判。所以以「免费引擎」开头的
/// 消息归 Other、保留原措辞、保留「重试」——每条文案都在这里过一遍。
/// </summary>
public class FreeEngineErrorTests
{
    /// <summary>FreeEngineMessages 里每一条字面量文案，反射取全，新增一条自动进来。</summary>
    public static TheoryData<string> Sentences
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var field in typeof(FreeEngineMessages)
                         .GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(field => field.IsLiteral && field.FieldType == typeof(string)))
            {
                data.Add((string)field.GetRawConstantValue()!);
            }

            data.Add(FreeEngineMessages.UnsupportedLanguage("Klingon"));
            data.Add(FreeEngineMessages.UnsupportedLanguage("Brazilian Portuguese"));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Sentences))]
    public void Every_free_engine_sentence_is_classified_other_and_keeps_its_words(string message)
    {
        var error = TranslationUserErrorMapper.Describe(new TranslationFailedException(message), message);

        Assert.Equal(TranslationErrorKind.Other, error.Kind);
        Assert.Equal("翻译失败", error.Title);
        Assert.Equal(message, error.Detail);

        // 保留「重试」；不叫人去检查 API 密钥，也不开「服务设置」。
        Assert.True(error.ShowRetry);
        Assert.False(error.ShowSettings);
    }

    [Theory]
    [MemberData(nameof(Sentences))]
    public void Every_free_engine_sentence_starts_with_the_prefix_the_classifier_keys_on(string message)
        => Assert.StartsWith(FreeEngineMessages.Prefix, message);

    [Fact]
    public void The_catalogue_is_not_empty()
        => Assert.True(Sentences.Cast<object[]>().Count() >= 4, "reflection found too few sentences — did the class change shape?");

    [Theory]
    [InlineData("免费引擎请求超时，请稍后再试")]
    [InlineData("免费引擎返回 429，请求过于频繁")]
    [InlineData("免费引擎提示 401：请检查密钥与凭据")]
    [InlineData("免费引擎连接翻译服务失败")]
    public void The_prefix_rule_comes_before_the_timeout_rate_limit_auth_and_network_rules(string message)
    {
        // 这些子串在别的消息里分别会判成超时、限流、Auth、网络——前缀规则排在它们前面。
        var error = TranslationUserErrorMapper.Describe(new TranslationFailedException(message), message);

        Assert.Equal(TranslationErrorKind.Other, error.Kind);
        Assert.True(error.ShowRetry);
        Assert.Equal(message, error.Detail);
    }

    [Fact]
    public void A_tls_failure_still_wins_over_the_prefix()
    {
        // Security 仍然最先判：两家都被抓包工具挡住时，用户该看到「无法建立安全连接」。
        var inner = new HttpRequestException(
            "boom",
            new System.Security.Authentication.AuthenticationException("The SSL connection could not be established."));
        var failure = new TranslationFailedException(FreeEngineMessages.Unavailable, inner);

        var error = TranslationUserErrorMapper.Describe(failure, failure.Message);

        Assert.Equal(TranslationErrorKind.Security, error.Kind);
    }

    [Fact]
    public void The_inner_exceptions_ride_along_in_the_raw_details()
    {
        var failure = new TranslationFailedException(
            FreeEngineMessages.Unavailable,
            new AggregateException(new FreeEngineException("bing down"), new FreeEngineException("tencent down")));

        var error = TranslationUserErrorMapper.Describe(failure, failure.Message);

        Assert.Contains("TranslationFailedException", error.Raw);
        Assert.Contains("bing down", error.Raw);
    }

    [Fact]
    public void Other_key_messages_are_still_credential_errors()
    {
        // 回归：加前缀规则不能放过真正的凭据类错误。
        var message = "翻译失败：凭据无效或已过期，请检查设置里的接口密钥。";

        var error = TranslationUserErrorMapper.Describe(new TranslationFailedException(message), message);

        Assert.Equal(TranslationErrorKind.Auth, error.Kind);
        Assert.False(error.ShowRetry);
    }

    [Fact]
    public void The_two_numbers_in_the_sentences_match_the_constants()
    {
        // 文案里写的 5000 字必须就是后端真正的上限。
        Assert.Contains(FreeEngineBackend.MaxTextChars.ToString(), FreeEngineMessages.TooLong);
    }
}
