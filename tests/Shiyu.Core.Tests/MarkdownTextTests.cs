using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 大模型输出按 Markdown 显示（用户需求 2026-10-10，ADR-0014）：解析成固定的块树，规则在
/// <see cref="MarkdownText"/> 一处定。这里钉住模型最常写的那些形状，和几条刻意的取舍——
/// 换行照原样、缩进不当代码、HTML 当字、流式半截照样读得出。
/// </summary>
public class MarkdownTextTests
{
    private static string Text(IReadOnlyList<MdInline> inlines)
        => string.Concat(inlines.Select(inline => inline switch
        {
            MdText text => text.Text,
            MdCode code => $"`{code.Code}`",
            MdStrong strong => $"<b>{Text(strong.Inlines)}</b>",
            MdEmphasis emphasis => $"<i>{Text(emphasis.Inlines)}</i>",
            MdStrike strike => $"<s>{Text(strike.Inlines)}</s>",
            MdLink link => $"[{Text(link.Inlines)}]({link.Url})",
            MdLineBreak => "\n",
            _ => "?",
        }));

    [Fact]
    public void Empty_text_has_no_blocks()
    {
        Assert.Empty(MarkdownText.Parse(string.Empty));
        Assert.Empty(MarkdownText.Parse("   \n\n "));
    }

    [Fact]
    public void Plain_prose_is_one_paragraph_of_text()
    {
        var block = Assert.Single(MarkdownText.Parse("Hello, world. 你好。"));
        var paragraph = Assert.IsType<MdParagraph>(block);
        Assert.Equal("Hello, world. 你好。", Text(paragraph.Inlines));
    }

    [Fact]
    public void A_single_newline_stays_a_line_break()
    {
        // 模型与译文的分行是内容：两行不能并成一行。
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse("第一行\n第二行")));
        Assert.Equal("第一行\n第二行", Text(paragraph.Inlines));
    }

    [Fact]
    public void Blank_lines_separate_paragraphs()
    {
        var blocks = MarkdownText.Parse("一段。\n\n二段。");
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.IsType<MdParagraph>(block));
    }

    [Theory]
    [InlineData("# 一级", 1)]
    [InlineData("## 二级", 2)]
    [InlineData("### 三级", 3)]
    [InlineData("###### 六级", 6)]
    public void Headings_keep_their_level(string markdown, int level)
    {
        var heading = Assert.IsType<MdHeading>(Assert.Single(MarkdownText.Parse(markdown)));
        Assert.Equal(level, heading.Level);
    }

    [Fact]
    public void A_hash_without_a_space_is_not_a_heading()
    {
        Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse("#hashtag 不是标题")));
    }

    [Fact]
    public void Inline_styles_nest_the_way_they_were_written()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse(
            "**粗** 与 *斜* 与 ~~删~~ 与 `code` 与 ***都有***")));
        var text = Text(paragraph.Inlines);
        Assert.Contains("<b>粗</b>", text);
        Assert.Contains("<i>斜</i>", text);
        Assert.Contains("<s>删</s>", text);
        Assert.Contains("`code`", text);
        Assert.True(text.Contains("<b><i>都有</i></b>") || text.Contains("<i><b>都有</b></i>"), text);
    }

    [Fact]
    public void Asterisks_with_spaces_around_them_stay_literal()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse("2 * 3 * 4 = 24")));
        Assert.Equal("2 * 3 * 4 = 24", Text(paragraph.Inlines));
    }

    [Fact]
    public void Intraword_underscores_stay_literal()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse("call snake_case_name now")));
        Assert.Equal("call snake_case_name now", Text(paragraph.Inlines));
    }

    [Fact]
    public void Bullet_and_numbered_lists_keep_their_kind_and_start()
    {
        var blocks = MarkdownText.Parse("- 甲\n- 乙\n\n3. 丙\n4. 丁");
        Assert.Equal(2, blocks.Count);

        var bullets = Assert.IsType<MdList>(blocks[0]);
        Assert.False(bullets.Ordered);
        Assert.Equal(2, bullets.Items.Count);
        Assert.Equal("甲", Text(Assert.IsType<MdParagraph>(Assert.Single(bullets.Items[0].Blocks)).Inlines));

        var numbers = Assert.IsType<MdList>(blocks[1]);
        Assert.True(numbers.Ordered);
        Assert.Equal(3, numbers.Start);
        Assert.Equal(2, numbers.Items.Count);
    }

    [Fact]
    public void Nested_lists_live_inside_their_item()
    {
        var list = Assert.IsType<MdList>(Assert.Single(MarkdownText.Parse("- 外\n  - 内一\n  - 内二\n- 外二")));
        Assert.Equal(2, list.Items.Count);
        var inner = Assert.IsType<MdList>(list.Items[0].Blocks[^1]);
        Assert.Equal(2, inner.Items.Count);
    }

    [Fact]
    public void Task_items_carry_their_box()
    {
        var list = Assert.IsType<MdList>(Assert.Single(MarkdownText.Parse("- [ ] 待办\n- [x] 已办\n- 普通")));
        Assert.False(list.Items[0].Checked);
        Assert.True(list.Items[1].Checked);
        Assert.Null(list.Items[2].Checked);

        // The box itself is not text, nor is the space after it.
        Assert.Equal("待办", Text(Assert.IsType<MdParagraph>(list.Items[0].Blocks[0]).Inlines));
    }

    [Fact]
    public void Fenced_code_keeps_its_lines_and_language()
    {
        var code = Assert.IsType<MdCodeBlock>(Assert.Single(MarkdownText.Parse("```csharp\nvar a = 1;\n  var b = 2;\n```")));
        Assert.Equal("csharp", code.Language);
        Assert.Equal("var a = 1;\n  var b = 2;", code.Code);
    }

    [Fact]
    public void An_unclosed_fence_mid_stream_is_still_a_code_block()
    {
        // 流式中途：收尾的 ``` 还没来，已经到的那几行照样按代码显示。
        var code = Assert.IsType<MdCodeBlock>(Assert.Single(MarkdownText.Parse("```\nline one\nline tw")));
        Assert.Equal("line one\nline tw", code.Code);
        Assert.Null(code.Language);
    }

    [Fact]
    public void Unclosed_emphasis_mid_stream_shows_its_asterisks()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse("这是 **还没写完")));
        Assert.Equal("这是 **还没写完", Text(paragraph.Inlines));
    }

    [Fact]
    public void Indented_lines_are_not_code()
    {
        // 译文里缩进的诗行、引文不该变成等宽代码块。
        var blocks = MarkdownText.Parse("前言：\n\n    缩进的一行\n    又一行");
        Assert.DoesNotContain(blocks, block => block is MdCodeBlock);
        Assert.Contains("缩进的一行", MarkdownText.Plain("前言：\n\n    缩进的一行\n    又一行"));
    }

    [Fact]
    public void Html_shows_as_the_text_it_was()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse("第一<br>第二 <b>粗</b>")));
        Assert.Equal("第一<br>第二 <b>粗</b>", Text(paragraph.Inlines));
    }

    [Fact]
    public void Quotes_hold_blocks()
    {
        var quote = Assert.IsType<MdQuote>(Assert.Single(MarkdownText.Parse("> 引一句\n> **还有**")));
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(quote.Blocks));
        Assert.Equal("引一句\n<b>还有</b>", Text(paragraph.Inlines));
    }

    [Fact]
    public void A_dash_rule_between_paragraphs_is_a_rule()
    {
        var blocks = MarkdownText.Parse("上\n\n---\n\n下");
        Assert.Equal(3, blocks.Count);
        Assert.IsType<MdRule>(blocks[1]);
    }

    [Fact]
    public void Pipe_tables_keep_header_cells_and_alignment()
    {
        var table = Assert.IsType<MdTable>(Assert.Single(MarkdownText.Parse(
            "| 词 | 释义 | 次数 |\n|:---|:---:|---:|\n| **apple** | 苹果 | 3 |\n| pear | 梨 | 1 |")));
        Assert.Equal([MdAlign.Left, MdAlign.Center, MdAlign.Right], table.Columns);
        Assert.Equal(3, table.Rows.Count);
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal("释义", Text(table.Rows[0].Cells[1]));
        Assert.Equal("<b>apple</b>", Text(table.Rows[1].Cells[0]));
    }

    [Fact]
    public void Links_and_bare_addresses_become_links()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse(
            "看 [文档](https://example.com/doc) 或 https://example.org/x")));
        var links = paragraph.Inlines.OfType<MdLink>().ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/doc", links[0].Url);
        Assert.Equal("文档", Text(links[0].Inlines));
        Assert.Equal("https://example.org/x", links[1].Url);
    }

    [Fact]
    public void Images_show_their_alt_text_only()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse("![一张图](https://example.com/a.png)")));
        Assert.Empty(paragraph.Inlines.OfType<MdLink>());
        Assert.Equal("一张图", Text(paragraph.Inlines));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/a?b=c", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("file:///C:/Windows/system32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("C:\\temp\\a.txt", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    public void Only_web_and_mail_links_open(string url, bool openable)
    {
        Assert.Equal(openable, MarkdownText.IsOpenable(url));
    }

    [Fact]
    public void Entities_are_decoded()
    {
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(MarkdownText.Parse("A &amp; B &lt; C")));
        Assert.Equal("A & B < C", Text(paragraph.Inlines));
    }

    [Fact]
    public void Plain_strips_the_markers_and_keeps_the_shape()
    {
        var plain = MarkdownText.Plain(
            "# 标题\n\n**粗** 和 `码`\n\n- 甲\n- [x] 乙\n\n2. 丙\n3. 丁\n\n> 引\n\n```\ncode\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.Equal(
            "标题\n粗 和 码\n• 甲\n☑ 乙\n2. 丙\n3. 丁\n引\ncode\na\tb\n1\t2",
            plain);
    }

    [Fact]
    public void Plain_of_plain_prose_is_the_prose()
    {
        Assert.Equal("Just words.\nOn two lines.", MarkdownText.Plain("Just words.\nOn two lines."));
    }
}
