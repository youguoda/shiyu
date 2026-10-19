using Shiyu.Core;
using Shiyu.Core.Tests.Fakes;

namespace Shiyu.Core.Tests;

/// <summary>
/// A file entry is a capped list of paths plus a label that tells the truth
/// about that cap — the truth part is the acceptance criterion.
/// </summary>
public class FileEntryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Copying_files_creates_one_file_entry()
    {
        using var database = new TempDatabase();
        var clipboard = new FakeClipboardMonitor();
        using var store = EntryStore.Open(database.FilePath);
        using var pipeline = new ClipboardPipeline(clipboard, store, new TestClock(Noon), new ExclusionPolicy());

        clipboard.EmitFiles([@"C:\docs\a.pdf", @"C:\docs\b.txt"], "explorer");

        var entry = Assert.Single(store.Recent(limit: 10));
        Assert.Equal(EntryKind.Files, entry.Kind);
        Assert.Equal(2, entry.Files.Count);
        Assert.Equal(@"C:\docs\a.pdf", entry.Files[0]);
    }

    [Fact]
    public void The_same_file_copy_twice_touches_rather_than_duplicating()
    {
        using var database = new TempDatabase();
        var clipboard = new FakeClipboardMonitor();
        using var store = EntryStore.Open(database.FilePath);
        using var pipeline = new ClipboardPipeline(clipboard, store, new TestClock(Noon), new ExclusionPolicy());

        clipboard.EmitFiles([@"C:\docs\a.pdf"], "explorer");
        clipboard.EmitFiles([@"C:\docs\a.pdf"], "explorer");

        Assert.Single(store.Recent(limit: 10));
    }

    [Fact]
    public void An_absurd_number_of_files_is_capped_and_the_label_says_so()
    {
        var many = Enumerable.Range(0, 200).Select(i => $@"C:\folder\file{i}.txt").ToArray();

        var kept = FileEntries.WithinCap(many, out var capped);

        Assert.Equal(FileEntries.Cap, kept.Count);
        Assert.True(capped);
        Assert.Contains("200", FileEntries.Label(many, capped));
    }

    [Fact]
    public void Labels_read_naturally_for_small_and_medium_copies()
    {
        Assert.Equal("a.txt", FileEntries.Label([@"C:\a.txt"], capped: false));
        Assert.Contains("a.txt", FileEntries.Label([@"C:\a.txt", @"C:\b.txt"], capped: false));
        Assert.Contains("2", FileEntries.Label([@"C:\a.txt", @"C:\b.txt"], capped: false));
    }

    [Fact]
    public void Round_trip_preserves_the_paths()
    {
        using var database = new TempDatabase();
        using var store = EntryStore.Open(database.FilePath);

        store.AppendFiles([@"C:\one\x.log", @"C:\two\y.log"], "terminal", Noon);

        var entry = Assert.Single(store.Recent(limit: 10));
        Assert.Equal([@"C:\one\x.log", @"C:\two\y.log"], entry.Files);
    }

    [Fact]
    public void A_copy_of_only_images_names_the_first_as_the_preview()
    {
        Assert.Equal(@"C:\pics\a.jpg", FileEntries.PreviewImagePath([@"C:\pics\a.jpg", @"C:\pics\b.png"]));
        Assert.Equal(@"C:\pics\only.png", FileEntries.PreviewImagePath([@"C:\pics\only.png"]));
        Assert.Equal(@"C:\pics\UPPER.JPG", FileEntries.PreviewImagePath([@"C:\pics\UPPER.JPG"]));
    }

    [Fact]
    public void A_mixed_or_empty_copy_has_no_preview()
    {
        Assert.Null(FileEntries.PreviewImagePath([@"C:\pics\a.jpg", @"C:\docs\b.pdf"]));
        Assert.Null(FileEntries.PreviewImagePath([@"C:\a.txt"]));
        Assert.Null(FileEntries.PreviewImagePath([]));
    }

    // ── 文件与目录都算「在」（用户实录 2026-10-19：文件夹的打开/定位没反应）──

    [Fact]
    public void Alive_counts_directories_as_well_as_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), "shiyu-alive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "a.txt");
            File.WriteAllText(file, "x");

            Assert.True(FileEntries.Alive(folder));
            Assert.True(FileEntries.Alive(file));
            Assert.False(FileEntries.Alive(Path.Combine(folder, "missing.txt")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void FirstAlive_returns_the_first_survivor_and_null_when_all_are_gone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "shiyu-alive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "a.txt");
            File.WriteAllText(file, "x");

            // 文件夹排在后面也按列表顺序取第一个活着的。
            Assert.Equal(file, FileEntries.FirstAlive([@"C:\gone\z.txt", file, folder]));
            Assert.Null(FileEntries.FirstAlive([@"C:\gone\z.txt"]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
