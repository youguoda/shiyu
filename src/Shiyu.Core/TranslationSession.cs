using System.Runtime.CompilerServices;

namespace Shiyu.Core;

public enum TranslationState
{
    Idle,
    Streaming,
    Finished,

    /// <summary>Stopped early. Whatever arrived before it stopped is kept.</summary>
    Failed,

    Cancelled,
}

/// <summary>
/// Drives one translation and holds what has arrived so far.
///
/// The partial text is kept on every ending, including failure: a stream that
/// dies two thirds of the way through has still given the user two thirds of
/// what they wanted, and throwing that away to show an error box instead would
/// be a second failure on top of the first.
///
/// 可以在后台线程上跑（用户实录 2026-10-15：LLM 出字时界面卡、"未响应"——原来整条流在 UI 线程上
/// 读、每到一片就当场排版）：片段在锁里追加，<see cref="Text"/> 随时可从别的线程读，读到的是
/// 某一刻完整的一份。
/// </summary>
public sealed class TranslationSession(ITranslationBackend backend)
{
    private readonly object _gate = new();
    private readonly System.Text.StringBuilder _raw = new();

    /// <summary>清洗过的那一份与它对应的原始长度、请求：长度与请求都没变就直接给它，不再拼、不再洗。</summary>
    private string _cleaned = string.Empty;

    private int _cleanedFrom = -1;
    private TranslationRequest? _cleanedFor;

    public TranslationState State { get; private set; } = TranslationState.Idle;

    /// <summary>Set when <see cref="State"/> is <see cref="TranslationState.Failed"/>.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// The failure itself (票 22)：标题/说明的映射按异常链分类，折叠的
    /// 「详细信息」也要它。与 <see cref="Error"/> 同生同灭；Cancelled 不算失败。
    /// </summary>
    public Exception? Failure { get; private set; }

    /// <summary>
    /// What has arrived so far, cleaned of any wrapping the model added. 清洗按屏幕上
    /// 那条请求的模板类别走（票 42）：翻译类全量，改写类只剥首尾的 &lt;text&gt;。
    /// </summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                // 原来每读一次都把所有片段重拼一遍、重洗一遍，界面一次刷新要读好几回，越长越慢。
                if (_cleanedFrom != _raw.Length || !ReferenceEquals(_cleanedFor, CurrentRequest))
                {
                    _cleaned = TranslationCleanup.Clean(
                        _raw.ToString(), CurrentRequest?.Template ?? PromptTemplates.Standard);
                    _cleanedFrom = _raw.Length;
                    _cleanedFor = CurrentRequest;
                }

                return _cleaned;
            }
        }
    }

    /// <summary>
    /// The request behind the text currently on screen. With an echo retry it
    /// differs from the request passed to <see cref="RunAsync"/> — the direction
    /// label needs to see the swap, not the user's original ask.
    /// </summary>
    public TranslationRequest? CurrentRequest { get; private set; }

    /// <summary>Raised whenever <see cref="Text"/> grows, so a view can follow along.</summary>
    public event Action? Updated;

    public async Task RunAsync(TranslationRequest request, CancellationToken cancellation = default)
    {
        await AttemptAsync(request, cancellation);

        // 回声且用户未强制源语言：换向重试恰好一次。重试请求带着声明的
        // 源语言，即使这里判断写错也构不成循环。再回声就如实展示。
        // 只对翻译类模板：改写结果与原文相近是正常的（优化一段本来就写得不错
        // 的提示词），回声换向对它没有意义。
        if (State == TranslationState.Finished
            && request.Template.Kind == PromptTemplateKind.Translate
            && request.SourceLanguage is null
            && TranslationEcho.IsEchoish(request.Text, Text))
        {
            await AttemptAsync(EchoRetryRequest(request), cancellation);
        }
    }

    private async Task AttemptAsync(TranslationRequest request, CancellationToken cancellation)
    {
        lock (_gate)
        {
            _raw.Clear();
            CurrentRequest = request;
        }

        Error = null;
        Failure = null;
        State = TranslationState.Streaming;
        Updated?.Invoke();

        try
        {
            await foreach (var piece in backend.TranslateAsync(request, cancellation))
            {
                if (piece.Length == 0)
                {
                    continue;
                }

                lock (_gate)
                {
                    _raw.Append(piece);
                }

                Updated?.Invoke();
            }

            State = TranslationState.Finished;
        }
        catch (OperationCanceledException)
        {
            State = TranslationState.Cancelled;
        }
        catch (TranslationFailedException failure)
        {
            State = TranslationState.Failed;
            Error = failure.Message;
            Failure = failure;
        }
        catch (Exception unexpected)
        {
            // Anything reaching the panel as an unhandled exception would take
            // the whole application down with it. The user gets a message.
            Log.Event(LogEvent.TranslationFailed, unexpected);
            State = TranslationState.Failed;
            Error = unexpected.Message;
            Failure = unexpected;
        }

        Updated?.Invoke();
    }

    /// <summary>
    /// 换向重试的请求：旧目标成为声明的源语言，新目标取原文的本地先验——
    /// 先验与旧目标同名或不可用时退到 English，再同名退到 Chinese，
    /// 与面板手动换向的缺省一致。"中文原文配中文目标"这一最常见回声
    /// 因此恰好落进 Chinese→English 的有用方向。
    /// </summary>
    private static TranslationRequest EchoRetryRequest(TranslationRequest request)
    {
        var target = LanguageGuess.FromText(request.Text).LanguageName ?? "English";
        if (string.Equals(target, request.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            target = "English";
        }

        if (string.Equals(target, request.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            target = "Chinese";
        }

        return request with
        {
            TargetLanguage = target,
            SourceLanguage = request.TargetLanguage,
        };
    }
}

/// <summary>Adapts a backend that returns whole strings into the streaming shape.</summary>
public sealed class WholeTextBackend(Func<TranslationRequest, CancellationToken, Task<string>> translate)
    : ITranslationBackend
{
    public async IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return await translate(request, cancellation);
    }
}
