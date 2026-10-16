using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class RetentionTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path_ = Path.Combine(Path.GetTempPath(), "shiyu-retention", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path_);
        }

        public string Path_ { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path_, recursive: true); }
            catch (IOException) { }
        }
    }

    private static Entry SaveImage(EntryStore store, ImageArchive archive, DateTimeOffset at)
    {
        var path = archive.Save([1, 2, 3], at);
        return store.AppendImage($"图片 {at:HHmmss}", [9], path, "test", at);
    }

    [Fact]
    public void Originals_past_the_retention_period_are_deleted()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);

        var old = SaveImage(store, archive, Noon.AddDays(-40));

        var result = new RetentionService(store, archive, new TestClock(Noon)).Sweep(TimeSpan.FromDays(30));

        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(old.OriginalPath!));
    }

    [Fact]
    public void The_entry_and_its_thumbnail_survive_the_cleanup()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);
        SaveImage(store, archive, Noon.AddDays(-40));

        new RetentionService(store, archive, new TestClock(Noon)).Sweep(TimeSpan.FromDays(30));

        // This is the whole reason thumbnails live in the database: the history
        // stays complete and explicable after the originals are gone. The list
        // row is narrow (O-22); the payload read goes through Get.
        var entry = Assert.Single(store.Page(limit: 10));
        Assert.Equal(EntryKind.Image, entry.Kind);
        Assert.Equal(new byte[] { 9 }, store.Get(entry.Id)!.ThumbnailPng);
        Assert.False(entry.HasOriginal);
        Assert.Null(entry.OriginalPath);
    }

    [Fact]
    public void Recent_originals_are_left_alone()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);
        var recent = SaveImage(store, archive, Noon.AddDays(-3));

        var result = new RetentionService(store, archive, new TestClock(Noon)).Sweep(TimeSpan.FromDays(30));

        Assert.Equal(0, result.Total);
        Assert.True(File.Exists(recent.OriginalPath!));
    }

    [Fact]
    public void Text_entries_are_never_cleaned_up_however_old()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);
        store.Append("something copied years ago", "test", Noon.AddYears(-3));

        new RetentionService(store, archive, new TestClock(Noon)).Sweep(TimeSpan.FromDays(30));

        // Text is tiny and is exactly what the user goes looking for months
        // later. Retention has no business touching it.
        var entry = Assert.Single(store.Page(limit: 10));
        Assert.Equal("something copied years ago", entry.Text);
    }

    [Fact]
    public void Shortening_the_retention_period_applies_to_what_is_already_stored()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);
        var image = SaveImage(store, archive, Noon.AddDays(-10));
        var service = new RetentionService(store, archive, new TestClock(Noon));

        Assert.Equal(0, service.Sweep(TimeSpan.FromDays(30)).Total);
        Assert.True(File.Exists(image.OriginalPath!));

        Assert.Equal(1, service.Sweep(TimeSpan.FromDays(7)).Removed);
        Assert.False(File.Exists(image.OriginalPath!));
    }

    [Fact]
    public void A_long_unused_installation_clears_its_whole_backlog()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);

        // More than one batch, so the loop that works through a backlog is
        // actually exercised rather than assumed.
        for (var i = 0; i < 250; i++)
        {
            SaveImage(store, archive, Noon.AddDays(-100).AddSeconds(i));
        }

        var result = new RetentionService(store, archive, new TestClock(Noon)).Sweep(TimeSpan.FromDays(30));

        Assert.Equal(250, result.Removed);
        Assert.Empty(Directory.GetFiles(folder.Path_));
        Assert.Equal(250, store.Count());
    }

    [Fact]
    public void An_interrupted_sweep_leaves_no_entry_pointing_at_a_missing_file()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);
        var image = SaveImage(store, archive, Noon.AddDays(-40));

        // Exactly the state a crash between the two steps would leave behind:
        // the file gone, the entry still claiming to have it.
        File.Delete(image.OriginalPath!);

        var result = new RetentionService(store, archive, new TestClock(Noon)).Sweep(TimeSpan.FromDays(30));

        Assert.Equal(1, result.Reclaimed);
        Assert.Null(Assert.Single(store.Page(limit: 10)).OriginalPath);
    }

    [Fact]
    public void Sweeping_twice_is_harmless()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);
        SaveImage(store, archive, Noon.AddDays(-40));
        var service = new RetentionService(store, archive, new TestClock(Noon));

        service.Sweep(TimeSpan.FromDays(30));
        var second = service.Sweep(TimeSpan.FromDays(30));

        Assert.Equal(0, second.Total);
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void An_empty_history_sweeps_to_nothing()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);

        var result = new RetentionService(store, new ImageArchive(folder.Path_), new TestClock(Noon))
            .Sweep(TimeSpan.FromDays(30));

        Assert.Equal(0, result.Total);
    }

    // ── 「历史保留」（评审 2026-10-16 F-13b）：用户选了周期，整条删 ──

    [Fact]
    public void A_history_period_removes_old_entries_of_every_kind_and_their_originals()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);
        store.Append("old text", "test", Noon.AddDays(-40));
        store.AppendFiles([@"C:\old\report.docx"], "test", Noon.AddDays(-40));
        var image = SaveImage(store, archive, Noon.AddDays(-40));

        var removed = new RetentionService(store, archive, new TestClock(Noon)).SweepHistory(30);

        Assert.Equal(3, removed);
        Assert.Equal(0, store.Count());
        Assert.False(File.Exists(image.OriginalPath!));
    }

    [Fact]
    public void A_history_period_leaves_entries_inside_it_alone()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);
        store.Append("last week", "test", Noon.AddDays(-7));
        var image = SaveImage(store, archive, Noon.AddDays(-29));

        var removed = new RetentionService(store, archive, new TestClock(Noon)).SweepHistory(30);

        Assert.Equal(0, removed);
        Assert.Equal(2, store.Count());
        Assert.True(File.Exists(image.OriginalPath!));
    }

    [Fact]
    public void A_history_period_keeps_what_deletion_protection_covers()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var archive = new ImageArchive(folder.Path_);
        var starred = store.Append("starred", "test", Noon.AddDays(-90));
        store.SetFavorite(starred.Id, true);
        var pinned = SaveImage(store, archive, Noon.AddDays(-90));
        store.SetPinned(pinned.Id, true);
        store.Append("plain", "test", Noon.AddDays(-90));

        var removed = new RetentionService(store, archive, new TestClock(Noon))
            .SweepHistory(30, keepFavorites: true, keepPinned: true);

        // 与批量删除同一个承诺：收藏与置顶在保护开着时不跟着周期走，钉住的图连原图也留着。
        Assert.Equal(1, removed);
        var survivors = store.Recent(limit: 10).Select(entry => entry.Id).Order().ToList();
        Assert.Equal([starred.Id, pinned.Id], survivors);
        Assert.True(File.Exists(pinned.OriginalPath!));
    }

    [Fact]
    public void Without_protection_a_history_period_takes_favourites_too()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        var starred = store.Append("starred", "test", Noon.AddDays(-90));
        store.SetFavorite(starred.Id, true);

        var removed = new RetentionService(store, new ImageArchive(folder.Path_), new TestClock(Noon))
            .SweepHistory(30);

        Assert.Equal(1, removed);
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void Never_is_the_default_and_keeps_everything()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        store.Append("something copied years ago", "test", Noon.AddYears(-3));

        var removed = new RetentionService(store, new ImageArchive(folder.Path_), new TestClock(Noon))
            .SweepHistory(new AppSettings().HistoryRetentionDays);

        Assert.Equal(0, removed);
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void A_hand_edited_period_off_the_choices_rounds_toward_keeping_more()
    {
        using var database = new TempDatabase();
        using var folder = new TempFolder();
        using var store = EntryStore.Open(database.FilePath);
        store.Append("twenty days old", "test", Noon.AddDays(-20));

        // 设置页显示的是「30 天」，就按 30 天删——不替用户提前删。
        var removed = new RetentionService(store, new ImageArchive(folder.Path_), new TestClock(Noon))
            .SweepHistory(14);

        Assert.Equal(0, removed);
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void The_history_period_round_trips_through_the_settings_file()
    {
        Assert.True(AppSettings.TryParse("{}", out var fresh));
        Assert.Equal(0, fresh.HistoryRetentionDays);

        Assert.True(AppSettings.TryParse(
            System.Text.Json.JsonSerializer.Serialize(new AppSettings { HistoryRetentionDays = 7 }),
            out var changed));
        Assert.Equal(7, changed.HistoryRetentionDays);
    }
}
