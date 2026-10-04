namespace Shiyu.Core;

/// <summary>一次运行的结局：带着序号，交回会话时凭它认出自己是不是已经过期。</summary>
/// <param name="State">Finished / Failed / Cancelled；运行结束了就不会是 Streaming。</param>
/// <param name="Text">清洗过的文字（按请求的模板类别，见 <see cref="TranslationCleanup"/>）。</param>
public readonly record struct ReverseOutcome(
    int Seq, TranslationState State, string Text, Exception? Failure, string? Error);

/// <summary>
/// 把会话开出的一次请求（<see cref="ReverseRun"/>）跑成 <see cref="TranslationSession"/>，再把结局
/// 按序号交回会话（票 43）。薄薄一层胶水，但"取消不当成失败、过期结果不上屏、清洗按模板类别"
/// 这几条都从这里过，所以放在 Core 里用脚本后端测——界面只剩把回调转到 UI 线程。
///
/// 回声换向重试（票 34）照 <see cref="TranslationSession"/> 的规矩：只对翻译类、且请求没有声明
/// 源语言时才有；反向输入框的请求总是声明了源语言（方向是用户选的或按规则定的），改写类根本不重试，
/// 所以这里不会出现"悄悄被反过来"。
/// </summary>
public static class ReverseInputRunner
{
    /// <param name="onPartial">
    /// 流式中间结果（序号, 清洗后的文字）。只在有字的时候回调，且可能不在 UI 线程上——调用方自己转线程。
    /// </param>
    public static async Task<ReverseOutcome> RunAsync(
        ReverseRun run,
        ITranslationBackend backend,
        Action<int, string>? onPartial,
        CancellationToken cancellation)
    {
        var translation = new TranslationSession(backend);

        translation.Updated += () =>
        {
            // 开头与结尾各有一次 Updated（状态变化）；中间结果只在流式且已经有字时才报。
            if (translation.State == TranslationState.Streaming && translation.Text.Length > 0)
            {
                onPartial?.Invoke(run.Seq, translation.Text);
            }
        };

        await translation.RunAsync(run.Request, cancellation);

        return new ReverseOutcome(
            run.Seq, translation.State, translation.Text, translation.Failure, translation.Error);
    }

    /// <summary>
    /// 把结局交给会话：完成 → <see cref="ReverseInputSession.Completed"/>（译文一到、Enter 已按过就回应"贴回"），
    /// 失败 → <see cref="ReverseInputSession.Failed"/>，取消 → 什么都不交（作废是会话自己在输入变化时做的）。
    /// </summary>
    public static ReverseInputStep Deliver(ReverseInputSession session, ReverseOutcome outcome)
        => outcome.State switch
        {
            TranslationState.Finished => session.Completed(outcome.Seq, outcome.Text),
            TranslationState.Failed => session.Failed(outcome.Seq, outcome.Failure, outcome.Error),
            _ => default,
        };
}
