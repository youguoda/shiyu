#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 探针命令 preview-workbench / preview-anchor / preview-zoom-perf / preview-decode-cost /
/// preview-churn 的管理窗编排（用户实录 2026-10-20：预览弹窗新旧内容叠显、跨条串台、图片
/// 缩放卡顿——测试方案的复现与测量段）。判断在应用里做完，日志只收 ASCII；不碰真剪贴板：
/// 动作矩阵里只点 save-as-new（只写探针自己的库）与 copy-image（对没有原图文件的条目
/// 是无操作）。唯一动真光标的是 preview-anchor（脚本侧有 Test-UserPresent 守卫）。
/// </summary>
internal partial class BarWindow
{
    private readonly List<string> _workbenchSink = [];

    private int _workbenchSinkFired;

    private bool _workbenchSinkArmed;

    /// <summary>事件汇：WorkbenchAction 的第二只订户；真实执行者照常跑（save-as-new 落库可查）。</summary>
    internal void ProbeSinkStart()
    {
        if (_workbenchSinkArmed)
        {
            _workbenchSink.Clear();
            return;
        }

        _workbenchSinkArmed = true;
        EnsurePreview().WorkbenchAction += (action, id, text) =>
        {
            _workbenchSinkFired++;
            _workbenchSink.Add($"{action}:{id}:{AsciiShort(text)}");
        };
    }

    internal int ProbeSinkFired => _workbenchSinkFired;

    /// <summary>汇里的事件，最多带三条（已 ASCII 化）。</summary>
    internal string ProbeSinkLine => _workbenchSinkArmed
        ? "n=" + _workbenchSink.Count + string.Concat(
            _workbenchSink.Take(3).Select((line, index) => "|" + index + "=" + line))
        : "unarmed";

    private static string AsciiShort(string text)
    {
        var keep = new string(text.Where(ch => ch is >= ' ' and <= '~').Take(48).ToArray());
        return keep.Length == text.Length ? keep : keep + "+" + text.Length + "ch";
    }

    /// <summary>按谓词找卡（save-as-new 会追加条目改变列表，卡片一律在入口解析引用）。</summary>
    internal BarCard? ProbeFindCard(Func<BarCard, bool> match)
    {
        var card = _cards.FirstOrDefault(match);
        if (card is null)
        {
            ReloadData();
            card = _cards.FirstOrDefault(match);
        }

        return card;
    }

    /// <summary>真实悬停路径出预览（同 RunPreviewCommand 的 Open 臂，BarPreview.cs:31-33）。</summary>
    internal async Task<bool> ProbeShowPreviewForAsync(BarCard card)
    {
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
            ?? Matrix.Identity;
        EnsurePreview().ShowFor(card, PlacementAnchor(AnchorFor(card) ?? WindowRect()), slide: false,
            scaleX: scale.M11, scaleY: scale.M22, band: ZBandPolicy.FollowsHost(Topmost));
        for (var waited = 0; waited < 30
            && (_preview is not { IsVisible: true } || _preview.CardId != card.Id); waited++)
        {
            await Task.Delay(100);
        }

        await ProbeSettleAsync();
        return _preview is { IsVisible: true };
    }

    private static async Task ProbeSettleAsync()
    {
        for (var round = 0; round < 4; round++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }
    }

    private async Task<bool> ProbeWaitAsync(Func<bool> done, int ticks)
    {
        for (var waited = 0; waited < ticks && !done(); waited++)
        {
            await Task.Delay(100);
        }

        return done();
    }

    private async Task ClosePreviewAsync()
    {
        if (_preview is not { IsVisible: true } preview)
        {
            return;
        }

        if (preview.ProbePinned)
        {
            preview.ProbeClickPin();   // 拔 → 立即收起。
        }
        else
        {
            preview.TakeDown();
        }

        await ProbeWaitAsync(() => _preview is not { IsVisible: true }, 30);
        await Task.Delay(120);
    }

    /// <summary>等原图落位（ProbeImagePixelSize 的宽越过缩略图的 320）。</summary>
    private async Task<bool> ProbeWaitOriginalAsync()
    {
        for (var waited = 0; waited < 50; waited++)
        {
            var size = _preview?.ProbeImagePixelSize ?? "none";
            var head = size.Split('x')[0];
            if (int.TryParse(head, out var pixels) && pixels > 320)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static int ProbeWidthOf(string size)
        => int.TryParse(size.Split('x')[0], out var width) ? width : 0;

    // --- preview-workbench：跨显陈旧矩阵 ----------------------------------------

    /// <summary>钉 textA 改草稿 → 拔 → 预览 textB → 未钉点遗留芯片 → 钉/拔 3 轮再点 →
    /// 文字→图片→链接 → 图片平移后换图 → 钉着删卡 → Esc 与双击。逐段一行。</summary>
    internal async Task<string> ProbeWorkbenchMatrixAsync()
    {
        var log = new StringBuilder();
        var textA = ProbeFindCard(card => card.Text.Contains("01 - short placeholder", StringComparison.Ordinal));
        var textB = ProbeFindCard(card => card.Text.Contains("placeholder line 02", StringComparison.Ordinal));
        var link = ProbeFindCard(card => card.Text.Contains("link-03", StringComparison.Ordinal));
        var img1 = ProbeFindCard(card => card.Kind == EntryKind.Image && card.PixelWidth == 800);
        var img2 = ProbeFindCard(card => card.Kind == EntryKind.Image && card.PixelWidth == 640);
        var longText = ProbeFindCard(card => card.Text.Contains("long placeholder body text", StringComparison.Ordinal));
        if (textA is null || textB is null || link is null || img1 is null || img2 is null || longText is null)
        {
            return "w|cards=False\n";
        }

        var store0 = _store.Count();
        ProbeSinkStart();
        log.AppendLine(FormattableString.Invariant($"w|step=baseline|store0={store0}"));

        // A. 钉 textA 设草稿 → 拔 → 预览 textB：叠印与串台的现场。
        await ProbeShowPreviewForAsync(textA);
        _preview!.ProbeClickPin();
        await Task.Delay(200);
        var pinChips = _preview.ProbeChipActions;
        _preview.ProbeSetDraft("DRAFT-A-0123456789");
        await ClosePreviewAsync();
        await ProbeShowPreviewForAsync(textB);
        var shown = _preview!;
        log.AppendLine(FormattableString.Invariant(
            $"w|step=A-text-text|pinChips={pinChips}|editVisible={shown.ProbeEditHostVisible}|textBodyVisible={shown.ProbeTextHostVisible}|draftIsA={shown.ProbeDraftTextIs("DRAFT-A-0123456789")}|draftLen={shown.ProbeDraftTextLength}|chipsVisible={shown.ProbeActionRowVisible}|chips={shown.ProbeChipActions}|teachVisible={shown.ProbeTeachVisible}|metaVisible={shown.ProbeMetaVisible}"));

        // B. 未钉状态点遗留芯片：事件照发，带的是上一条的草稿。
        var firedBefore = ProbeSinkFired;
        var clicked = shown.ProbeChipClick("save-as-new");
        await Task.Delay(400);
        log.AppendLine(FormattableString.Invariant(
            $"w|step=B-chip-unpinned|clicked={clicked}|fired={ProbeSinkFired - firedBefore}|store={_store.Count() - store0}|{ProbeSinkLine}"));
        await ClosePreviewAsync();

        // C. 钉/拔 3 轮（A 里第 1 钉，共 5 钉）后一次点击：一次触发 5 个 handler。
        for (var round = 0; round < 3; round++)
        {
            await ProbeShowPreviewForAsync(textB);
            _preview!.ProbeClickPin();
            await Task.Delay(150);
            await ClosePreviewAsync();
        }

        await ProbeShowPreviewForAsync(textB);
        _preview!.ProbeClickPin();
        await Task.Delay(200);
        _preview.ProbeSetDraft("DRAFT-B-9876543210");
        firedBefore = ProbeSinkFired;
        var clickedC = _preview.ProbeChipClick("save-as-new");
        await Task.Delay(500);
        log.AppendLine(FormattableString.Invariant(
            $"w|step=C-accumulate|clicked={clickedC}|clicksFired={ProbeSinkFired - firedBefore}|store={_store.Count() - store0}"));
        await ClosePreviewAsync();

        // D. 文字→图片→链接：芯片表随形态换，残留的下一显还带着上一形态。
        await ProbeShowPreviewForAsync(img1);
        _preview!.ProbeClickPin();
        await Task.Delay(300);
        var imgChips = _preview.ProbeChipActions;
        var imgEditVisible = _preview.ProbeEditHostVisible;
        for (var tick = 0; tick < 4; tick++)
        {
            _preview.ProbeWheel(120);
            await Task.Delay(60);
        }

        var scaleAfterWheel = _preview.ProbeScale;
        await ClosePreviewAsync();
        await ProbeShowPreviewForAsync(link);
        var linkChips = _preview.ProbeChipActions;
        firedBefore = ProbeSinkFired;
        var crossClicked = _preview!.ProbeChipClick("copy-image");
        await Task.Delay(300);
        log.AppendLine(FormattableString.Invariant(
            $"w|step=D-text-image-link|imgChips={imgChips}|imgEditVisible={imgEditVisible}|scaleAfterWheel={scaleAfterWheel:F6}|linkChips={linkChips}|crossClicked={crossClicked}|fired={ProbeSinkFired - firedBefore}|{ProbeSinkLine}"));
        await ClosePreviewAsync();

        // E. 图片→图片：缩放/平移原样带进下一张。
        await ProbeShowPreviewForAsync(img1);
        _preview!.ProbeClickPin();
        await Task.Delay(300);
        var center = _preview.ProbeImageCenter;
        await _preview.ProbePressAtAsync(center.X, center.Y);
        await _preview.ProbeMoveToAsync(center.X + 40, center.Y + 20);
        await _preview.ProbeReleaseAsync();
        var originAfterPan = _preview.ProbeOrigin;
        await ClosePreviewAsync();
        await ProbeShowPreviewForAsync(img2);
        log.AppendLine(FormattableString.Invariant(
            $"w|step=E-image-image|originAfterPan={originAfterPan}|scale={_preview!.ProbeScale:F6}|origin={_preview.ProbeOrigin}"));
        await ClosePreviewAsync();

        // F. 钉着删卡：强制收起跳过视觉复位——图钉、元信息行、下一击的语义全串。
        await ProbeShowPreviewForAsync(longText);
        _preview!.ProbeClickPin();
        await Task.Delay(200);
        ProbeRemoveCard(longText);
        await Task.Delay(400);
        var pinnedAfterRemove = _preview.ProbePinned;
        var glyphAfterRemove = _preview.ProbePinGlyph;
        await ProbeWaitAsync(() => _preview is not { IsVisible: true }, 30);
        await Task.Delay(150);
        await ProbeShowPreviewForAsync(textB);
        var whenVisible = _preview.ProbeWhenVisible;
        var kindVisible = _preview.ProbeKindVisible;
        var pinnedBefore = _preview.ProbePinned;
        var glyphBeforeClick = _preview.ProbePinGlyph;
        _preview.ProbeClickPin();
        await Task.Delay(200);
        log.AppendLine(FormattableString.Invariant(
            $"w|step=F-forced|pinnedAfterRemove={pinnedAfterRemove}|glyph={glyphAfterRemove}|whenVisible={whenVisible}|kindVisible={kindVisible}|pinnedBefore={pinnedBefore}|glyphBeforeClick={glyphBeforeClick}|pinnedAfterClick={_preview.ProbePinned}"));
        await ClosePreviewAsync();

        // G. Esc 退出编辑（宣称有、实际无）与双击复位（处理器从未接线）。
        await ProbeShowPreviewForAsync(textB);
        _preview!.ProbeClickPin();
        await Task.Delay(200);
        _preview.ProbeEscapeInEdit();
        await Task.Delay(250);
        var editStillVisible = _preview.ProbeEditHostVisible;
        await ClosePreviewAsync();
        await ProbeShowPreviewForAsync(img1);
        _preview!.ProbeClickPin();
        await Task.Delay(300);
        for (var tick = 0; tick < 4; tick++)
        {
            _preview.ProbeWheel(120);
            await Task.Delay(60);
        }

        await _preview.ProbeDoubleClickAtAsync(_preview.ProbeImageCenter.X, _preview.ProbeImageCenter.Y);
        await Task.Delay(250);
        log.AppendLine(FormattableString.Invariant(
            $"w|step=G-esc-dblclick|editStillVisible={editStillVisible}|scaleAfterDblClick={_preview.ProbeScale:F6}"));
        await ClosePreviewAsync();

        // H. 钉住后整窗可拖：拖 (60, 40)（局部 DIP），窗口跟动；同卡再显不回锚。
        await ProbeShowPreviewForAsync(img1);
        _preview!.ProbeClickPin();
        await Task.Delay(300);
        var xy0 = _preview.ProbeWindowXY;
        var hCenter = _preview.ProbeImageCenter;
        await _preview.ProbePressAtAsync(hCenter.X, hCenter.Y);
        await _preview.ProbeMoveToAsync(hCenter.X + 60, hCenter.Y + 40);
        await _preview.ProbeReleaseAsync();
        await Task.Delay(150);
        var xy1 = _preview.ProbeWindowXY;
        await ProbeShowPreviewForAsync(img1);   // 同卡再显：不许回锚。
        await ProbeSettleAsync();
        var xy2 = _preview.ProbeWindowXY;
        log.AppendLine(FormattableString.Invariant(
            $"w|step=H-window-drag|xy0={xy0}|xy1={xy1}|xy2={xy2}"));
        await ClosePreviewAsync();

        // I. 颜色条目：芯片表只有「复制色值」——不碰剪贴板，只验表。
        var color = ProbeFindCard(card => card.Subtype == Shiyu.Core.EntrySubtype.Color);
        if (color is not null)
        {
            await ProbeShowPreviewForAsync(color);
            _preview!.ProbeClickPin();
            await Task.Delay(300);
            log.AppendLine(FormattableString.Invariant(
                $"w|step=color-chips|chips={_preview.ProbeChipActions}"));

            // 拔钉再收：钉着的预览不认 TakeDown，带着钉住态进下一行，下一行的「钉住」点下去反而是拔钉（QA F-08）。
            await ClosePreviewAsync();
            await Task.Delay(200);
        }

        // J. 邮箱条目：芯片含「存为新条目」——点它入探针库（不碰剪贴板）。
        var email = ProbeFindCard(card => card.Subtype == Shiyu.Core.EntrySubtype.Email);
        if (email is not null)
        {
            var before = _store.Count();
            await ProbeShowPreviewForAsync(email);
            _preview!.ProbeClickPin();
            await Task.Delay(300);
            var chips = _preview.ProbeChipActions;
            _preview.ProbeChipClick("save-as-new");
            await Task.Delay(400);
            log.AppendLine(FormattableString.Invariant(
                $"w|step=email-save|chips={chips}|added={_store.Count() - before}"));
            await ClosePreviewAsync();
            await Task.Delay(200);
        }

        // K. 文件条目（芯片表「打开/定位/复制路径」——只验表，不真开）。
        var files = ProbeFindCard(card => card.Kind == EntryKind.Files);
        if (files is not null)
        {
            await ProbeShowPreviewForAsync(files);
            _preview!.ProbeClickPin();
            await Task.Delay(300);
            log.AppendLine(FormattableString.Invariant(
                $"w|step=files-chips|chips={_preview.ProbeChipActions}"));
            await ClosePreviewAsync();
            await Task.Delay(200);
        }

        return log.ToString();
    }

    /// <summary>直调私有 RemoveCard：删卡强制收起的真实路径（BarCards.cs:758-767）。</summary>
    internal void ProbeRemoveCard(BarCard card) => RemoveCard(card);

    // --- preview-anchor：缩放锚点漂移 --------------------------------------------

    /// <summary>平移后再在同一屏幕点滚轮：光标下的图像点该不动（漂移 = 前后读数差）。</summary>
    internal async Task<string> ProbeAnchorAsync()
    {
        var log = new StringBuilder();
        var img1 = ProbeFindCard(card => card.Kind == EntryKind.Image && card.PixelWidth == 800);
        if (img1 is null)
        {
            return "anchor|card=False\n";
        }

        await ProbeShowPreviewForAsync(img1);
        if (_preview is not { IsVisible: true } preview)
        {
            return "anchor|notVisible\n";
        }

        preview.ProbeClickPin();
        await Task.Delay(300);
        if (!await ProbeWaitOriginalAsync())
        {
            log.AppendLine("anchor|original=False");
            return log.ToString();
        }

        var center = preview.ProbeImageCenter;
        for (var tick = 0; tick < 4; tick++)
        {
            if (!await preview.ProbeWheelAtAsync(center.X, center.Y, 120))
            {
                log.AppendLine("anchor|cursor=False");
                return log.ToString();
            }

            await Task.Delay(80);
        }

        var scale1 = preview.ProbeScale;
        var u1 = preview.ProbeLocalUnderCursor;
        await preview.ProbePressAtAsync(center.X, center.Y);
        await preview.ProbeMoveToAsync(center.X + 40, center.Y + 20);
        await preview.ProbeReleaseAsync();
        var originAfterPan = preview.ProbeOrigin;

        // 第二段缩放钉在同一个屏幕点：漂移只许来自处理器自己的锚点算术。
        var fixedScreen = preview.ProbeImageCenterOnScreen;
        for (var tick = 0; tick < 4; tick++)
        {
            if (!preview.ProbeCursorToScreen(fixedScreen.X, fixedScreen.Y))
            {
                log.AppendLine("anchor|cursor=False");
                return log.ToString();
            }

            await PreviewWindow.ProbePumpAsync();
            preview.ProbeWheel(120);
            await Task.Delay(80);
        }

        var scale2 = preview.ProbeScale;
        var u2 = preview.ProbeLocalUnderCursor;
        var drift = ProbeDistance(u1, u2);
        log.AppendLine(FormattableString.Invariant(
            $"anchor|scale1={scale1:F6}|u1={u1}|originAfterPan={originAfterPan}|scale2={scale2:F6}|u2={u2}|drift={drift:F3}"));
        await ClosePreviewAsync();
        return log.ToString();
    }

    private static double ProbeDistance(string a, string b)
    {
        var first = a.Split(';');
        var second = b.Split(';');
        if (first.Length != 2 || second.Length != 2
            || !double.TryParse(first[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)
            || !double.TryParse(first[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay)
            || !double.TryParse(second[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var bx)
            || !double.TryParse(second[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var by))
        {
            return -1;
        }

        return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }

    // --- preview-zoom-perf：滚轮/平移的响应度 ------------------------------------

    internal async Task<string> ProbeZoomPerfAsync()
    {
        var log = new StringBuilder();
        var large = ProbeFindCard(card => card.Kind == EntryKind.Image && card.PixelWidth == 3840);
        var small = ProbeFindCard(card => card.Kind == EntryKind.Image && card.PixelWidth == 900);
        if (large is null || small is null)
        {
            return "zoom|cards=False\n";
        }

        await ProbeZoomRoundsAsync(large, "large", log, withPan: true);
        await ProbeZoomRoundsAsync(small, "small", log, withPan: false);
        return log.ToString();
    }

    /// <summary>放大 120 拍（+ 平移 30 拍 + 收尾 40 拍）逐段量：Input 针（UI 线程）与
    /// CompositionTarget.Rendering 帧间隙（渲染线程——HighQuality 重滤波的卡顿只有这里抓得到）。</summary>
    private async Task ProbeZoomRoundsAsync(BarCard card, string label, StringBuilder log, bool withPan)
    {
        await ProbeShowPreviewForAsync(card);
        if (_preview is not { IsVisible: true } preview)
        {
            log.AppendLine(FormattableString.Invariant($"zoom|img={label}|visible=False"));
            return;
        }

        preview.ProbeClickPin();
        await Task.Delay(300);
        await ProbeWaitOriginalAsync();
        var center = preview.ProbeImageCenter;

        var clock = Stopwatch.StartNew();
        var started = new Dictionary<DispatcherOperation, long>();
        long longest = 0;
        long busy = 0;
        void OnStarted(object? sender, DispatcherHookEventArgs e) => started[e.Operation] = clock.ElapsedTicks;
        void OnEnded(object? sender, DispatcherHookEventArgs e)
        {
            if (started.Remove(e.Operation, out var at))
            {
                var took = clock.ElapsedTicks - at;
                longest = Math.Max(longest, took);
                busy += took;
            }
        }

        var latencies = new List<double>();
        using var stop = new System.Threading.CancellationTokenSource();
        var pinger = System.Threading.Tasks.Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var posted = clock.ElapsedTicks;
                await Dispatcher.InvokeAsync(
                    () => latencies.Add((clock.ElapsedTicks - posted) * 1000.0 / Stopwatch.Frequency),
                    DispatcherPriority.Input);
                await Task.Delay(25);
            }
        });

        var gaps = new List<double>();
        var lastFrame = clock.Elapsed;
        void OnRendering(object? sender, EventArgs e)
        {
            var now = clock.Elapsed;
            gaps.Add((now - lastFrame).TotalMilliseconds);
            lastFrame = now;
        }

        CompositionTarget.Rendering += OnRendering;
        Dispatcher.Hooks.OperationStarted += OnStarted;
        Dispatcher.Hooks.OperationCompleted += OnEnded;
        Dispatcher.Hooks.OperationAborted += OnEnded;

        static double At(List<double> values, double share)
        {
            if (values.Count == 0)
            {
                return -1;
            }

            values.Sort();
            return values[Math.Min(values.Count - 1, (int)Math.Floor(values.Count * share))];
        }

        void Emit(string name, Stopwatch watch, int latFrom, int gapFrom, long busyFrom)
        {
            var input = latencies.Skip(latFrom).ToList();
            var frame = gaps.Skip(gapFrom).ToList();
            var longestMs = longest * 1000.0 / Stopwatch.Frequency;
            var busyPct = 100.0 * (busy - busyFrom) / Math.Max(1, watch.ElapsedTicks);
            log.AppendLine(FormattableString.Invariant(
                $"{name}|img={label}|ms={watch.Elapsed.TotalMilliseconds:F0}|pings={input.Count}|inputMaxMs={At(input, 0.999):F0}|inputP95Ms={At(input, 0.95):F0}|longestOpMs={longestMs:F0}|busyPct={busyPct:F0}|frames={frame.Count}|gapMaxMs={At(frame, 0.999):F0}|gapP95Ms={At(frame, 0.95):F0}"));
        }

        // 适应时图源的像素数：放大一开始就会换全尺寸原图，得在放大前记下。
        var fitPixels = preview.ProbeSourcePixels;

        // 放大段。
        var busyFrom = busy;
        var latFrom = latencies.Count;
        var gapFrom = gaps.Count;
        var watch = Stopwatch.StartNew();
        for (var tick = 0; tick < 120; tick++)
        {
            preview.ProbeWheel(120);
            await Task.Delay(16);
        }

        await Task.Delay(150);
        var scaleAfterUp = preview.ProbeScale;
        Emit("zoom", watch, latFrom, gapFrom, busyFrom);

        // 放大后换上原图全部像素（用户反馈 2026-10-22：放大越看越糊）：等后台解码落位。
        for (var waited = 0; waited < 60 && preview.ProbeSourcePixels == fitPixels; waited++)
        {
            await Task.Delay(50);
        }

        log.AppendLine(FormattableString.Invariant(
            $"sharp|img={label}|fitPx={fitPixels}|zoomPx={preview.ProbeSourcePixels}|cardPx={card.PixelWidth}x{card.PixelHeight}"));

        if (withPan)
        {
            // 平移段。
            busyFrom = busy;
            latFrom = latencies.Count;
            gapFrom = gaps.Count;
            watch.Restart();
            await preview.ProbePressAtAsync(center.X, center.Y);
            for (var step = 1; step <= 30; step++)
            {
                await preview.ProbeMoveToAsync(center.X + step, center.Y + step / 2.0);
                await Task.Delay(16);
            }

            await preview.ProbeReleaseAsync();
            await Task.Delay(150);
            Emit("pan", watch, latFrom, gapFrom, busyFrom);
        }

        // 收尾段：一路缩回 1.0（顺带核钳制与回零）。
        busyFrom = busy;
        latFrom = latencies.Count;
        gapFrom = gaps.Count;
        watch.Restart();
        for (var tick = 0; tick < 40; tick++)
        {
            preview.ProbeWheel(-120);
            await Task.Delay(16);
        }

        await Task.Delay(150);
        Emit("wind", watch, latFrom, gapFrom, busyFrom);
        log.AppendLine(FormattableString.Invariant(
            $"clamps|img={label}|scaleAfterUp={scaleAfterUp:F6}|scaleAfterDown={preview.ProbeScale:F6}|origin={preview.ProbeOrigin}"));

        // 适应时双击 → 一比一，再双击 → 回到适应（用户需求 2026-10-22）。
        await Task.Delay(600);
        var expectedActual = preview.ProbeActualSizeScale;
        await preview.ProbeDoubleClickAtAsync(center.X, center.Y);
        await Task.Delay(250);
        var actualScale = preview.ProbeScale;
        await Task.Delay(600);
        await preview.ProbeDoubleClickAtAsync(center.X, center.Y);
        await Task.Delay(250);
        log.AppendLine(FormattableString.Invariant(
            $"actual|img={label}|expected={expectedActual:F4}|scale={actualScale:F4}|back={preview.ProbeScale:F4}"));

        CompositionTarget.Rendering -= OnRendering;
        Dispatcher.Hooks.OperationStarted -= OnStarted;
        Dispatcher.Hooks.OperationCompleted -= OnEnded;
        Dispatcher.Hooks.OperationAborted -= OnEnded;
        stop.Cancel();
        await pinger;
        await ClosePreviewAsync();
    }

    // --- preview-decode-cost：4K 原图首显的冷/暖解码 -----------------------------

    internal async Task<string> ProbeDecodeCostAsync()
    {
        var log = new StringBuilder();
        var large = ProbeFindCard(card => card.Kind == EntryKind.Image && card.PixelWidth == 3840);
        if (large is null)
        {
            return "decode|card=False\n";
        }

        var clock = Stopwatch.StartNew();
        var latencies = new List<double>();
        using var stop = new System.Threading.CancellationTokenSource();
        var pinger = System.Threading.Tasks.Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var posted = clock.ElapsedTicks;
                await Dispatcher.InvokeAsync(
                    () => latencies.Add((clock.ElapsedTicks - posted) * 1000.0 / Stopwatch.Frequency),
                    DispatcherPriority.Input);
                await Task.Delay(25);
            }
        });

        var coldWatch = Stopwatch.StartNew();
        await ProbeShowPreviewForAsync(large);
        var landed = await ProbeWaitOriginalAsync();
        var coldMs = coldWatch.Elapsed.TotalMilliseconds;
        var px = _preview?.ProbeImagePixelSize ?? "none";
        await ClosePreviewAsync();

        var warmWatch = Stopwatch.StartNew();
        await ProbeShowPreviewForAsync(large);
        var warmLanded = ProbeWidthOf(_preview?.ProbeImagePixelSize ?? "none") > 320;
        var warmMs = warmWatch.Elapsed.TotalMilliseconds;
        stop.Cancel();
        await pinger;
        latencies.Sort();
        var inputMax = latencies.Count == 0 ? -1 : latencies[^1];
        await ClosePreviewAsync();
        log.AppendLine(FormattableString.Invariant(
            $"decode|landed={landed}|coldMs={coldMs:F0}|warmLanded={warmLanded}|warmMs={warmMs:F0}|inputMaxMs={inputMax:F0}|px={px}"));
        return log.ToString();
    }

    // --- preview-churn：显/收搅拌应力 --------------------------------------------

    internal async Task<string> ProbeChurnAsync()
    {
        var log = new StringBuilder();
        var text = ProbeFindCard(card => card.Text.Contains("placeholder line 02", StringComparison.Ordinal));
        var image = ProbeFindCard(card => card.Kind == EntryKind.Image && card.PixelWidth == 800);
        var files = ProbeFindCard(card => card.Kind == EntryKind.Files);
        if (text is null || image is null || files is null)
        {
            return "churn|cards=False\n";
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var process = Process.GetCurrentProcess();
        var wsBefore = process.WorkingSet64;
        var handlesBefore = process.HandleCount;
        var gcBefore = GC.GetTotalMemory(false);

        for (var cycle = 0; cycle < 30; cycle++)
        {
            var card = (cycle % 3) switch { 0 => text, 1 => image, _ => files };
            if (!await ProbeShowPreviewForAsync(card))
            {
                log.AppendLine(FormattableString.Invariant($"churn|cycle={cycle}|visible=False"));
                break;
            }

            await ProbeSettleAsync();
            await ClosePreviewAsync();
            await Task.Delay(60);
        }

        await ProbeSettleAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var wsAfter = process.WorkingSet64;
        var handlesAfter = process.HandleCount;
        var gcAfter = GC.GetTotalMemory(false);
        var gcBeforeMb = gcBefore / 1048576.0;
        var gcAfterMb = gcAfter / 1048576.0;
        var wsBeforeMb = wsBefore / 1048576.0;
        var wsAfterMb = wsAfter / 1048576.0;
        var wsDeltaMb = (wsAfter - wsBefore) / 1048576.0;
        log.AppendLine(FormattableString.Invariant(
            $"churn|cycles=30|wsBeforeMB={wsBeforeMb:F0}|wsAfterMB={wsAfterMb:F0}|wsDeltaMB={wsDeltaMb:F0}|gcBeforeMB={gcBeforeMb:F0}|gcAfterMB={gcAfterMb:F0}|handlesBefore={handlesBefore}|handlesDelta={handlesAfter - handlesBefore}|originals={_preview?.ProbeOriginalsCount ?? -1}"));
        return log.ToString();
    }
}
#endif
