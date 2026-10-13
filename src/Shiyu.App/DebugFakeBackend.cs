#if DEBUG
using System.Runtime.CompilerServices;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 探针用的确定性假后端（票 43，只在 Debug 构建存在）：返回 <c>[EN] </c> + 原文，分两段流出，
/// 既不碰网络、也不依赖任何服务的状态，端到端流程（打字 → 出译文 → Enter → 贴回）才有可断言的输出。
/// 由 <c>SHIYU_FAKE_BACKEND=1</c> 打开（<see cref="DebugOverrides.FakeBackend"/>）。
/// </summary>
internal sealed class DebugFakeBackend : ITranslationBackend
{
    public const string Prefix = "[EN] ";

    public async IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return Prefix;

        // 让出一拍：流式的形状（先出前缀、再出正文）与真实后端一致。
        await Task.Delay(30, cancellation);
        yield return request.Text;
    }
}

/// <summary>
/// 与假后端同一个开关的假词典（用户需求 2026-10-13：单词态也能换模板）：给一张只有一条义项的
/// 卡、不碰网络，并记下查了几次——改写类模板下不查、切回翻译类时补查、查过的不重查，探针都要
/// 数得出来。计数是进程级的：每个探针都是新进程。
/// </summary>
internal sealed class DebugFakeDictionary : IDictionaryApi
{
    private static int _lookups;

    public static int Lookups => Volatile.Read(ref _lookups);

    public Task<DictionaryCard?> LookupAsync(string word, CancellationToken cancellation = default)
    {
        Interlocked.Increment(ref _lookups);
        return Task.FromResult<DictionaryCard?>(
            new DictionaryCard(word, "/probe/", [new DictionarySense("n.", ["probe sense"], [], [])]));
    }
}
#endif
