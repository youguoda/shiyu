using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 编排的可测骨架（票 09）：一个按路径回放固定文件的假发布源，一把测试
/// 自造的签名钥。这里钉住的是分叉——每个"不"都必须既报中文人话、又把
/// 暂存区清空。
/// </summary>
public class UpdateOrchestratorTests
{
    private static readonly UpdateChannel Channel = new("example", "shiyu", "shiyu-win-x64.zip");

    private static byte[] InstallerZip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Shiyu.App.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("new exe");
        }

        return stream.ToArray();
    }

    private static (byte[] Signature, string PublicKeyPem) Sign(byte[] package)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            key.SignData(package, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence),
            key.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>一条完整的发布：安装包、签名、校验和三件齐全，互相吻合。</summary>
    private static ReleaseManifest Release(long installerSize)
        => new(
            new UpdateVersion(1, 4, 2),
            "修复了若干问题。",
            DateTimeOffset.Now,
            [
                new ReleaseAsset("shiyu-win-x64.zip", installerSize, "https://example.invalid/shiyu-win-x64.zip"),
                new ReleaseAsset("shiyu-win-x64.zip.sig", 72, "https://example.invalid/shiyu-win-x64.zip.sig"),
                new ReleaseAsset("shiyu-win-x64.zip.sha256", 90, "https://example.invalid/shiyu-win-x64.zip.sha256"),
            ]);

    private static string ChecksumText(byte[] package)
        => Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant() + "  shiyu-win-x64.zip";

    private static UpdateOrchestrator Orchestrator(
        FakeReleaseServer server, string dataDirectory, string publicKeyPem)
        => new(new HttpClient(server), dataDirectory, Channel, publicKeyPem);

    [Fact]
    public async Task The_whole_pipeline_stages_a_signed_release()
    {
        using var area = new TempDirectory();
        var package = InstallerZip();
        var (signature, publicKey) = Sign(package);
        using var server = new FakeReleaseServer(new Dictionary<string, object>
        {
            ["/shiyu-win-x64.zip"] = package,
            ["/shiyu-win-x64.zip.sig"] = signature,
            ["/shiyu-win-x64.zip.sha256"] = ChecksumText(package),
        });

        var (ok, error) = await Orchestrator(server, area.Path, publicKey).DownloadAsync(
            Release(package.Length), progress: null, CancellationToken.None);

        Assert.True(ok, error);
        Assert.True(File.Exists(
            Path.Combine(UpdateStaging.StagedDirectory(area.Path), "Shiyu.App.exe")));
        Assert.Equal("1.4.2", UpdateStaging.ReadPending(area.Path)?.Version);
    }

    [Fact]
    public async Task A_tampered_package_is_rejected_and_the_staging_area_left_empty()
    {
        using var area = new TempDirectory();
        var package = InstallerZip();
        var (signature, publicKey) = Sign(package);

        // 中间人改了一个字节：签名对的是原包，对不上眼前的包。
        package[^1] ^= 1;
        using var server = new FakeReleaseServer(new Dictionary<string, object>
        {
            ["/shiyu-win-x64.zip"] = package,
            ["/shiyu-win-x64.zip.sig"] = signature,
            ["/shiyu-win-x64.zip.sha256"] = ChecksumText(package),
        });

        var (ok, error) = await Orchestrator(server, area.Path, publicKey).DownloadAsync(
            Release(package.Length), progress: null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("签名不符", error);
        Assert.False(Directory.Exists(UpdateStaging.Root(area.Path)));
    }

    [Fact]
    public async Task A_release_without_a_signature_asset_is_refused()
    {
        using var area = new TempDirectory();
        var package = InstallerZip();
        var (_, publicKey) = Sign(package);
        var release = Release(package.Length) with
        {
            Assets = [.. Release(package.Length).Assets.Where(asset => !asset.Name.EndsWith(".sig"))],
        };
        using var server = new FakeReleaseServer(new Dictionary<string, object>
        {
            ["/shiyu-win-x64.zip"] = package,
        });

        var (ok, error) = await Orchestrator(server, area.Path, publicKey).DownloadAsync(
            release, progress: null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("签名", error);
        Assert.False(Directory.Exists(UpdateStaging.Root(area.Path)));
    }

    [Fact]
    public async Task A_signature_that_cannot_be_downloaded_is_refused()
    {
        using var area = new TempDirectory();
        var package = InstallerZip();
        var (signature, publicKey) = Sign(package);
        using var server = new FakeReleaseServer(new Dictionary<string, object>
        {
            ["/shiyu-win-x64.zip"] = package,
            ["/shiyu-win-x64.zip.sha256"] = ChecksumText(package),
            // .sig 没有路由 → 404：取不到签名与没有签名同一个下场。
        });

        var (ok, error) = await Orchestrator(server, area.Path, publicKey).DownloadAsync(
            Release(package.Length), progress: null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("下载失败", error);
        Assert.False(Directory.Exists(UpdateStaging.Root(area.Path)));
    }

    [Fact]
    public async Task A_checksum_that_cannot_be_fetched_is_no_longer_waved_through()
    {
        using var area = new TempDirectory();
        var package = InstallerZip();
        var (signature, publicKey) = Sign(package);
        using var server = new FakeReleaseServer(new Dictionary<string, object>
        {
            ["/shiyu-win-x64.zip"] = package,
            ["/shiyu-win-x64.zip.sig"] = signature,
            // .sha256 没有路由 → 404：从前的 fail-open 会当它不存在。
        });

        var (ok, error) = await Orchestrator(server, area.Path, publicKey).DownloadAsync(
            Release(package.Length), progress: null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("下载失败", error);
        Assert.False(Directory.Exists(UpdateStaging.Root(area.Path)));
    }

    [Fact]
    public async Task An_installer_download_failure_cleans_the_staging_area()
    {
        using var area = new TempDirectory();
        var package = InstallerZip();
        var (signature, publicKey) = Sign(package);
        using var server = new FakeReleaseServer([]);

        var (ok, error) = await Orchestrator(server, area.Path, publicKey).DownloadAsync(
            Release(package.Length), progress: null, CancellationToken.None);

        Assert.False(ok);
        Assert.False(Directory.Exists(UpdateStaging.Root(area.Path)));
    }

    [Fact]
    public async Task A_cancelled_download_cleans_the_staging_area_and_says_so()
    {
        using var area = new TempDirectory();
        var package = InstallerZip();
        var (signature, publicKey) = Sign(package);
        using var server = new FakeReleaseServer(new Dictionary<string, object>
        {
            ["/shiyu-win-x64.zip"] = package,
            ["/shiyu-win-x64.zip.sig"] = signature,
            ["/shiyu-win-x64.zip.sha256"] = ChecksumText(package),
        });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (ok, error) = await Orchestrator(server, area.Path, publicKey).DownloadAsync(
            Release(package.Length), progress: null, cancelled.Token);

        Assert.False(ok);
        Assert.Contains("取消", error);
        Assert.False(Directory.Exists(UpdateStaging.Root(area.Path)));
    }

    [Fact]
    public async Task CheckAsync_reads_the_latest_release_from_the_channel()
    {
        using var server = new FakeReleaseServer(new Dictionary<string, object>
        {
            ["/repos/example/shiyu/releases/latest"] = """
                {
                  "tag_name": "v1.4.2",
                  "body": "说明",
                  "assets": [
                    { "name": "shiyu-win-x64.zip", "size": 10, "browser_download_url": "https://example.invalid/shiyu-win-x64.zip" },
                    { "name": "shiyu-win-x64.zip.sig", "size": 72, "browser_download_url": "https://example.invalid/shiyu-win-x64.zip.sig" }
                  ]
                }
                """,
        });

        var release = await new UpdateOrchestrator(
            new HttpClient(server), Path.GetTempPath(), Channel).CheckAsync();

        Assert.NotNull(release);
        Assert.Equal(new UpdateVersion(1, 4, 2), release.Version);
        Assert.NotNull(release.SignatureFor("shiyu-win-x64.zip"));
    }

    /// <summary>
    /// 按请求路径回放固定文件的假发布源：byte[] 按二进制、string 按文本，
    /// 没有路由一律 404。路径匹配不看主机——编排只该关心它要哪个文件。
    /// </summary>
    private sealed class FakeReleaseServer(Dictionary<string, object> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellation)
        {
            if (!files.TryGetValue(request.RequestUri!.AbsolutePath, out var file))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            HttpContent content = file switch
            {
                byte[] bytes => new ByteArrayContent(bytes),
                string text => new StringContent(text),
                _ => throw new InvalidOperationException("unknown fixture"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}

/// <summary>
/// 多源下载与续传（用户反馈 2026-10-19：GitHub 直连慢、时坏）：直连打头、镜像垫后；
/// 一个源断流换下一个，半成品字节可跨源续传。自带的假源按「直连半途断流、镜像全量
/// 200」与「认 Range 的续传源」两种姿势演。
/// </summary>
public class UpdateDownloadSourceTests
{
    private const string AssetUrl = "https://example.invalid/shiyu-win-x64.zip";
    private static readonly string MirrorUrl =
        UpdateOrchestrator.DownloadMirrorPrefixes[0] + AssetUrl;

    private static readonly UpdateChannel Channel = new("example", "shiyu", "shiyu-win-x64.zip");

    private static (byte[] Package, byte[] Signature, string PublicKeyPem, string Checksum) SignedPackage()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Shiyu.App.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("new exe");
        }

        var package = stream.ToArray();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = key.SignData(package, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var checksum = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant() + "  shiyu-win-x64.zip";
        return (package, signature, key.ExportSubjectPublicKeyInfoPem(), checksum);
    }

    [Fact]
    public void Candidates_put_the_direct_url_first_then_the_mirrors_in_order()
    {
        var candidates = UpdateOrchestrator.CandidateUrls(AssetUrl);

        Assert.Equal(1 + UpdateOrchestrator.DownloadMirrorPrefixes.Length, candidates.Count);
        Assert.Equal(AssetUrl, candidates[0]);
        Assert.Equal(
            UpdateOrchestrator.DownloadMirrorPrefixes.Select(prefix => prefix + AssetUrl),
            candidates.Skip(1));
    }

    [Fact]
    public async Task A_direct_source_breaking_mid_body_fails_over_to_the_mirror()
    {
        var (package, signature, pem, checksum) = SignedPackage();
        var files = new Dictionary<string, object>
        {
            // 直连：报了全长却只给一半（断流）；镜像：全量。
            ["/shiyu-win-x64.zip"] = package.Take(package.Length / 2).ToArray(),
            ["/https://example.invalid/shiyu-win-x64.zip"] = package,
            ["/https://example.invalid/shiyu-win-x64.zip.sig"] = signature,
            ["/https://example.invalid/shiyu-win-x64.zip.sha256"] = checksum,
        };

        using var area = new TempDirectory();
        var orchestrator = new UpdateOrchestrator(
            new HttpClient(new ByPathServer(files)), area.Path, Channel, pem);

        var (ok, error) = await orchestrator.DownloadAsync(Release(package.Length), null, CancellationToken.None);

        Assert.True(ok, error);
    }

    [Fact]
    public async Task A_partial_download_resumes_from_where_it_stopped()
    {
        var (package, signature, pem, checksum) = SignedPackage();
        using var area = new TempDirectory();
        var partial = UpdateStaging.InstallerPath(area.Path) + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.WriteAllBytes(partial, package[..(package.Length / 2)]);

        // 认 Range 的源：从已有长度接着给尾巴。
        using var handler = new RangeAwareServer(package, signature, checksum);

        var (ok, error) = await new UpdateOrchestrator(
            new HttpClient(handler), area.Path, Channel, pem)
            .DownloadAsync(Release(package.Length), null, CancellationToken.None);

        Assert.True(ok, error);
        Assert.Equal(package, await File.ReadAllBytesAsync(UpdateStaging.InstallerPath(area.Path)));
    }

    [Fact]
    public async Task A_source_that_ignores_the_range_restarts_from_zero()
    {
        var (package, signature, pem, checksum) = SignedPackage();
        using var area = new TempDirectory();
        var partial = UpdateStaging.InstallerPath(area.Path) + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.WriteAllBytes(partial, package[..(package.Length / 2)]);

        // 无视 Range 的源：永远回全量——下载器应当从头重写，而不是把尾巴接到半成品上。
        using var handler = new PlainServer(package, signature, checksum);

        var (ok, error) = await new UpdateOrchestrator(
            new HttpClient(handler), area.Path, Channel, pem)
            .DownloadAsync(Release(package.Length), null, CancellationToken.None);

        Assert.True(ok, error);
        Assert.Equal(package, await File.ReadAllBytesAsync(UpdateStaging.InstallerPath(area.Path)));
    }

    private static ReleaseManifest Release(long installerSize)
        => new(
            new UpdateVersion(1, 4, 3),
            "修复了若干问题。",
            DateTimeOffset.Now,
            [
                new ReleaseAsset("shiyu-win-x64.zip", installerSize, AssetUrl),
                new ReleaseAsset("shiyu-win-x64.zip.sig", 72, AssetUrl + ".sig"),
                new ReleaseAsset("shiyu-win-x64.zip.sha256", 90, AssetUrl + ".sha256"),
            ]);

    /// <summary>认 Range 的源：安装包带 Range 回 206 尾巴；签名与校验和照字面给。</summary>
    private sealed class RangeAwareServer(byte[] package, byte[] signature, string checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sig"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(signature),
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(checksum),
                });
            }

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range is { } span && (span.From ?? 0) > 0)
            {
                var from = (int)(span.From ?? 0);
                var message = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(package[from..]),
                };
                message.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                    from, package.Length - 1, package.Length);
                return Task.FromResult(message);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package),
            });
        }
    }

    /// <summary>无视 Range 的源：安装包永远回 200 全量（测「从头重写」的分支）；签名与校验和照字面给。</summary>
    private sealed class PlainServer(byte[] package, byte[] signature, string checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sig"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(signature),
                });
            }

            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(checksum),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package),
            });
        }
    }

    /// <summary>按绝对路径回放文件的假源（自带一份，不借嵌套私件）。</summary>
    private sealed class ByPathServer(Dictionary<string, object> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellation)
        {
            if (!files.TryGetValue(request.RequestUri!.AbsolutePath, out var file))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            HttpContent content = file switch
            {
                byte[] bytes => new ByteArrayContent(bytes),
                string text => new StringContent(text),
                _ => throw new InvalidOperationException("unknown fixture"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
