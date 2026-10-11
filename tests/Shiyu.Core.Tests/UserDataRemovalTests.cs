using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 卸载时「同时删除数据」（ADR-0015）：默认位置整个删；用户挪去的数据位置只删拾语认得的，
/// 别的文件一个不碰。全在临时目录里演，从不碰真实的 %LOCALAPPDATA%\Shiyu。
/// </summary>
public sealed class UserDataRemovalTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "shiyu-removal-" + Guid.NewGuid().ToString("N"));

    private string DefaultRoot => Path.Combine(_sandbox, "LocalAppData", "Shiyu");

    private string Moved => Path.Combine(_sandbox, "D", "MyStuff");

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { }
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void The_default_location_goes_entirely()
    {
        Touch(Path.Combine(DefaultRoot, "settings.json"));
        Touch(Path.Combine(DefaultRoot, "settings.json.bad-20261010-120000"));
        Touch(Path.Combine(DefaultRoot, "history.db"));
        Touch(Path.Combine(DefaultRoot, "images", "anything.png"));
        Touch(Path.Combine(DefaultRoot, "logs", "shiyu-20261010.log"));

        var failed = UserDataRemoval.Remove(DefaultRoot, movedDirectory: null);

        Assert.Empty(failed);
        Assert.False(Directory.Exists(DefaultRoot));
    }

    [Fact]
    public void A_moved_location_loses_only_what_shiyu_put_there()
    {
        Touch(Path.Combine(DefaultRoot, "settings.json"));
        var shiyu = new[]
        {
            Touch(Path.Combine(Moved, "history.db")),
            Touch(Path.Combine(Moved, "history.db-wal")),
            Touch(Path.Combine(Moved, "hints.json")),
            Touch(Path.Combine(Moved, "images", "20261010-123456-a1b2c3.png")),
            Touch(Path.Combine(Moved, "logs", "shiyu-20261010.log")),
            Touch(Path.Combine(Moved, "updates", "pending.json")),
            Touch(Path.Combine(Moved, "updates", "staged", "Shiyu.App.exe")),
        };
        var theirs = new[]
        {
            Touch(Path.Combine(Moved, "notes.txt")),
            Touch(Path.Combine(Moved, "images", "holiday.png")),
            Touch(Path.Combine(Moved, "logs", "other-app.log")),
            Touch(Path.Combine(Moved, "updates", "something-else.bin")),
        };

        var failed = UserDataRemoval.Remove(DefaultRoot, Moved);

        Assert.Empty(failed);
        Assert.False(Directory.Exists(DefaultRoot));
        Assert.All(shiyu, path => Assert.False(File.Exists(path), path));
        Assert.All(theirs, path => Assert.True(File.Exists(path), path));
        Assert.False(Directory.Exists(Path.Combine(Moved, "updates", "staged")));
    }

    [Fact]
    public void A_moved_location_that_held_only_shiyu_data_goes_too()
    {
        Touch(Path.Combine(Moved, "history.db"));
        Touch(Path.Combine(Moved, "images", "20261010-123456-a1b2c3.png"));
        Touch(Path.Combine(Moved, "logs", "shiyu-20261010.log"));
        Touch(Path.Combine(Moved, "updates", "installer.zip"));

        var failed = UserDataRemoval.Remove(DefaultRoot, Moved);

        Assert.Empty(failed);
        Assert.False(Directory.Exists(Moved));
        Assert.True(Directory.Exists(Path.GetDirectoryName(Moved)));
    }

    [Fact]
    public void A_moved_location_inside_the_default_one_goes_with_it()
    {
        var inside = Path.Combine(DefaultRoot, "data");
        Touch(Path.Combine(inside, "history.db"));
        Touch(Path.Combine(inside, "unrecognised.txt"));

        var failed = UserDataRemoval.Remove(DefaultRoot, inside);

        Assert.Empty(failed);
        Assert.False(Directory.Exists(DefaultRoot));
    }

    [Fact]
    public void Nothing_there_is_nothing_to_report()
    {
        Assert.Empty(UserDataRemoval.Remove(DefaultRoot, Moved));
        Assert.Empty(UserDataRemoval.Remove(DefaultRoot, "   "));
    }

    [Fact]
    public void A_file_in_use_is_reported_and_the_rest_still_goes()
    {
        var locked = Touch(Path.Combine(Moved, "history.db"));
        var other = Touch(Path.Combine(Moved, "hints.json"));

        IReadOnlyList<string> failed;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = UserDataRemoval.Remove(DefaultRoot, Moved);
        }

        Assert.Contains(locked, failed);
        Assert.False(File.Exists(other));
        Assert.True(Directory.Exists(Moved));
    }
}
