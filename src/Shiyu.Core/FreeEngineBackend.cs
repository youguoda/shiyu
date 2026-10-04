using System.Runtime.CompilerServices;

namespace Shiyu.Core;

/// <summary>
/// 免费引擎的用户可见文案（票 41）。每一条都以「免费引擎」开头——
/// <see cref="TranslationUserErrorMapper"/> 据此把它们归 Other、保留原措辞与「重试」，
/// 不让"改用自备密钥"里的"密钥"二字被误判成 Auth。新增文案务必同样开头；
/// 测试按反射遍历这里的每一条常量。
/// </summary>
public static class FreeEngineMessages
{
    /// <summary>分类器认的前缀。</summary>
    public static readonly string Prefix = "免费引擎";

    /// <summary>总长超过 <see cref="FreeEngineBackend.MaxTextChars"/>（有测试钉住两处数字一致）。</summary>
    public const string TooLong = "免费引擎单次最多翻译 5000 字，长文请改用自备密钥。";

    /// <summary>必应与腾讯都没有给出译文。</summary>
    public const string Unavailable = "免费引擎暂时不可用：微软和腾讯都没有响应，稍后再试，或在 设置 → 翻译 换一种翻译方式。";

    /// <summary>语言不在码表里（设置文件被手改过一类的情形）。</summary>
    public static string UnsupportedLanguage(string name)
        => $"免费引擎暂不支持语言「{name}」：请在 设置 → 翻译 里换一个，或改用自备密钥。";
}

/// <summary>
/// "必应不健康"的记录（票 41）：必应失败后记五分钟，期间的新翻译直接走腾讯，
/// 不让每次翻译都先白等一遍超时。进程级（后端每次翻译都新建，记在实例上等于没记）；
/// 时钟可注入，测试用假时钟推进。
/// </summary>
internal sealed class FreeEngineHealth(TimeProvider clock)
{
    internal static readonly TimeSpan BingCooldown = TimeSpan.FromMinutes(5);

    internal static FreeEngineHealth Shared { get; } = new(TimeProvider.System);

    private readonly object _gate = new();
    private DateTimeOffset _bingUnhealthyUntil = DateTimeOffset.MinValue;

    /// <summary>冷却已过（含从未失败）就是健康：到点那一刻起可以再试一次。</summary>
    internal bool BingHealthy
    {
        get
        {
            lock (_gate)
            {
                return clock.GetUtcNow() >= _bingUnhealthyUntil;
            }
        }
    }

    internal void MarkBingUnhealthy()
    {
        lock (_gate)
        {
            _bingUnhealthyUntil = clock.GetUtcNow() + BingCooldown;
        }
    }
}

/// <summary>
/// 免费引擎（票 41、ADR-0013）：零配置的翻译方式——微软必应翻译的网页接口为主，
/// 腾讯交互翻译（TranSmart）自动兜底。不注册、不填密钥、不依赖我们部署任何服务端。
///
/// 它<b>不实现 <see cref="IStreamingModel"/></b>：网页翻译没有 prompt，不是通用模型。
/// 因此 Agent 动作、管理窗批量翻译、LLM 词典永远拿不到它——它们要的是
/// <see cref="IStreamingModel"/>，类型系统挡在门口；<c>BuildStreamingModel</c> 遇到
/// 非流式模型的后端会回落到自备密钥。
///
/// 流程：整段按段落与句末切块（首块小、其后大，见 <see cref="FreeEngineChunker"/>），
/// 逐块顺序翻译，每块的译文一到就经共用的 <see cref="StreamingSlicer"/> 吐出——长文
/// 实测要 9 秒，不渐进输出面板会一直空等。必应在第 k 块失败（网络、超时、解析失败、
/// 强刷后仍是 205、5xx、429）时，本次从第 k 块起全部改走腾讯，中途不再切回，并把必应
/// 记为不健康五分钟。用户取消永远不算失败。
/// </summary>
public sealed class FreeEngineBackend : ITranslationBackend
{
    /// <summary>总长上限：超出时给人话，建议改用自备密钥。</summary>
    public const int MaxTextChars = 5000;

    /// <summary>单块超时。必应实测 1392 字要 9 秒，所以块要切小，而不是把超时调大。</summary>
    internal static readonly TimeSpan DefaultChunkTimeout = TimeSpan.FromSeconds(8);

    private readonly IWholeTextTranslator _bing;
    private readonly IWholeTextTranslator _tencent;
    private readonly FreeEngineHealth _health;
    private readonly TimeSpan _chunkTimeout;
    private readonly Func<TimeSpan> _pieceDelay;
    private readonly int _firstChunkChars;
    private readonly int _chunkChars;

    /// <summary>
    /// 生产用：连接池是进程共用的 <see cref="HttpClients.Shared"/>，会话缓存与健康记录
    /// 也是进程级的——本类每次翻译都会被新建，状态不能放在实例上。
    /// </summary>
    public FreeEngineBackend()
        : this(
            new BingWebTranslator(HttpClients.Shared, BingSessionCache.Shared),
            new TransmartTranslator(HttpClients.Shared),
            FreeEngineHealth.Shared)
    {
    }

    internal FreeEngineBackend(
        IWholeTextTranslator bing,
        IWholeTextTranslator tencent,
        FreeEngineHealth health,
        TimeSpan? chunkTimeout = null,
        Func<TimeSpan>? pieceDelay = null,
        int firstChunkChars = FreeEngineChunker.FirstChunkChars,
        int chunkChars = FreeEngineChunker.ChunkChars)
    {
        _bing = bing;
        _tencent = tencent;
        _health = health;
        _chunkTimeout = chunkTimeout ?? DefaultChunkTimeout;
        _pieceDelay = pieceDelay ?? (() => StreamingSlicer.PieceInterval);
        _firstChunkChars = firstChunkChars;
        _chunkChars = chunkChars;
    }

    public async IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        // 前置检查：注定被拒的请求不值当发出去，也不值当让用户等。
        if (request.Text.Length > MaxTextChars)
        {
            throw new TranslationFailedException(FreeEngineMessages.TooLong);
        }

        var target = Resolve(request.TargetLanguage);
        var source = string.IsNullOrWhiteSpace(request.SourceLanguage) ? null : Resolve(request.SourceLanguage);

        var plan = FreeEngineChunker.Split(request.Text, _firstChunkChars, _chunkChars);
        if (plan.Chunks.Count == 0)
        {
            yield break;
        }

        // 冷却期内的新翻译直接走腾讯。
        var onTencent = !_health.BingHealthy;
        Exception? bingFailure = null;
        var previous = string.Empty;

        for (var index = 0; index < plan.Chunks.Count; index++)
        {
            var chunk = plan.Chunks[index];
            string? translated = null;

            if (!onTencent)
            {
                var attempt = await AttemptAsync(_bing, chunk.Text, source, target, cancellation);
                if (attempt.Text is not null)
                {
                    translated = attempt.Text;
                }
                else
                {
                    // 第 index 块起全部改走腾讯，中途不再切回；必应记五分钟不健康。
                    bingFailure = attempt.Failure;
                    onTencent = true;
                    _health.MarkBingUnhealthy();
                    Log.Event(LogEvent.TranslationFailed, attempt.Failure!, ("engine", 1), ("chunk", index));
                }
            }

            if (translated is null)
            {
                var attempt = await AttemptAsync(_tencent, chunk.Text, source, target, cancellation);
                if (attempt.Text is null)
                {
                    throw new TranslationFailedException(
                        FreeEngineMessages.Unavailable, Combine(bingFailure, attempt.Failure));
                }

                translated = attempt.Text;
            }

            // 块间的分隔：段落换行原样、句间空白按译文的文字系统来；末块带上原文末尾的空白。
            var lead = index == 0
                ? plan.Prefix
                : FreeEngineChunker.Between(plan.Chunks[index - 1].Separator, previous, translated);
            var tail = index == plan.Chunks.Count - 1 ? chunk.Separator : string.Empty;
            previous = translated;

            var pieces = StreamingSlicer.Split(lead + translated + tail);
            for (var piece = 0; piece < pieces.Count; piece++)
            {
                if (piece > 0)
                {
                    await Task.Delay(_pieceDelay(), cancellation);
                }

                yield return pieces[piece];
            }
        }
    }

    /// <summary>
    /// 预热（票 41）：当前翻译方式是免费引擎时，徽标出现或翻译热键按下的那一刻，
    /// 会话缺失或快过期就在后台取一次。不设定时器；失败不提示。调用方在 UI 线程上
    /// 调它也没关系——真正的工作在后台任务里。
    /// </summary>
    public static void WarmUp(AppSettings settings)
        => _ = Task.Run(() => WarmUpAsync(settings, BingSessionCache.Shared, FreeEngineHealth.Shared));

    /// <summary>
    /// 预热的判断与动作：只有选了免费引擎、必应不在冷却、会话缺失或快过期才取；
    /// 其余一律什么都不做——没选免费引擎就不替用户联系微软，哪怕只是取一个页面。
    /// </summary>
    internal static Task WarmUpAsync(AppSettings settings, BingSessionCache sessions, FreeEngineHealth health)
        => settings.TranslationBackend == TranslationBackendKind.Free
            && health.BingHealthy
            && sessions.NeedsFetch
                ? sessions.WarmAsync()
                : Task.CompletedTask;

    private static FreeEngineLanguage Resolve(string name)
        => FreeEngineLanguages.Find(name)
            ?? throw new TranslationFailedException(FreeEngineMessages.UnsupportedLanguage(name));

    private readonly record struct Attempt(string? Text, Exception? Failure);

    /// <summary>
    /// 让一家引擎译一块，单块限时。用户取消原样抛出——永远不算失败、不触发兜底；
    /// 其余一切（网络、超时、形状不对、空译文）折成一次失败交回调用方裁决。
    /// </summary>
    private async Task<Attempt> AttemptAsync(
        IWholeTextTranslator engine,
        string text,
        FreeEngineLanguage? source,
        FreeEngineLanguage target,
        CancellationToken cancellation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(_chunkTimeout);

        try
        {
            var translated = await engine.TranslateAsync(text, source, target, limit.Token);

            // 非空的原文换来空的译文，不是"没有可译的"，是这家没给出结果。
            return string.IsNullOrWhiteSpace(translated)
                ? new Attempt(null, new FreeEngineException("引擎返回了空的译文"))
                : new Attempt(translated, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            // expected: 失败不在这里说——交回调用方决定兜底还是报错；单块超时折成一般失败。
            return new Attempt(
                null,
                failure is OperationCanceledException ? new TimeoutException("单块翻译超时") : failure);
        }
    }

    /// <summary>两家各自的原因都带上（「复制错误详情」里两家都看得见）；只有一家就是它自己。</summary>
    private static Exception? Combine(Exception? bing, Exception? tencent)
        => bing is null ? tencent : tencent is null ? bing : new AggregateException(bing, tencent);
}
