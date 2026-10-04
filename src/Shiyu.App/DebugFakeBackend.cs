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
#endif
