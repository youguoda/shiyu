using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Shiyu.Core;

/// <summary>一块：段落、标题、代码块、引用、列表、分隔线或表格。</summary>
public abstract record MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <param name="Level">1–6。</param>
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <param name="Language">围栏后的语言名；没写为 null。</param>
public sealed record MdCodeBlock(string Code, string? Language) : MdBlock;

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

/// <param name="Start">有序列表的起始编号；无序列表为 1。</param>
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock;

/// <param name="Checked">任务项（- [ ] / - [x]）的勾选状态；普通项为 null。</param>
public sealed record MdListItem(IReadOnlyList<MdBlock> Blocks, bool? Checked);

/// <summary>分隔线（---）。</summary>
public sealed record MdRule : MdBlock;

/// <param name="Columns">每列的对齐；表头行在 <paramref name="Rows"/> 里，IsHeader 为真。</param>
public sealed record MdTable(IReadOnlyList<MdAlign> Columns, IReadOnlyList<MdTableRow> Rows) : MdBlock;

public sealed record MdTableRow(bool IsHeader, IReadOnlyList<IReadOnlyList<MdInline>> Cells);

public enum MdAlign
{
    Left,
    Center,
    Right,
}

/// <summary>行内：文字、加粗、斜体、删除线、行内代码、链接、换行。</summary>
public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdStrong(IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdEmphasis(IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdStrike(IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdCode(string Code) : MdInline;

/// <param name="Url">原样的地址；能不能点由界面按 <see cref="MarkdownText.IsOpenable"/> 决定。</param>
public sealed record MdLink(string Url, IReadOnlyList<MdInline> Inlines) : MdInline;

public sealed record MdLineBreak : MdInline;

/// <summary>
/// 把大模型输出的 Markdown 读成一棵小而固定的块树（用户需求 2026-10-10：翻译框与反向输入框的
/// 输出按 Markdown 显示；ADR-0014）。解析交给 Markdig，界面只认这里的几种块与行内——渲染器
/// 不碰 Markdig，哪些语法认、哪些不认在这一处定、在这一处测：
///
/// - 认：标题、段落、加粗/斜体/删除线、行内代码、围栏代码块、引用、有序/无序/任务列表、分隔线、
///   管道表格、链接与裸网址；
/// - 换行照原样换：模型（和翻译）的分行是内容，不是排版噪音——单个换行不并成空格；
/// - 不认缩进代码块：译文里缩进的诗行、引文不该变成等宽代码；
/// - 不认 HTML：&lt;br&gt; 之类原样显示成字，界面不解释标签；
/// - 不完整也读得出：流式中途的 Markdown（没收尾的 ** 或 ```）照常给出一棵树，收尾时自然变好。
///
/// 复制、贴回、存历史一律用原文（Markdown 本身），这里只管"看"。
/// </summary>
public static class MarkdownText
{
    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

    private static MarkdownPipeline BuildPipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
            .UseTaskLists()
            .UseAutoLinks()
            .DisableHtml();
        builder.BlockParsers.TryRemove<IndentedCodeBlockParser>();
        return builder.Build();
    }

    public static IReadOnlyList<MdBlock> Parse(string markdown)
        => string.IsNullOrEmpty(markdown) ? [] : Blocks(Markdown.Parse(markdown, Pipeline));

    /// <summary>
    /// 去掉标记的纯文字：块与块之间一个换行，列表项前加 "• " 或 "1. "，表格一行一行、格间
    /// 用制表符。给只放得下几行的地方用（翻译记录收起时的预览）。
    /// </summary>
    public static string Plain(string markdown)
    {
        var text = new StringBuilder();
        AppendPlain(text, Parse(markdown), indent: string.Empty);
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>界面上能点开的链接只有网页与邮件；别的（file:、javascript: 之类）只显示不打开。</summary>
    public static bool IsOpenable(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);

    // --- Markdig → 块树 ------------------------------------------------------------------

    private static List<MdBlock> Blocks(ContainerBlock container)
    {
        var blocks = new List<MdBlock>();
        foreach (var block in container)
        {
            if (Block(block) is { } converted)
            {
                blocks.Add(converted);
            }
        }

        return blocks;
    }

    private static MdBlock? Block(Block block) => block switch
    {
        HeadingBlock heading => new MdHeading(Math.Clamp(heading.Level, 1, 6), Inlines(heading.Inline)),
        ParagraphBlock paragraph => new MdParagraph(Inlines(paragraph.Inline)),
        FencedCodeBlock fenced => new MdCodeBlock(
            fenced.Lines.ToString(),
            string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info.Trim()),
        CodeBlock code => new MdCodeBlock(code.Lines.ToString(), null),
        QuoteBlock quote => new MdQuote(Blocks(quote)),
        ListBlock list => List(list),
        ThematicBreakBlock => new MdRule(),
        Table table => Table(table),

        // Reference definitions and the like carry nothing to show.
        _ => null,
    };

    private static MdList List(ListBlock list)
    {
        var start = list.IsOrdered
            && int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : 1;

        var items = new List<MdListItem>();
        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            // A task item's box is the first inline of its first paragraph.
            bool? done = null;
            if (item.Count > 0
                && item[0] is ParagraphBlock { Inline.FirstChild: TaskList task })
            {
                done = task.Checked;
            }

            items.Add(new MdListItem(Blocks(item), done));
        }

        return new MdList(list.IsOrdered, start, items);
    }

    private static MdTable Table(Table table)
    {
        var columns = table.ColumnDefinitions
            .Select(column => column.Alignment switch
            {
                TableColumnAlign.Center => MdAlign.Center,
                TableColumnAlign.Right => MdAlign.Right,
                _ => MdAlign.Left,
            })
            .ToList();

        var rows = new List<MdTableRow>();
        foreach (var child in table)
        {
            if (child is not TableRow row)
            {
                continue;
            }

            var cells = new List<IReadOnlyList<MdInline>>();
            foreach (var cell in row.OfType<TableCell>())
            {
                // A cell holds paragraphs; a pipe table only ever puts one there.
                var inlines = new List<MdInline>();
                foreach (var paragraph in cell.OfType<ParagraphBlock>())
                {
                    if (inlines.Count > 0)
                    {
                        inlines.Add(new MdLineBreak());
                    }

                    inlines.AddRange(Inlines(paragraph.Inline));
                }

                cells.Add(inlines);
            }

            rows.Add(new MdTableRow(row.IsHeader, cells));
        }

        // The separator row can define fewer columns than the widest row.
        var widest = rows.Count == 0 ? 0 : rows.Max(row => row.Cells.Count);
        while (columns.Count < widest)
        {
            columns.Add(MdAlign.Left);
        }

        return new MdTable(columns, rows);
    }

    private static List<MdInline> Inlines(ContainerInline? container)
    {
        var inlines = new List<MdInline>();
        if (container is null)
        {
            return inlines;
        }

        var afterTaskBox = false;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case TaskList:
                    // Read into MdListItem.Checked by the list; the space the
                    // writer put after "[x]" goes with the box.
                    afterTaskBox = true;
                    continue;
                case LiteralInline literal:
                    var content = literal.Content.ToString();
                    AddText(inlines, afterTaskBox ? content.TrimStart() : content);
                    break;
                case HtmlEntityInline entity:
                    AddText(inlines, entity.Transcoded.ToString());
                    break;
                case CodeInline code:
                    inlines.Add(new MdCode(code.Content));
                    break;
                case LineBreakInline:
                    // Soft or hard, a line break in the output is a line break on screen.
                    inlines.Add(new MdLineBreak());
                    break;
                case AutolinkInline autolink:
                    inlines.Add(new MdLink(
                        autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url,
                        [new MdText(autolink.Url)]));
                    break;
                case LinkInline { IsImage: true } image:
                    // No pictures fetched from the network: the alt text stands in.
                    inlines.AddRange(Inlines(image));
                    break;
                case LinkInline link:
                    inlines.Add(new MdLink(link.Url ?? string.Empty, Inlines(link)));
                    break;
                case EmphasisInline emphasis:
                    inlines.Add(Emphasis(emphasis));
                    break;
                case ContainerInline nested:
                    inlines.AddRange(Inlines(nested));
                    break;
                default:
                    // Anything the pipeline produces beyond the above (raw HTML is
                    // already plain text with DisableHtml) shows as what it was typed.
                    AddText(inlines, inline.ToString() ?? string.Empty);
                    break;
            }

            afterTaskBox = false;
        }

        return inlines;
    }

    private static MdInline Emphasis(EmphasisInline emphasis)
    {
        var children = Inlines(emphasis);
        return emphasis.DelimiterChar == '~'
            ? new MdStrike(children)
            : emphasis.DelimiterCount >= 2
                ? new MdStrong(children)
                : new MdEmphasis(children);
    }

    /// <summary>相邻的文字并成一段：Markdig 会把一行字切成好几个 literal。</summary>
    private static void AddText(List<MdInline> inlines, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (inlines.Count > 0 && inlines[^1] is MdText previous)
        {
            inlines[^1] = new MdText(previous.Text + text);
        }
        else
        {
            inlines.Add(new MdText(text));
        }
    }

    // --- 块树 → 纯文字 -------------------------------------------------------------------

    private static void AppendPlain(StringBuilder text, IReadOnlyList<MdBlock> blocks, string indent)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MdParagraph paragraph:
                    text.Append(indent).Append(PlainInlines(paragraph.Inlines)).Append('\n');
                    break;
                case MdHeading heading:
                    text.Append(indent).Append(PlainInlines(heading.Inlines)).Append('\n');
                    break;
                case MdCodeBlock code:
                    foreach (var line in code.Code.Split('\n'))
                    {
                        text.Append(indent).Append(line).Append('\n');
                    }

                    break;
                case MdQuote quote:
                    AppendPlain(text, quote.Blocks, indent);
                    break;
                case MdList list:
                    var number = list.Start;
                    foreach (var item in list.Items)
                    {
                        var marker = item.Checked switch
                        {
                            true => "☑ ",
                            false => "☐ ",
                            null => list.Ordered ? $"{number}. " : "• ",
                        };
                        number++;

                        // The marker rides on the item's first line; the rest indent under it.
                        var itemText = new StringBuilder();
                        AppendPlain(itemText, item.Blocks, string.Empty);
                        var lines = itemText.ToString().TrimEnd('\n').Split('\n');
                        text.Append(indent).Append(marker).Append(lines[0]).Append('\n');
                        foreach (var line in lines.Skip(1))
                        {
                            text.Append(indent).Append("  ").Append(line).Append('\n');
                        }
                    }

                    break;
                case MdTable table:
                    foreach (var row in table.Rows)
                    {
                        text.Append(indent).Append(string.Join('\t', row.Cells.Select(PlainInlines))).Append('\n');
                    }

                    break;
                case MdRule:
                    break;
            }
        }
    }

    private static string PlainInlines(IReadOnlyList<MdInline> inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            text.Append(inline switch
            {
                MdText plain => plain.Text,
                MdCode code => code.Code,
                MdLineBreak => "\n",
                MdStrong strong => PlainInlines(strong.Inlines),
                MdEmphasis emphasis => PlainInlines(emphasis.Inlines),
                MdStrike strike => PlainInlines(strike.Inlines),
                MdLink link => PlainInlines(link.Inlines),
                _ => string.Empty,
            });
        }

        return text.ToString();
    }
}
