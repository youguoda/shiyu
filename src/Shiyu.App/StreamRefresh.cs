using System.Diagnostics;
using System.Windows.Threading;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 流式出字时把"来了一片"变成"按节拍刷新一回"（用户实录 2026-10-15：LLM 出字时输出一卡一卡、拖
/// 滚动条发涩、偶尔"未响应"）。翻译框与反向输入框共用：片在后台线程上到，这里只按
/// <see cref="RenderCadence"/> 在 UI 线程上排一回刷新，优先级是 Background——低于输入与渲染，用户的
/// 拖动、点击、界面重绘永远先办。原来每一片都同步、以最高优先级（Send）当场把整篇重排一遍。
/// </summary>
internal static class StreamRefresh
{
    /// <summary>来了一片（任何线程）。该排一回刷新就排，已经排着就不重复排——那一回会读到这一片。</summary>
    public static void Request(Dispatcher dispatcher, RenderCadence cadence, Stopwatch clock, Action render)
    {
        if (cadence.Request(clock.Elapsed) is not { } wait)
        {
            return;
        }

        if (wait <= TimeSpan.Zero)
        {
            Post();
        }
        else
        {
            _ = Task.Delay(wait).ContinueWith(_ => Post(), TaskScheduler.Default);
        }

        void Post() => dispatcher.InvokeAsync(
            () =>
            {
                // 刷新开始：此后到的片排下一回。
                cadence.Rendering(clock.Elapsed);
                render();
            },
            DispatcherPriority.Background);
    }
}
