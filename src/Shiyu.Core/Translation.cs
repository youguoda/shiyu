namespace Shiyu.Core;

public sealed record TranslationRequest(string Text, string TargetLanguage)
{
    /// <summary>Null asks the backend to work it out from the text.</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>
    /// 提示词模板（票 42），默认是内置的标准模板。传的是解析好的模板对象而不是
    /// id，Core 不必回头去读设置；回声换向重试用 <c>with</c> 复制请求，模板随之保留。
    /// </summary>
    public PromptTemplate Template { get; init; } = PromptTemplates.Standard;

    private double? _temperature;

    /// <summary>
    /// 采样温度。调用方没有指定时跟随模板（标准与正式 <see cref="TranslationPrompt.DefaultTemperature"/>，
    /// 口语与改写类 0.3）；指定了就是调用方的覆盖，模板不再插手。
    /// </summary>
    public double Temperature
    {
        get => _temperature ?? Template.Temperature;
        init => _temperature = value;
    }
}

/// <summary>
/// A translation backend.
///
/// Streaming from the very first version, deliberately: retrofitting a stream
/// into a blocking interface means changing every caller, every view and every
/// test at once. The user should be reading while the rest is still arriving.
/// </summary>
public interface ITranslationBackend
{
    /// <summary>
    /// Yields the translation in pieces as they arrive. Implementations throw
    /// <see cref="TranslationFailedException"/> for anything the user needs to
    /// be told about.
    /// </summary>
    IAsyncEnumerable<string> TranslateAsync(TranslationRequest request, CancellationToken cancellation);
}

public sealed class TranslationFailedException(string message, Exception? inner = null)
    : Exception(message, inner);
