using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 按 Markdown 显示的一段输出（用户需求 2026-10-10，ADR-0014）：翻译框的译文、反向输入框的
/// 输出、翻译记录展开后的结果。块树来自 <see cref="MarkdownText"/>（规则在 Core），这里只管
/// 排成 FlowDocument：标题、列表、代码、引用、表格、分隔线、链接各有各的样子。
///
/// 字号与行高跟宿主走——宿主在本控件上设 FontSize 与 TextBlock.LineHeight（动态资源，内容
/// 字号一变就跟着变），标题与代码按比例绑在这两个值上，从不写死数字。只读、可拖选、Ctrl+C 与
/// 右键菜单是 RichTextBox 自带的（样式 SelectableDocument）；复制按钮、贴回、存历史仍用原文。
///
/// 同一段字不重排：每次状态变化都会走到 <see cref="Show"/>，重排会抹掉用户正在拖的选区（同
/// 反向输入框旧 OutputBox 的规矩）。流式中尾部挂静态 accent 光标块（票 22），结算即撤。
/// </summary>
internal sealed class MarkdownBox : RichTextBox
{
    /// <summary>
    /// 可绑定的入口（翻译记录的行模板）：不可见时不排，显出来再排。用了它的框才走这条路——
    /// 翻译框与反向输入框用 <see cref="Show"/>，它们的可见性一变不能拿空的绑定值把字清掉。
    /// </summary>
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownBox),
        new PropertyMetadata(string.Empty, (target, _) =>
        {
            var box = (MarkdownBox)target;
            box._bound = true;
            box.ShowBound();
        }));

    private const double BlockGap = 8;
    private const double ItemGap = 2;

    private string? _shown;
    private bool _shownStreaming;
    private bool _bound;
    private bool _boundPending;

    public MarkdownBox()
    {
        // RichTextBox imposes its own 5,0,5,0 PagePadding on the document it hosts — set
        // before attaching, a zero does not survive — and the text would sit 5 DIP right of
        // where the TextBlock it replaced put it. So: back to zero once loaded, and on
        // every render.
        Document = new FlowDocument();
        Loaded += (_, _) => Document.PagePadding = new Thickness(0);
        IsVisibleChanged += (_, _) => ShowBound();
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnNavigate));
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>此刻排在屏幕上的原文（探针与宿主核对用）。</summary>
    public string Shown => _shown ?? string.Empty;

    /// <summary>此刻是否挂着流式光标块。</summary>
    public bool Streaming => _shownStreaming;

    /// <summary>
    /// 排出一段 Markdown。<paramref name="streaming"/> 时尾部挂光标块；与上一次完全相同（同字、
    /// 同流式与否）就什么都不做。
    /// </summary>
    public void Show(string markdown, bool streaming)
    {
        if (markdown == _shown && streaming == _shownStreaming)
        {
            return;
        }

        _shown = markdown;
        _shownStreaming = streaming;
        Document.PagePadding = new Thickness(0);

        var blocks = Document.Blocks;
        blocks.Clear();
        foreach (var block in MarkdownText.Parse(markdown))
        {
            blocks.Add(BlockOf(block));
        }

        // The gap is "between" blocks: the last one does not push the box taller.
        if (blocks.LastBlock is { } last)
        {
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }

        if (streaming)
        {
            LastParagraph(blocks).Inlines.Add(new InlineUIContainer(Caret()));
        }
    }

    public void Clear() => Show(string.Empty, streaming: false);

    private void ShowBound()
    {
        if (!_bound)
        {
            return;
        }

        if (!IsVisible)
        {
            _boundPending = true;
            return;
        }

        if (_boundPending || Markdown != _shown)
        {
            _boundPending = false;
            Show(Markdown ?? string.Empty, streaming: false);
        }
    }

    // --- 块 -------------------------------------------------------------------------------

    private Block BlockOf(MdBlock block) => block switch
    {
        MdParagraph paragraph => ParagraphOf(paragraph.Inlines, BlockGap),
        MdHeading heading => HeadingOf(heading),
        MdCodeBlock code => CodeOf(code),
        MdQuote quote => QuoteOf(quote),
        MdList list => ListOf(list),
        MdTable table => TableOf(table),
        MdRule => RuleOf(),
        _ => new Paragraph(),
    };

    private Paragraph ParagraphOf(IReadOnlyList<MdInline> inlines, double gap)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, gap) };
        paragraph.Inlines.AddRange(InlinesOf(inlines));
        return paragraph;
    }

    /// <summary>标题：内容字号的 1.33 / 1.2 / 1.1 倍（四到六级同正文），加粗，与上文多空一点。</summary>
    private Paragraph HeadingOf(MdHeading heading)
    {
        var scale = heading.Level switch
        {
            1 => 1.33,
            2 => 1.2,
            3 => 1.1,
            _ => 1.0,
        };

        var paragraph = ParagraphOf(heading.Inlines, BlockGap / 2);
        paragraph.Margin = new Thickness(0, BlockGap / 2, 0, BlockGap / 2);
        paragraph.SetResourceReference(TextElement.FontWeightProperty, "Weight.Emphasis");
        Scale(paragraph, TextElement.FontSizeProperty, FontSizeProperty, scale);
        Scale(paragraph, Block.LineHeightProperty, Block.LineHeightProperty, scale);
        return paragraph;
    }

    /// <summary>代码块：等宽、九成字号、浅底，空格与换行照原样。</summary>
    private Paragraph CodeOf(MdCodeBlock code)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, BlockGap),
            Padding = new Thickness(10, 6, 10, 6),
        };
        paragraph.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "Brush.SurfaceInput");
        Scale(paragraph, TextElement.FontSizeProperty, FontSizeProperty, 0.9);
        Scale(paragraph, Block.LineHeightProperty, Block.LineHeightProperty, 0.9);

        var lines = code.Code.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(lines[index].TrimEnd('\r')));
        }

        return paragraph;
    }

    /// <summary>引用：左侧一道分隔色竖线，次级色字。</summary>
    private Section QuoteOf(MdQuote quote)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 0, 0, BlockGap),
            Padding = new Thickness(10, 0, 0, 0),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };
        section.SetResourceReference(Block.BorderBrushProperty, "Brush.Divider");
        section.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
        Fill(section.Blocks, quote.Blocks, nested: true);
        return section;
    }

    /// <summary>列表：圆点或数字（从原文的起始号数起）；任务项前面是空框或勾框，不再另加圆点。</summary>
    private List ListOf(MdList list)
    {
        var tasks = list.Items.Any(item => item.Checked is not null);
        var flow = new List
        {
            MarkerStyle = tasks ? TextMarkerStyle.None : list.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            StartIndex = Math.Max(1, list.Start),
            MarkerOffset = 6,
            Padding = new Thickness(tasks ? 4 : 22, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, BlockGap),
        };

        foreach (var item in list.Items)
        {
            var listItem = new ListItem { Margin = new Thickness(0, 0, 0, ItemGap) };
            Fill(listItem.Blocks, item.Blocks, nested: true);
            if (item.Checked is { } done)
            {
                // The box comes from the icon font, as a pair: Unicode's ☑ falls back
                // to an emoji face and never matches the size of its ☐.
                var lead = LeadingParagraph(listItem.Blocks);
                var box = new Run(done ? "\uE73A" : "\uE739");
                box.SetResourceReference(TextElement.FontFamilyProperty, "Font.Icon");
                var gap = new Run(" ");
                if (lead.Inlines.FirstInline is { } first)
                {
                    lead.Inlines.InsertBefore(first, box);
                    lead.Inlines.InsertAfter(box, gap);
                }
                else
                {
                    lead.Inlines.Add(box);
                    lead.Inlines.Add(gap);
                }
            }

            flow.ListItems.Add(listItem);
        }

        return flow;
    }

    /// <summary>表格：等分列宽，表头加粗、底下一道线，每行一道浅线；对齐照分隔行的冒号。</summary>
    private Table TableOf(MdTable table)
    {
        var flow = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, BlockGap) };
        foreach (var _ in table.Columns)
        {
            flow.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        foreach (var row in table.Rows)
        {
            var flowRow = new TableRow();
            if (row.IsHeader)
            {
                flowRow.SetResourceReference(TextElement.FontWeightProperty, "Weight.Emphasis");
            }

            for (var column = 0; column < table.Columns.Count; column++)
            {
                var inlines = column < row.Cells.Count ? row.Cells[column] : [];
                var paragraph = ParagraphOf(inlines, 0);
                paragraph.TextAlignment = table.Columns[column] switch
                {
                    MdAlign.Center => TextAlignment.Center,
                    MdAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };

                var cell = new TableCell(paragraph)
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    BorderThickness = new Thickness(0, 0, 0, row.IsHeader ? 2 : 1),
                };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Brush.Divider");
                flowRow.Cells.Add(cell);
            }

            group.Rows.Add(flowRow);
        }

        flow.RowGroups.Add(group);
        return flow;
    }

    private static BlockUIContainer RuleOf()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.Divider");
        return new BlockUIContainer(line) { Margin = new Thickness(0, 0, 0, BlockGap) };
    }

    /// <summary>列表项、引用里的块：彼此之间只留一点缝，不用正文段落的间距。</summary>
    private void Fill(BlockCollection target, IReadOnlyList<MdBlock> blocks, bool nested)
    {
        foreach (var block in blocks)
        {
            var flow = BlockOf(block);
            if (nested)
            {
                flow.Margin = new Thickness(flow.Margin.Left, flow.Margin.Top, flow.Margin.Right, ItemGap);
            }

            target.Add(flow);
        }

        if (target.LastBlock is { } last)
        {
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }
    }

    // --- 行内 -----------------------------------------------------------------------------

    private IEnumerable<Inline> InlinesOf(IReadOnlyList<MdInline> inlines)
        => inlines.Select(InlineOf);

    private Inline InlineOf(MdInline inline)
    {
        switch (inline)
        {
            case MdText text:
                return new Run(text.Text);

            case MdLineBreak:
                return new LineBreak();

            case MdStrong strong:
                var bold = new Span();
                bold.Inlines.AddRange(InlinesOf(strong.Inlines));
                bold.SetResourceReference(TextElement.FontWeightProperty, "Weight.Emphasis");
                return bold;

            case MdEmphasis emphasis:
                var italic = new Italic();
                italic.Inlines.AddRange(InlinesOf(emphasis.Inlines));
                return italic;

            case MdStrike strike:
                var struck = new Span { TextDecorations = TextDecorations.Strikethrough };
                struck.Inlines.AddRange(InlinesOf(strike.Inlines));
                return struck;

            case MdCode code:
                var run = new Run(code.Code);
                run.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                run.SetResourceReference(TextElement.BackgroundProperty, "Brush.SurfaceInput");
                Scale(run, TextElement.FontSizeProperty, FontSizeProperty, 0.9);
                return run;

            case MdLink link:
                var hyperlink = new Hyperlink { ToolTip = link.Url };
                hyperlink.Inlines.AddRange(InlinesOf(link.Inlines));
                hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
                if (MarkdownText.IsOpenable(link.Url))
                {
                    hyperlink.NavigateUri = new Uri(link.Url);
                }

                return hyperlink;

            default:
                return new Run();
        }
    }

    // --- 杂项 -----------------------------------------------------------------------------

    /// <summary>把一个属性绑成本控件某个值的倍数：内容字号一变，标题与代码跟着按比例变。</summary>
    private void Scale(DependencyObject target, DependencyProperty property, DependencyProperty source, double factor)
        => BindingOperations.SetBinding(target, property, new Binding
        {
            Source = this,
            Path = new PropertyPath(source),
            Converter = ScaleConverter.Instance,
            ConverterParameter = factor,
        });

    private Rectangle Caret()
    {
        // 与旧 TextBlock 时代同一个光标块（票 22）：2 宽、与字同高、静态不闪。
        var caret = new Rectangle
        {
            Width = 2,
            Height = FontSize,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        caret.SetResourceReference(Shape.FillProperty, "Brush.Accent");
        return caret;
    }

    /// <summary>文档里最后一段字（光标块挂在这里）；最后是表格、分隔线或什么都没有，就另起一段。</summary>
    private static Paragraph LastParagraph(BlockCollection blocks)
    {
        switch (blocks.LastBlock)
        {
            case Paragraph paragraph:
                return paragraph;
            case List { ListItems.LastListItem: { } item }:
                return LastParagraph(item.Blocks);
            case Section section when section.Blocks.Count > 0:
                return LastParagraph(section.Blocks);
            default:
                var tail = new Paragraph { Margin = new Thickness(0) };
                blocks.Add(tail);
                return tail;
        }
    }

    /// <summary>一个列表项开头的那段字（任务框插在这里）；开头不是字就补一段。</summary>
    private static Paragraph LeadingParagraph(BlockCollection blocks)
    {
        if (blocks.FirstBlock is Paragraph paragraph)
        {
            return paragraph;
        }

        var lead = new Paragraph { Margin = new Thickness(0, 0, 0, ItemGap) };
        if (blocks.FirstBlock is { } first)
        {
            blocks.InsertBefore(first, lead);
        }
        else
        {
            blocks.Add(lead);
        }

        return lead;
    }

    /// <summary>
    /// 只放行网页与邮件（<see cref="MarkdownText.IsOpenable"/>）：模型写出来的链接，点了就交给
    /// 系统浏览器；打不开要有说法——日志留一条，窗口不碎（同窄条的打开链接，O-24）。
    /// </summary>
    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri is not { } uri || !MarkdownText.IsOpenable(uri.AbsoluteUri))
        {
            return;
        }

        try
        {
            // 打开即弃（O-43）。
            using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.OpenLinkFailed, failure, ("markdown", 1));
        }
    }

    /// <summary>值 × 参数（double）。NaN 进 NaN 出——没设行高时仍是"自动"。</summary>
    private sealed class ScaleConverter : IValueConverter
    {
        public static readonly ScaleConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double number && parameter is double factor ? number * factor : DependencyProperty.UnsetValue;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
