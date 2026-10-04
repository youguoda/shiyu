using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class UpdateFeedTests
{
    [Fact]
    public void The_default_channel_is_the_published_repository()
    {
        Assert.Equal(
            "https://api.github.com/repos/youguoda/shiyu/releases/latest",
            UpdateChannel.Default.LatestUrl);
        Assert.Equal("shiyu-win-x64.zip", UpdateChannel.Default.AssetName);
    }

    [Fact]
    public void Tags_parse_with_or_without_the_v()
    {
        Assert.Equal(new UpdateVersion(1, 2, 3), UpdateVersion.Parse("v1.2.3"));
        Assert.Equal(new UpdateVersion(1, 2, 3), UpdateVersion.Parse("1.2.3"));
        Assert.Equal(new UpdateVersion(1, 0, 0), UpdateVersion.Parse("1"));
        Assert.Equal(new UpdateVersion(1, 2, 0), UpdateVersion.Parse("1.2"));
    }

    [Fact]
    public void Anything_that_is_not_a_version_is_refused_not_guessed()
    {
        Assert.Null(UpdateVersion.Parse(null));
        Assert.Null(UpdateVersion.Parse("latest"));
        Assert.Null(UpdateVersion.Parse("1.2.x"));
        Assert.Null(UpdateVersion.Parse(""));
    }

    [Fact]
    public void Comparison_is_field_by_field_not_stringwise()
    {
        // String comparison would call 0.10.0 older than 0.9.0.
        Assert.True(UpdateVersion.Parse("0.10.0")!.Value.CompareTo(UpdateVersion.Parse("0.9.0")!.Value) > 0);
        Assert.True(UpdateVersion.Parse("2.0.0")!.Value.CompareTo(UpdateVersion.Parse("1.99.99")!.Value) > 0);
        Assert.Equal(UpdateVersion.Parse("1.2.3"), UpdateVersion.Parse("v1.2.3"));
    }

    [Fact]
    public void Prerelease_suffixes_parse_and_lose_to_the_final_release()
    {
        // 0.9.0-accept18 parsed to null before, silently reading as "already
        // newest" against 0.9.0 — every rc/accept install was stranded.
        var accepted = UpdateVersion.Parse("0.9.0-accept18");
        Assert.NotNull(accepted);
        Assert.Equal("0.9.0-accept18", accepted!.Value.Text);

        Assert.True(UpdateVersion.Parse("0.9.0")!.Value.CompareTo(accepted.Value) > 0);
        Assert.True(accepted.Value.CompareTo(UpdateVersion.Parse("0.9.0")!.Value) < 0);

        // Build metadata never affects ordering.
        Assert.Equal(accepted.Value, UpdateVersion.Parse("0.9.0-accept18+deadbeef")!.Value);

        // Two prereleases order by suffix ordinal (rc1 < rc2 — documented as
        // enough for our own naming; it is NOT numeric-aware for rc10),
        // and a newer trio beats any suffix.
        Assert.True(UpdateVersion.Parse("0.9.0-rc1")!.Value.CompareTo(UpdateVersion.Parse("0.9.0-rc2")!.Value) < 0);
        Assert.True(UpdateVersion.Parse("0.9.1-rc1")!.Value.CompareTo(UpdateVersion.Parse("0.9.0")!.Value) > 0);

        // A dangling dash or an empty suffix is not a version.
        Assert.Null(UpdateVersion.Parse("1.2.3-"));
    }

    private const string ReleaseJson = """
        {
          "tag_name": "v1.4.2",
          "body": "修复了图片记录的一个问题。",
          "published_at": "2026-09-20T08:00:00Z",
          "assets": [
            { "name": "shiyu-win-x64.zip", "size": 71234567, "browser_download_url": "https://example.invalid/shiyu-win-x64.zip" },
            { "name": "shiyu-win-x64.zip.sha256", "size": 88, "browser_download_url": "https://example.invalid/shiyu-win-x64.zip.sha256" },
            { "name": "sources.zip", "size": 100, "browser_download_url": "https://example.invalid/sources.zip" }
          ]
        }
        """;

    [Fact]
    public void A_release_parses_into_version_notes_and_assets()
    {
        var release = ReleaseManifest.Parse(ReleaseJson);

        Assert.NotNull(release);
        Assert.Equal(new UpdateVersion(1, 4, 2), release.Version);
        Assert.Contains("修复", release.Notes);
        Assert.Equal(3, release.Assets.Count);

        var installer = release.Asset("shiyu-win-x64.zip");
        Assert.NotNull(installer);
        Assert.Equal(71234567, installer.Size);

        var checksum = release.ChecksumFor("shiyu-win-x64.zip");
        Assert.NotNull(checksum);
        Assert.Equal("shiyu-win-x64.zip.sha256", checksum.Name);
    }

    [Fact]
    public void A_release_without_a_tag_is_not_a_release()
    {
        Assert.Null(ReleaseManifest.Parse("{}"));
        Assert.Null(ReleaseManifest.Parse("not json at all"));
    }

    [Fact]
    public void Newer_is_decided_by_the_version_not_the_date()
    {
        var release = ReleaseManifest.Parse(ReleaseJson)!;

        Assert.True(release.IsNewerThan(new UpdateVersion(1, 4, 1)));
        Assert.False(release.IsNewerThan(new UpdateVersion(1, 4, 2)));
        Assert.False(release.IsNewerThan(new UpdateVersion(9, 0, 0)));
    }
}
