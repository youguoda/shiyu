using Shiyu.Core;
using Shiyu.Core.Tests.Fakes;

namespace Shiyu.Core.Tests;

public class ContentClassifierTests
{
    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    [InlineData("Could you send me the revised figures before Friday?")]
    [InlineData("Hello")]
    [InlineData("This release fixes a long-standing issue with clipboard history.")]
    public void English_prose_is_natural_language(string text)
        => Assert.Equal(ContentKind.NaturalLanguage, ContentClassifier.Classify(text));

    [Theory]
    [InlineData("const total = items.reduce((sum, item) => sum + item.price, 0);")]
    [InlineData("public void Dispose() { _connection.Dispose(); }")]
    [InlineData("def main():\n    print(\"hello\")")]
    [InlineData("import os")]
    [InlineData("if (x != y) { return false; }")]
    [InlineData("<div class=\"header\"></div>")]
    public void Code_is_not_offered_for_translation(string text)
        => Assert.Equal(ContentKind.Code, ContentClassifier.Classify(text));

    [Theory]
    [InlineData("https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats")]
    [InlineData("http://example.com")]
    [InlineData("www.example.com")]
    public void Urls_are_recognised(string text)
        => Assert.Equal(ContentKind.Url, ContentClassifier.Classify(text));

    [Theory]
    [InlineData(@"C:\Users\someone\Documents\report.docx")]
    [InlineData(@"\\fileserver\share\thing.txt")]
    [InlineData("./src/main.ts")]
    [InlineData("/usr/local/bin/dotnet")]
    public void File_paths_are_recognised(string text)
        => Assert.Equal(ContentKind.FilePath, ContentClassifier.Classify(text));

    [Theory]
    [InlineData("1234567890")]
    [InlineData("+86 138 0000 0000")]
    [InlineData("2026-09-22")]
    [InlineData("￥1,280.00")]
    public void Numbers_and_symbols_have_nothing_to_translate(string text)
        => Assert.Equal(ContentKind.Mechanical, ContentClassifier.Classify(text));

    [Theory]
    [InlineData("这是一段中文，没有翻译的必要。")]
    [InlineData("拾语")]
    [InlineData("把这段文字复制到剪贴板")]
    public void The_users_own_language_needs_no_badge(string text)
        => Assert.Equal(ContentKind.NativeLanguage, ContentClassifier.Classify(text));

    [Fact]
    public void Mostly_english_with_a_chinese_word_still_counts_as_readable_already()
    {
        // The point of the badge is helping with text the user cannot read.
        // Enough of their own language in it and they plainly can.
        Assert.Equal(
            ContentKind.NativeLanguage,
            ContentClassifier.Classify("请 review 一下这个 pull request"));
    }

    [Fact]
    public void Empty_and_whitespace_are_not_natural_language()
    {
        Assert.Equal(ContentKind.Mechanical, ContentClassifier.Classify(""));
        Assert.Equal(ContentKind.Mechanical, ContentClassifier.Classify("   \n\t "));
    }

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog.", true)]
    [InlineData("const total = items.reduce((s, i) => s + i, 0);", false)]
    [InlineData("https://example.com", false)]
    [InlineData(@"C:\Users\someone\file.txt", false)]
    [InlineData("1234567890", false)]
    [InlineData("这是一段中文", false)]
    public void The_badge_appears_only_for_text_worth_translating(string text, bool expected)
        => Assert.Equal(expected, ContentClassifier.DeservesBadge(text));
}

public class BadgeDoesNotAffectRecordingTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Everything_is_recorded_whatever_the_classifier_thinks_of_it()
    {
        using var database = new TempDatabase();
        var clipboard = new FakeClipboardMonitor();
        using var store = EntryStore.Open(database.FilePath);
        using var pipeline = new ClipboardPipeline(
            clipboard, store, new TestClock(Noon), new ExclusionPolicy());

        // Every one of these is filtered out of the badge. Not one of them may
        // be filtered out of the history: a gap the user cannot explain is far
        // worse than a badge that did not appear.
        var filtered = new[]
        {
            "const x = 1;",
            "https://example.com",
            @"C:\Users\someone\file.txt",
            "1234567890",
            "这是一段中文",
        };

        foreach (var text in filtered)
        {
            Assert.False(ContentClassifier.DeservesBadge(text));
            clipboard.Emit(text);
        }

        Assert.Equal(filtered.Length, store.Count());
    }
}

public class BadgeOfferTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static (FakeClipboardMonitor Clipboard, EntryStore Store, ClipboardPipeline Pipeline, List<string> Offers)
        Build(TempDatabase database, ExclusionPolicy exclusions)
    {
        var clipboard = new FakeClipboardMonitor();
        var store = EntryStore.Open(database.FilePath);
        var pipeline = new ClipboardPipeline(clipboard, store, new TestClock(Noon), exclusions);
        var offers = new List<string>();
        pipeline.BadgeDeserved += offers.Add;
        return (clipboard, store, pipeline, offers);
    }

    [Fact]
    public void English_prose_offers_a_badge()
    {
        using var database = new TempDatabase();
        var (clipboard, store, pipeline, offers) = Build(database, new ExclusionPolicy());
        using var _ = store;
        using var __ = pipeline;

        clipboard.Emit("Could you send me the revised figures before Friday?");

        Assert.Single(offers);
    }

    [Fact]
    public void With_the_badge_switched_off_the_copy_is_recorded_and_offers_nothing()
    {
        // 「翻译徽标」关着（用户需求 2026-10-13，默认关）：外语照样入册，只是不弹「译」。
        using var database = new TempDatabase();
        var (clipboard, store, pipeline, offers) = Build(database, new ExclusionPolicy());
        using var _ = store;
        using var __ = pipeline;
        pipeline.OfferBadges = false;

        clipboard.Emit("Could you send me the revised figures before Friday?");
        clipboard.Emit("Could you send me the revised figures before Friday?");

        Assert.Empty(offers);
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void Switching_the_badge_back_on_takes_effect_from_the_next_copy()
    {
        using var database = new TempDatabase();
        var (clipboard, store, pipeline, offers) = Build(database, new ExclusionPolicy());
        using var _ = store;
        using var __ = pipeline;

        pipeline.OfferBadges = false;
        clipboard.Emit("The first sentence nobody is offered a badge for.");
        pipeline.OfferBadges = true;
        clipboard.Emit("The second sentence arrives after the switch went on.");

        Assert.Equal(["The second sentence arrives after the switch went on."], offers);
    }

    [Fact]
    public void Excluded_content_never_reaches_the_badge_either()
    {
        using var database = new TempDatabase();
        var exclusions = new ExclusionPolicy([ExclusionRule.ForSourceApp("KeePassXC")]);
        var (clipboard, store, pipeline, offers) = Build(database, exclusions);
        using var _ = store;
        using var __ = pipeline;

        // Both routes out of the history must also be routes out of the badge:
        // a badge hovering over a just-copied password would be the exact
        // opposite of what exclusion is for.
        clipboard.EmitExcluded("a perfectly translatable English sentence here");
        clipboard.Emit("another translatable English sentence", sourceApp: "KeePassXC");

        Assert.Empty(offers);
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void Filtered_content_is_recorded_but_offers_nothing()
    {
        using var database = new TempDatabase();
        var (clipboard, store, pipeline, offers) = Build(database, new ExclusionPolicy());
        using var _ = store;
        using var __ = pipeline;

        clipboard.Emit("const x = 1;");

        Assert.Empty(offers);
        Assert.Equal(1, store.Count());
    }
}
