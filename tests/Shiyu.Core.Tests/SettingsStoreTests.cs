using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// SettingsStore 的合同（O-20）：所有写入在最新值上做增量；写盘失败时
/// 内存与磁盘都不动；坏文件改名保留；环境变量旁路只活在内存里。
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "shiyu-settings-store", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    // --- 写入口的唯一性 --------------------------------------------------------

    [Fact]
    public void Interleaved_field_updates_from_two_writers_both_survive()
    {
        new AppSettings { TargetLanguage = "Chinese", BarPinned = false }
            .Save(SettingsPath);
        var store = SettingsStore.Load(SettingsPath).Store;

        // S1 的形状：设置窗攥着开窗快照；窄条的图钉在此期间改了钉住
        // （只写它自己那个字段）；随后设置窗保存也只提交它改过的字段。
        store.Update(s => s with { BarPinned = true }, SettingsPath);
        store.Update(latest => latest with { TargetLanguage = "Japanese" }, SettingsPath);

        // 两边的修改都活着——不再有"最后保存的一方获胜"。
        Assert.True(store.Current.BarPinned);
        Assert.Equal("Japanese", store.Current.TargetLanguage);

        var onDisk = AppSettings.Load(SettingsPath);
        Assert.True(onDisk.BarPinned);
        Assert.Equal("Japanese", onDisk.TargetLanguage);
    }

    [Fact]
    public async Task Concurrent_updates_on_the_same_field_are_all_counted()
    {
        var store = new SettingsStore(new AppSettings { ImageRetentionDays = 0 });

        // 两个线程交错做"读—改—写"：任何一次读到旧值都会少数一次，
        // 最终计数就是竞态是否存在的证据。
        var done = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
            {
                store.Update(s => s with { ImageRetentionDays = s.ImageRetentionDays + 1 }, SettingsPath);
            }
        })).ToArray();

        await Task.WhenAll(done);

        Assert.Equal(500, store.Current.ImageRetentionDays);
        Assert.Equal(500, AppSettings.Load(SettingsPath).ImageRetentionDays);
    }

    // --- Changed 事件 ------------------------------------------------------------

    [Fact]
    public void Changed_carries_the_latest_effective_value_after_each_update()
    {
        var store = new SettingsStore(new AppSettings { TargetLanguage = "Chinese" });
        var seen = new List<string>();
        store.Changed += settings => seen.Add(settings.TargetLanguage);

        store.Update(s => s with { TargetLanguage = "English" }, SettingsPath);
        store.Update(s => s with { TargetLanguage = "Japanese" }, SettingsPath);

        Assert.Equal(["English", "Japanese"], seen);
    }

    [Fact]
    public void An_update_from_inside_a_Changed_handler_does_not_deadlock_nor_broadcast_backwards()
    {
        var store = new SettingsStore(new AppSettings { BarTextLines = 4, BarFileCount = 3 });
        var seen = new List<AppSettings>();

        store.Changed += settings =>
        {
            seen.Add(settings);

            // 处理器里的级联改动（如窄条钉）：再进 Update 不能死锁。
            if (settings.BarTextLines == 5 && settings.BarFileCount == 3)
            {
                store.Update(s => s with { BarFileCount = 4 }, SettingsPath);
            }
        };

        store.Update(s => s with { BarTextLines = 5 }, SettingsPath);

        Assert.Equal(5, store.Current.BarTextLines);
        Assert.Equal(4, store.Current.BarFileCount);

        // 广播序列必须单调：外层那次广播不能在内层改动之后又播一个旧值。
        Assert.Equal(5, seen[0].BarTextLines);
        Assert.Equal(4, seen[^1].BarFileCount);
        Assert.Equal(store.Current, seen[^1]);
    }

    // --- 写盘失败 ----------------------------------------------------------------

    [Fact]
    public void A_failed_save_changes_neither_memory_nor_disk()
    {
        new AppSettings { TargetLanguage = "Chinese" }.Save(SettingsPath);
        var store = SettingsStore.Load(SettingsPath).Store;

        // 占住设置文件，模拟"正在被别的程序看着"——Windows 上 Move 到一个
        // FileShare.None 的目标会抛 IOException。
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<SettingsSaveException>(() =>
                store.Update(s => s with { TargetLanguage = "English" }, SettingsPath));
        }

        // 内存没改、磁盘没改：不存在"内存已改、磁盘没写"的中间态。
        Assert.Equal("Chinese", store.Current.TargetLanguage);
        Assert.Equal("Chinese", AppSettings.Load(SettingsPath).TargetLanguage);

        // 占用解除后同一改动可以成功。
        store.Update(s => s with { TargetLanguage = "English" }, SettingsPath);
        Assert.Equal("English", AppSettings.Load(SettingsPath).TargetLanguage);
    }

    // --- 坏文件的迁移 ------------------------------------------------------------

    [Fact]
    public void A_corrupt_file_is_renamed_aside_and_preserved()
    {
        const string garbage = "{ this is not json";
        File.WriteAllText(SettingsPath, garbage);

        var loaded = SettingsStore.Load(SettingsPath);

        // 原文件被改名保留，没有被默认值覆盖。
        Assert.NotNull(loaded.QuarantinedPath);
        Assert.False(File.Exists(SettingsPath));
        Assert.True(File.Exists(loaded.QuarantinedPath!));
        Assert.StartsWith("settings.json.bad-", Path.GetFileName(loaded.QuarantinedPath!));
        Assert.Equal(garbage, File.ReadAllText(loaded.QuarantinedPath!));

        // 拿到的是能用的默认设置。
        Assert.Equal("Chinese", loaded.Store.Current.TargetLanguage);
    }

    [Fact]
    public void A_quarantined_store_writes_fresh_settings_without_touching_the_kept_file()
    {
        File.WriteAllText(SettingsPath, "{ this is not json");
        var loaded = SettingsStore.Load(SettingsPath);
        var quarantined = loaded.QuarantinedPath!;

        loaded.Store.Update(s => s with { TargetLanguage = "English" }, SettingsPath);

        // 新文件写出来了，保留下来的坏文件原样躺着。
        Assert.Equal("English", AppSettings.Load(SettingsPath).TargetLanguage);
        Assert.Equal("{ this is not json", File.ReadAllText(quarantined));
    }

    [Fact]
    public void A_missing_or_unreadable_file_yields_defaults_without_renaming_anything()
    {
        var fresh = SettingsStore.Load(SettingsPath);
        Assert.Null(fresh.QuarantinedPath);
        Assert.Equal("Chinese", fresh.Store.Current.TargetLanguage);
        Assert.False(File.Exists(SettingsPath));

        // 读不出来的文件（被占用）留在原地：它还在，下次启动还有机会读对。
        new AppSettings { TargetLanguage = "English" }.Save(SettingsPath);
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = SettingsStore.Load(SettingsPath);
            Assert.Null(blocked.QuarantinedPath);
            Assert.Equal("Chinese", blocked.Store.Current.TargetLanguage);
        }
    }

    // --- 非持久化旁路 ------------------------------------------------------------

    [Fact]
    public void The_bypass_overlay_reads_live_but_never_touches_the_disk()
    {
        new AppSettings { TargetLanguage = "Chinese" }.Save(SettingsPath);
        var store = SettingsStore.Load(
            SettingsPath, bypass: s => s with { RelayEndpoint = "http://probe.local" }).Store;

        // 读取看到旁路值。
        Assert.Equal("http://probe.local", store.Current.RelayEndpoint);

        store.Update(s => s with { TargetLanguage = "English" }, SettingsPath);

        // 落盘的永远是真值：旁路没有写进文件，磁盘上的端点还是官方地址。
        var onDisk = AppSettings.Load(SettingsPath);
        Assert.Equal("English", onDisk.TargetLanguage);
        Assert.Equal(new AppSettings().RelayEndpoint, onDisk.RelayEndpoint);

        // mutate 看到的也必须是磁盘真值，不是叠加值。
        store.Update(s => s with { BackendModel = s.RelayEndpoint }, SettingsPath);
        Assert.Equal(new AppSettings().RelayEndpoint, store.Current.BackendModel);
    }

    [Fact]
    public void A_bypassed_store_broadcasts_the_overlaid_value()
    {
        var store = new SettingsStore(
            new AppSettings(), bypass: s => s with { RelayEndpoint = "http://probe.local" });
        AppSettings? broadcast = null;
        store.Changed += settings => broadcast = settings;

        store.Update(s => s with { TargetLanguage = "English" }, SettingsPath);

        Assert.NotNull(broadcast);
        Assert.Equal("http://probe.local", broadcast!.RelayEndpoint);
        Assert.Equal("English", broadcast.TargetLanguage);
    }
}
