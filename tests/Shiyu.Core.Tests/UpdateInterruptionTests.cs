using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// 更新中断的端到端语义（测试调研 P0-4）：截断的下载、校验和不符、缺签名资产——
/// 每一种拒绝都要 (a) 如实报错、(b) 清空暂存区（没有半截安装包）。本地 HttpListener
/// 扮演发布源；验签用测试自产的 ECDSA 密钥对（公钥经构造器注入，不碰内置公钥）。
/// </summary>
public class UpdateInterruptionTests : IDisposable
{
    private static readonly string InstallerMarker = "Shiyu.App.exe 的假安装包字节";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), $"shiyu-upd-{Guid.NewGuid():N}");

    private readonly byte[] _zip;
    private readonly byte[] _signature;
    private readonly byte[] _sha256Line;
    private readonly string _publicKeyPem;

    public UpdateInterruptionTests()
    {
        // 一个真实的 zip：VerifyInstaller 要求里面躺着 Shiyu.App.exe。
        var zipStream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Shiyu.App.exe");
            using var writer = entry.Open();
            writer.Write(Encoding.UTF8.GetBytes(InstallerMarker));
        }

        _zip = zipStream.ToArray();

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        _signature = ecdsa.SignData(
            _zip, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var sha256Hex = Convert.ToHexString(SHA256.HashData(_zip)).ToLowerInvariant();
        _sha256Line = Encoding.ASCII.GetBytes($"{sha256Hex}  shiyu-win-x64.zip");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ReleaseManifest Manifest(
        string baseUri, bool withSignature = true, bool withChecksum = true, bool truncate = false)
    {
        var root = baseUri.TrimEnd('/');
        var size = truncate ? 1000 : _zip.Length;
        var assets = new List<ReleaseAsset>
        {
            new("shiyu-win-x64.zip", size, root + "/assets/shiyu-win-x64.zip"),
        };
        if (withSignature)
        {
            assets.Add(new ReleaseAsset("shiyu-win-x64.zip.sig", _signature.Length, root + "/assets/shiyu-win-x64.zip.sig"));
        }
        if (withChecksum)
        {
            assets.Add(new ReleaseAsset("shiyu-win-x64.zip.sha256", _sha256Line.Length, root + "/assets/shiyu-win-x64.zip.sha256"));
        }

        return new ReleaseManifest(new UpdateVersion(99, 0, 0), "probe", DateTimeOffset.UtcNow, assets);
    }

    [Fact]
    public async Task A_truncated_download_refuses_and_clears_the_staging_area()
    {
        using var server = new ProbeSource(_zip, _signature, _sha256Line, truncateAt: 100);
        var orchestrator = new UpdateOrchestrator(
            new HttpClient(), _dataDirectory, UpdateChannel.Default, _publicKeyPem);

        var (ok, error) = await orchestrator.DownloadAsync(Manifest(server.Prefix, truncate: true), null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("下载失败", error);
        Assert.False(Directory.Exists(UpdateStaging.Root(_dataDirectory)));
    }

    [Fact]
    public async Task A_checksum_mismatch_refuses_and_clears_the_staging_area()
    {
        using var server = new ProbeSource(_zip, _signature, Encoding.ASCII.GetBytes("0000000000000000000000000000000000000000000000000000000000000000  shiyu-win-x64.zip"), truncateAt: null);
        var orchestrator = new UpdateOrchestrator(
            new HttpClient(), _dataDirectory, UpdateChannel.Default, _publicKeyPem);

        var (ok, error) = await orchestrator.DownloadAsync(Manifest(server.Prefix), null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("SHA256", error);
        Assert.False(Directory.Exists(UpdateStaging.Root(_dataDirectory)));
    }

    [Fact]
    public async Task A_matching_package_stages_with_its_image_and_pending_marker()
    {
        using var server = new ProbeSource(_zip, _signature, _sha256Line, truncateAt: null);
        var orchestrator = new UpdateOrchestrator(
            new HttpClient(), _dataDirectory, UpdateChannel.Default, _publicKeyPem);

        var (ok, error) = await orchestrator.DownloadAsync(Manifest(server.Prefix), null, CancellationToken.None);

        Assert.True(ok, error);
        Assert.True(File.Exists(UpdateStaging.InstallerPath(_dataDirectory)));
        Assert.True(Directory.Exists(UpdateStaging.StagedDirectory(_dataDirectory)));
        Assert.True(File.Exists(UpdateStaging.PendingPath(_dataDirectory)));
    }

    [Fact]
    public async Task A_release_without_a_signature_asset_refuses_before_any_download()
    {
        using var server = new ProbeSource(_zip, _signature, _sha256Line, truncateAt: null);
        var orchestrator = new UpdateOrchestrator(
            new HttpClient(), _dataDirectory, UpdateChannel.Default, _publicKeyPem);

        var (ok, error) = await orchestrator.DownloadAsync(Manifest(server.Prefix, withSignature: false), null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("签名", error);
        Assert.False(File.Exists(UpdateStaging.InstallerPath(_dataDirectory)));
    }

    private sealed class ProbeSource : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly Dictionary<string, byte[]> _routes = new();
        private readonly bool _truncate;
        private bool _stopped;

        public ProbeSource(byte[] zip, byte[] signature, byte[] sha256, int? truncateAt)
        {
            _routes["/assets/shiyu-win-x64.zip"] = zip;
            _routes["/assets/shiyu-win-x64.zip.sig"] = signature;
            _routes["/assets/shiyu-win-x64.zip.sha256"] = sha256;
            _truncate = truncateAt is not null;

            _listener = System.Net.Sockets.TcpListener.Create(0);
            _listener.Start();
            Prefix = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = Task.Run(ListenLoop);
        }

        public string Prefix { get; }

        private async Task ListenLoop()
        {
            while (!_stopped)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => Serve(client));
            }
        }

        /// <summary>手写最小 HTTP 应答：请求行取路径，路由表给字节；截断模式发一半就断。</summary>
        private void Serve(System.Net.Sockets.TcpClient client)
        {
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n"))
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    Console.WriteLine("[ProbeSource] 客户端在请求头完整前就断了连接");
                    return;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var path = request.ToString().Split(' ')[1];
            var body = _routes.TryGetValue(path, out var route) ? route : Array.Empty<byte>();
            Console.WriteLine($"[ProbeSource] 收到 {path} → {(body.Length > 0 ? "200" : "404")}（{_truncate}）");
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(body.Length > 0 ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            if (body.Length > 0 && _truncate && path.EndsWith(".zip"))
            {
                stream.Write(body, 0, Math.Min(100, body.Length));
            }
            else if (body.Length > 0)
            {
                stream.Write(body, 0, body.Length);
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _listener.Stop();
        }
    }
}
