using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>识别结果的拼法（评审 2026-10-16 F-07）：中文不留字间空格，西文照常隔开。</summary>
public class OcrTextTests
{
    [Fact]
    public void Chinese_characters_come_back_without_the_spaces_between_them()
        => Assert.Equal("剪贴板历史", OcrText.JoinWords(["剪", "贴", "板", "历", "史"]));

    [Fact]
    public void Latin_words_keep_their_spaces()
        => Assert.Equal("Copy the text", OcrText.JoinWords(["Copy", "the", "text"]));

    [Fact]
    public void Mixed_lines_space_only_between_latin_words()
    {
        Assert.Equal("打开Settings页面", OcrText.JoinWords(["打", "开", "Settings", "页", "面"]));
        Assert.Equal("版本v0.16.1已发布，详见CHANGELOG", OcrText.JoinWords(["版", "本", "v0.16.1", "已", "发", "布", "，", "详", "见", "CHANGELOG"]));
    }

    [Fact]
    public void Lines_are_joined_one_per_line_and_blank_ones_dropped()
        => Assert.Equal("第一行\nsecond line", OcrText.JoinLines(["  第一行 ", "", "second line"]));
}
