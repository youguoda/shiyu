using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 剪贴板内容是不可信输入（测试调研 P0-3）：恶意构造的 Markdown 与文本要能
/// 完整走完解析与展示，且界面上可点开的链接只许网页与邮件——javascript:、
/// file: 之类的 scheme 只显示不打开（MarkdownText.IsOpenable 白名单）。
/// </summary>
public class MalContentTests
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/config")]
    [InlineData("vbscript:msgbox")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    public void Dangerous_link_schemes_are_never_openable(string url)
    {
        Assert.False(MarkdownText.IsOpenable(url));
    }

    [Theory]
    [InlineData("https://example.invalid/page")]
    [InlineData("http://example.invalid/")]
    [InlineData("mailto:someone@example.invalid")]
    public void Web_and_mail_links_stay_openable(string url)
    {
        Assert.True(MarkdownText.IsOpenable(url));
    }

    [Theory]
    [InlineData("[点击领取](javascript:alert(1))")]
    [InlineData("[打开文件](file:///C:/Windows/System32/config)")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("[[https://example.invalid][nested-brackets]]")]
    public void Hostile_markdown_parses_without_throwing(string markdown)
    {
        var blocks = MarkdownText.Parse(markdown);

        Assert.NotNull(blocks);
    }

    [Fact]
    public void A_megabyte_of_markdown_parses_and_plains_without_throwing()
    {
        var markdown = string.Concat(Enumerable.Repeat("# 标题\n\n[链接](https://example.invalid/x) 与 **加粗**。\n\n", 12000));

        var blocks = MarkdownText.Parse(markdown);
        var plain = MarkdownText.Plain(markdown);

        Assert.NotEmpty(blocks);
        Assert.NotEmpty(plain);
    }

    [Theory]
    [InlineData("E:\\Project\\..\\..\\nonexistent-target\\passwd")]
    [InlineData("../../../../etc/passwd-does-not-exist")]
    public void Path_traversal_strings_that_go_nowhere_are_not_alive(string path)
    {
        // FileEntries.Alive 是纯存在性检查：指向真实文件的路径（哪怕是系统文件）
        // 返回 true 是对的；调用方只拿它决定「打开/定位」按钮的可用性，没有执行面。
        Assert.False(FileEntries.Alive(path));
    }

    [Fact]
    public void Control_characters_and_null_bytes_roundtrip_through_the_store()
    {
        var file = Path.Combine(Path.GetTempPath(), $"shiyu-malcontent-{Guid.NewGuid():N}.db");
        using var store = EntryStore.Open(file);
        var hostile = "retention\u0000null\n\r\t tab 控制字符混排";
        var entry = store.Append(hostile, "probe", DateTimeOffset.UtcNow);

        var read = store.Search("null", limit: 10);

        Assert.Equal(entry.Id, Assert.Single(read).Id);
    }
}
