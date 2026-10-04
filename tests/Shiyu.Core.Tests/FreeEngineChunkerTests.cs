using System.Text;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 分块（票 41）：必应翻 1392 字实测 9 秒，整段等完才上屏面板会一直空等，所以
/// 按段落与句末切块、逐块渐进输出。首块小（让首字出得快），其后每块大一些。
/// 切块只切不删：块与块之间的空白（换行、缩进）原样记作分隔，拼回与原文一字不差。
/// </summary>
public class FreeEngineChunkerTests
{
    /// <summary>不含任何分隔的中文句流：每句 13 字左右，句末是全角句号。</summary>
    private static string ChineseSentences(int count)
        => string.Concat(Enumerable.Range(1, count).Select(i => $"这是第{i:D2}句话，随便写写。"));

    private static string EnglishSentences(int count)
        => string.Join(" ", Enumerable.Range(1, count).Select(i => $"Sentence number {i:D2} says something plain."));

    public static TheoryData<string> Samples => new()
    {
        "Hello world",
        ChineseSentences(60),
        EnglishSentences(40),
        string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"段落 {i}：{new string('字', 70)}。")),
        string.Join("\r\n", Enumerable.Range(1, 80).Select(i => $"line {i}")),
        "\n\n  leading blank lines, indentation\n    and a trailing newline\n",
        new string('x', 1000),
        string.Concat(Enumerable.Repeat("😀", 300)),
        "tab\tseparated\twords\tin\ta\tvery\tlong\tline " + new string('y', 500),
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Chunks_and_their_separators_rebuild_the_text_exactly(string text)
    {
        var plan = FreeEngineChunker.Split(text);

        Assert.Equal(text, plan.Rebuild());
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void No_chunk_is_blank_or_wrapped_in_whitespace(string text)
    {
        // 空白全部归分隔：发给引擎的块两头干净，引擎不会为一个换行白费一次请求。
        var plan = FreeEngineChunker.Split(text);

        Assert.All(plan.Chunks, chunk =>
        {
            Assert.NotEmpty(chunk.Text);
            Assert.Equal(chunk.Text.Trim(), chunk.Text);
        });
    }

    [Fact]
    public void Short_text_is_one_chunk_without_a_separator()
    {
        var plan = FreeEngineChunker.Split("Hello world");

        var chunk = Assert.Single(plan.Chunks);
        Assert.Equal("Hello world", chunk.Text);
        Assert.Equal(string.Empty, chunk.Separator);
        Assert.Equal(string.Empty, plan.Prefix);
    }

    [Fact]
    public void The_first_chunk_is_small_and_the_rest_are_larger()
    {
        var plan = FreeEngineChunker.Split(ChineseSentences(60));

        Assert.True(plan.Chunks.Count > 2);
        Assert.InRange(plan.Chunks[0].Text.Length, 1, FreeEngineChunker.FirstChunkChars);
        Assert.All(plan.Chunks.Skip(1), chunk => Assert.True(chunk.Text.Length <= FreeEngineChunker.ChunkChars));

        // 首块确实比后续的小：这就是"首字出得快"。
        Assert.True(plan.Chunks[0].Text.Length < plan.Chunks[1].Text.Length);
    }

    [Fact]
    public void The_two_limits_are_the_documented_constants()
    {
        // 票面常量，按探针数据调：首块 150 字，其后每块 400 字。
        Assert.Equal(150, FreeEngineChunker.FirstChunkChars);
        Assert.Equal(400, FreeEngineChunker.ChunkChars);
    }

    [Fact]
    public void A_cut_lands_on_a_sentence_end_when_the_text_has_them()
    {
        var plan = FreeEngineChunker.Split(EnglishSentences(40));

        Assert.True(plan.Chunks.Count > 2);
        foreach (var chunk in plan.Chunks.Take(plan.Chunks.Count - 1))
        {
            Assert.EndsWith(".", chunk.Text);
            Assert.Equal(" ", chunk.Separator);
        }
    }

    [Fact]
    public void Chinese_sentences_are_cut_after_the_full_stop_with_no_separator()
    {
        var plan = FreeEngineChunker.Split(ChineseSentences(60));

        foreach (var chunk in plan.Chunks.Take(plan.Chunks.Count - 1))
        {
            Assert.EndsWith("。", chunk.Text);
            Assert.Equal(string.Empty, chunk.Separator);
        }
    }

    [Fact]
    public void Paragraph_breaks_become_separators_and_stay_verbatim()
    {
        var plan = FreeEngineChunker.Split(
            "First paragraph.\n\nSecond paragraph.\r\n  Third, indented.", firstChars: 20, chunkChars: 20);

        Assert.Equal(
            [("First paragraph.", "\n\n"), ("Second paragraph.", "\r\n  "), ("Third, indented.", string.Empty)],
            plan.Chunks.Select(chunk => (chunk.Text, chunk.Separator)).ToArray());
    }

    [Fact]
    public void Short_lines_are_packed_into_one_chunk_up_to_the_limit()
    {
        // 60 条短行（实测的形状）不该变成 60 次请求：行间换行留在块内，一并发给引擎。
        var lines = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"short line {i}"));

        var plan = FreeEngineChunker.Split(lines, firstChars: 150, chunkChars: 400);

        var chunk = Assert.Single(plan.Chunks);
        Assert.Contains("\n", chunk.Text);
        Assert.Equal(lines, plan.Rebuild());
    }

    [Fact]
    public void Dotted_tokens_are_never_cut_in_the_middle()
    {
        // 句点后面不是空白的不算句末：3.14159 与 example.com 必须整个落在一块里。
        var plan = FreeEngineChunker.Split(
            "See version 3.14159 at example.com now. Then stop.", firstChars: 22, chunkChars: 22);

        Assert.Contains(plan.Chunks, chunk => chunk.Text.Contains("3.14159"));
        Assert.Contains(plan.Chunks, chunk => chunk.Text.Contains("example.com"));
        Assert.Equal("See version 3.14159 at example.com now. Then stop.", plan.Rebuild());
    }

    [Fact]
    public void A_stretch_with_no_break_at_all_is_cut_at_the_limit()
    {
        var plan = FreeEngineChunker.Split(new string('x', 1000));

        Assert.Equal(FreeEngineChunker.FirstChunkChars, plan.Chunks[0].Text.Length);
        Assert.All(plan.Chunks.Skip(1), chunk => Assert.True(chunk.Text.Length <= FreeEngineChunker.ChunkChars));
        Assert.All(plan.Chunks, chunk => Assert.Equal(string.Empty, chunk.Separator));
    }

    [Fact]
    public void A_hard_cut_never_splits_a_surrogate_pair()
    {
        // 150 个字符的限额落在一对代理项中间时，退一格。
        var text = string.Concat(Enumerable.Repeat("😀", 300));
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);

        var plan = FreeEngineChunker.Split(text, firstChars: 151, chunkChars: 151);

        Assert.All(plan.Chunks, chunk => strict.GetBytes(chunk.Text));
        Assert.Equal(text, plan.Rebuild());
    }

    [Fact]
    public void Leading_and_trailing_whitespace_stay_outside_the_chunk_text()
    {
        var plan = FreeEngineChunker.Split("\n\nHello world.\n");

        Assert.Equal("\n\n", plan.Prefix);
        var chunk = Assert.Single(plan.Chunks);
        Assert.Equal("Hello world.", chunk.Text);
        Assert.Equal("\n", chunk.Separator);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \n\t \r\n")]
    public void Whitespace_only_text_has_no_chunks_and_keeps_its_prefix(string text)
    {
        var plan = FreeEngineChunker.Split(text);

        Assert.Empty(plan.Chunks);
        Assert.Equal(text, plan.Rebuild());
    }
}
