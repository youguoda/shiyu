#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shiyu.Core;
using Shiyu.Windows;

namespace Shiyu.App;

/// <summary>反向输入框的调试探针区（只在 Debug 构建存在，同 App.DebugProbes.cs）。</summary>
internal partial class ReverseInputWindow
{
    /// <summary>
    /// 探针命令 reverse-select（用户需求 2026-10-05：译文能用鼠标选取复制）。把一句话放进
    /// 输入框，等后端译完，看结算后的输出是不是可选取的只读框；选中开头几个字，问一声
    /// "复制"此刻能不能执行——只问不执行，不碰用户的剪贴板。渲一张窗口 PNG 供人眼看。
    /// 2026-10-10 起输出是一个按 Markdown 排的框（ADR-0014），从流式到结算都是它：日志的
    /// label 一栏（旧的流式标签）恒为 False，行距拿来比的是内容字号的行高。
    /// </summary>
    internal async Task<string> ProbeSelectableOutput(string text, string picture)
    {
        InputBox.Text = text;
        for (var waited = 0; waited < 60 && (_session.Running || _session.SettledOutput is null); waited++)
        {
            await Task.Delay(100);
        }

        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        var log = new StringBuilder();
        log.AppendLine(FormattableString.Invariant(
            $"settled|box={OutputDoc.IsVisible}|readOnly={OutputDoc.IsReadOnly}|label=False|text={OutputDoc.Shown}"));

        // 行距必须等于内容字号的行高（Line.ContentCompact）——多行译文两行之间就是这一个值。
        var first = OutputDoc.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var lines = 1;
        for (var line = first.GetLineStartPosition(1); line is not null; line = line.GetLineStartPosition(1))
        {
            lines++;
        }

        var second = first.GetLineStartPosition(1);
        var pitch = second is null
            ? double.NaN
            : second.GetCharacterRect(LogicalDirection.Forward).Top - first.GetCharacterRect(LogicalDirection.Forward).Top;
        log.AppendLine(FormattableString.Invariant(
            $"pitch|box={pitch:F1}|label={(double)FindResource("Line.ContentCompact"):F1}|lines={lines}"));

        OutputDoc.Focus();
        var end = first;
        for (var step = 0; step < 4 && end.GetNextInsertionPosition(LogicalDirection.Forward) is { } next; step++)
        {
            end = next;
        }

        OutputDoc.Selection.Select(first, end);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        log.AppendLine(FormattableString.Invariant(
            $"selected|text={OutputDoc.Selection.Text}|canCopy={ApplicationCommands.Copy.CanExecute(null, OutputDoc)}"));

        SavePicture(picture);
        return log.ToString();
    }

    /// <summary>
    /// 探针命令 reverse-enter（用户需求 2026-10-13：加一个回车按钮）。按钮在底栏最右、是回车字形、
    /// 不拿焦点、不是拖动把手；空着时变灰；译文落定时可点、念作"贴回"。换到改写类模板：可点、念作
    /// "整理"；组字中点它不动；点它开跑，跑的时候变灰，跑完停下等过目，窗口还在。改写类第一次按
    /// 不会贴回，所以探针只点这一步，不碰剪贴板，也不往别的窗口发按键。名字比对在这边做，日志只写 ASCII。
    /// </summary>
    internal async Task<string> ProbeEnterButton(string text, string picture)
    {
        var log = new StringBuilder();

        async Task<bool> Settled()
        {
            for (var waited = 0; waited < 60; waited++)
            {
                if (!_session.Running && _session.SettledOutput is not null)
                {
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        void Line(string step, string action) => log.AppendLine(FormattableString.Invariant(
            $"{step}|enabled={EnterButton.IsEnabled}|named={AutomationProperties.GetName(EnterButton) == $"回车：{action}"}|tip={EnterButton.ToolTip as string == $"{action}（Enter）"}|status={_session.Status}|visible={IsVisible}"));

        void Click() => EnterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        // 从标准模板起步：呼出时沿用的可能是上次选着的改写类。
        PickTemplate(PromptTemplates.Standard);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        var buttonLeft = EnterButton.TranslatePoint(new Point(0, 0), Shell).X;
        var hintLeft = HintText.TranslatePoint(new Point(0, 0), Shell).X;
        log.AppendLine(FormattableString.Invariant(
            $"button|visible={EnterButton.IsVisible}|glyph={EnterGlyph.Text == ((char)0xE751).ToString()}|focusable={EnterButton.Focusable}|grip={DragGrip.IsGrip(EnterGlyph, Shell)}|rightOfHint={buttonLeft > hintLeft}"));
        Line("empty", "贴回");

        InputBox.Text = text;
        log.AppendLine(FormattableString.Invariant($"translated|ok={await Settled()}"));
        Line("ready", "贴回");

        var rewrite = PromptTemplates.All(_settings).First(template => template.Kind == PromptTemplateKind.Rewrite);
        PickTemplate(rewrite);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Line("needs", "整理");

        // 安全闸：真点之前必须站在改写类、还没开跑——否则这一下可能就是贴回。
        if (_session.Template.Kind != PromptTemplateKind.Rewrite || _session.Status != ReverseInputStatus.NeedsEnter)
        {
            log.AppendLine("unsafe|click=skipped");
            CancelAndClose();
            return log.ToString();
        }

        _session.SetComposing(true);
        Click();
        log.AppendLine(FormattableString.Invariant($"composing|status={_session.Status}"));
        _session.SetComposing(false);

        Click();
        Line("running", "整理");

        log.AppendLine(FormattableString.Invariant($"rewritten|ok={await Settled()}"));
        Line("reviewed", "贴回");

        // 此刻的提示最长（"整理好了，过目后再按 Enter 贴回 · Esc 关闭"）：宽度不够时让提示截断，
        // chip 不能被盖住或裁掉，提示也不能伸进按钮。
        var chipRight = TemplateChip.TranslatePoint(new Point(TemplateChip.ActualWidth, 0), Shell).X;
        var hintStart = HintText.TranslatePoint(new Point(0, 0), Shell).X;
        var hintEnd = HintText.TranslatePoint(new Point(HintText.ActualWidth, 0), Shell).X;
        var buttonStart = EnterButton.TranslatePoint(new Point(0, 0), Shell).X;
        log.AppendLine(FormattableString.Invariant(
            $"bar|chipRight={chipRight:F1}|hintStart={hintStart:F1}|hintEnd={hintEnd:F1}|buttonStart={buttonStart:F1}|chipsFit={chipRight <= hintStart + 0.5}|hintClear={hintEnd <= buttonStart + 0.5}"));

        SavePicture(picture);
        CancelAndClose();
        return log.ToString();
    }

    /// <summary>窗口内容按 1.5 倍渲一张 PNG（铺上窗口底色），供人眼看。</summary>
    private void SavePicture(string picture)
    {
        if (Content is not FrameworkElement root || root.ActualWidth <= 0)
        {
            return;
        }

        const double scale = 1.5;
        var sheet = new DrawingVisual();
        using (var context = sheet.RenderOpen())
        {
            var area = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            context.DrawRectangle((Brush)FindResource("Brush.Background"), null, area);
            context.DrawRectangle(new VisualBrush(root), null, area);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(sheet);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(picture);
        encoder.Save(stream);
    }

    /// <summary>
    /// 探针命令 translation-log 的反向输入框一段（用户需求 2026-10-09：两个框都能拖、翻译记下来）。
    /// 放一句话进去、等它落定，然后：
    ///   - 问拖动的把手：底栏提示是把手；输入框、输出框、两枚 chip 不是（选字、点按钮照旧）；
    ///   - 把窗口挪到远处、走拖动收尾，再让内容长高：窗口应留在拖到的地方，而不是被拽回插入符旁；
    ///   - 走 Esc 的路径关窗：落定的结果应记进翻译记录（由调用方查库）。
    /// 系统的移动循环要真鼠标，探针不碰用户的鼠标——拖动本身只能由人来试，这里验的是它前后两头。
    /// </summary>
    internal async Task<string> ProbeTranslationLog(string text)
    {
        var log = new StringBuilder();

        async Task<bool> Settled()
        {
            for (var waited = 0; waited < 80; waited++)
            {
                if (_session.SettledOutput is not null && OutputDoc.Visibility == Visibility.Visible)
                {
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        InputBox.Text = text;
        log.AppendLine(FormattableString.Invariant($"settled|ok={await Settled()}|output={_session.SettledOutput}"));

        log.AppendLine(FormattableString.Invariant(
            $"grip|hint={DragGrip.IsGrip(HintText, Shell)}|input={DragGrip.IsGrip(InputBox, Shell)}|output={DragGrip.IsGrip(OutputDoc, Shell)}|template={DragGrip.IsGrip(TemplateChip, Shell)}|direction={DragGrip.IsGrip(DirectionChip, Shell)}"));

        // 落点离原位尽量远：原来在右半边就挪到左边，反之亦然；纵向留出长高的余地。
        var handle = new WindowInteropHelper(this).Handle;
        WindowRects.TryGet(handle, out var start);
        var middle = _workArea.Left + _workArea.Width / 2;
        var target = new ScreenPoint(
            start.Left + start.Width / 2 > middle ? _workArea.Left + 40 : _workArea.Right - start.Width - 40,
            start.Top > _workArea.Top + 200 ? _workArea.Top + 40 : _workArea.Top + 240);
        TransientWindow.MoveTo(handle, target, ZBand.Topmost);
        KeepWhereDragged();

        InputBox.Text = string.Join("\n", text, text, text, text);
        var grown = await Settled();
        WindowRects.TryGet(handle, out var after);
        log.AppendLine(FormattableString.Invariant(
            $"drag|settled={grown}|from={start.Left},{start.Top},{start.Height}|target={target.X},{target.Y}|after={after.Left},{after.Top},{after.Height}"));

        CancelAndClose();
        log.AppendLine(FormattableString.Invariant($"closed|visible={IsVisible}"));
        return log.ToString();
    }
}
#endif
