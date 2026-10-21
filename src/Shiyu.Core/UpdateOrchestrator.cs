using System.Net.Http;

namespace Shiyu.Core;

/// <summary>
/// 更新的编排（票 09 / ADR-0010）：取清单 → 选资产 → 下载 → 下载 .sig 并
/// 验签 → 校验大小与 SHA-256（只管完整性，不再承担信任）→ 解压 → 写
/// pending。整个流程在 Core 里跑，HttpClient 与数据目录从外面注入，网络
/// 的每一个分叉都可以在测试里钉死。
///
/// 信任只有一道门：内置公钥对安装包完整字节的验签。没有 .sig 资产、
/// 签名下不来、验签不过——一律拒绝并清空暂存区，不存在任何"取不到就
/// 放行"的旁路；发布带了 .sha256 就必须下得来且对得上（从前的校验和
/// fail-open 已删）。
/// </summary>
public sealed class UpdateOrchestrator(
    HttpClient http,
    string dataDirectory,
    UpdateChannel? channel = null,
    string? publicKeyPem = null,
    string? userAgent = null)
{
    private readonly HttpClient _http = http;
    private readonly string _dataDirectory = dataDirectory;
    private readonly UpdateChannel _channel = channel ?? UpdateChannel.Default;
    private readonly string? _publicKeyPem = publicKeyPem;
    private readonly string _userAgent = userAgent ?? "Shiyu";

    /// <summary>
    /// 下载源：GitHub 直连打头，公共镜像垫后（直连实测 2026-10-19：gh-proxy 可用，
    /// mirror.ghproxy 与 ghfast.top 超时——镜像可用性会漂移，所以每个源都进链、
    /// 直连永远第一；漂了只是多花时间，不会失败）。镜像只做字节搬运：每一路的
    /// 字节都要过内置公钥验签与 SHA-256，被篡改的源一条也进不了安装。
    /// </summary>
    public static readonly string[] DownloadMirrorPrefixes =
    [
        "https://gh-proxy.com/",
        "https://mirror.ghproxy.com/",
        "https://ghfast.top/",
    ];

    /// <summary>下载候选：直连打头，镜像依次垫后。</summary>
    public static IReadOnlyList<string> CandidateUrls(string url)
    {
        var list = new List<string>(DownloadMirrorPrefixes.Length + 1) { url };
        list.AddRange(DownloadMirrorPrefixes.Select(prefix => prefix + url));
        return list;
    }

    /// <summary>
    /// GitHub refuses anonymous API calls without a User-Agent. On the shared
    /// process client the header cannot live on the client itself — it would
    /// claim every request Shiyu makes is an update check — so it rides on
    /// each message.
    /// </summary>
    private async Task<HttpResponseMessage> GetAsync(
        string url, HttpCompletionOption completion, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        return await _http.SendAsync(request, completion, cancel);
    }

    /// <summary>The latest release, or null when the channel said nothing usable.</summary>
    public async Task<ReleaseManifest?> CheckAsync(CancellationToken cancel = default)
    {
        var url = _channel.LatestUrl;
#if DEBUG
        // Debug only: a release build takes its update source from nothing
        // but the compiled-in channel. 常量随读取一起住在 DEBUG 区（O-41）：
        // 无引用的私有常量也会留在元数据里，字面量进发布产物就没有干净的
        // 二进制验证可言。
        const string ApiOverrideVariable = "SHIYU_UPDATE_API";

        if (Environment.GetEnvironmentVariable(ApiOverrideVariable) is { Length: > 0 } overrideBase)
        {
            url = overrideBase.TrimEnd('/') + "/releases/latest";
        }
#endif

        using var response = await GetAsync(url, HttpCompletionOption.ResponseContentRead, cancel);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return ReleaseManifest.Parse(await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>
    /// Downloads, verifies and stages an update. Every refusal clears the
    /// whole staging area — the ticket's "no half installers" is a directory
    /// invariant, not a promise.
    /// </summary>
    public async Task<(bool Ok, string Error)> DownloadAsync(
        ReleaseManifest release, IProgress<double>? progress, CancellationToken cancel)
    {
        try
        {
            if (release.Asset(_channel.AssetName) is not { } asset)
            {
                UpdateStaging.TryReset(_dataDirectory);
                return (false, $"这个发布（v{release.Version.Text}）没有 {_channel.AssetName}。");
            }

            if (release.SignatureFor(asset.Name) is not { } signatureAsset)
            {
                // 没有签名就没有信任可言——这不是"少了一个可选附件"。
                UpdateStaging.TryReset(_dataDirectory);
                return (false, $"这个发布（v{release.Version.Text}）没有安装包签名（{asset.Name}.sig），已拒绝安装。");
            }

            // 拒绝即空目录：从第一步起就没有旧失败留下的东西。
            UpdateStaging.Reset(_dataDirectory);
            Directory.CreateDirectory(UpdateStaging.Root(_dataDirectory));

            var partialPath = UpdateStaging.InstallerPath(_dataDirectory) + ".partial";
            await DownloadFileAsync(asset.Url, partialPath, asset.Size, progress, cancel);
            File.Move(partialPath, UpdateStaging.InstallerPath(_dataDirectory), overwrite: true);

            // 信任之门开在一切内容检查之前：验不过，大小、校验和、zip
            // 结构一概不看了。
            var signature = await DownloadBytesWithFailover(signatureAsset.Url, cancel);
            var installer = await File.ReadAllBytesAsync(UpdateStaging.InstallerPath(_dataDirectory), cancel);
            if (!UpdateSignature.Verify(installer, signature, _publicKeyPem))
            {
                UpdateStaging.TryReset(_dataDirectory);
                return (false, "更新包签名不符，已拒绝安装。");
            }

            // 完整性（不是信任）：带了 .sha256 就必须下得来、对得上。
            var checksum = await DownloadChecksumAsync(release, asset, cancel);
            var (ok, error) = UpdateStaging.VerifyInstaller(
                UpdateStaging.InstallerPath(_dataDirectory), asset.Size, checksum);
            if (!ok)
            {
                UpdateStaging.TryReset(_dataDirectory);
                return (false, error);
            }

            UpdateStaging.Extract(
                UpdateStaging.InstallerPath(_dataDirectory), UpdateStaging.StagedDirectory(_dataDirectory));
            UpdateStaging.WritePending(_dataDirectory, release);
            return (true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            UpdateStaging.TryReset(_dataDirectory);
            return (false, "已取消。");
        }
        catch (HttpRequestException failure)
        {
            UpdateStaging.TryReset(_dataDirectory);
            return (false, "下载失败：" + failure.Message);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // expected: 暂存区被占着（前一次下载没停）、磁盘满、没有权限——都收成一句话。
            UpdateStaging.TryReset(_dataDirectory);
            return (false, "写入失败：" + failure.Message);
        }
    }

    private async Task<string?> DownloadChecksumAsync(
        ReleaseManifest release, ReleaseAsset asset, CancellationToken cancel)
    {
        if (release.ChecksumFor(asset.Name) is not { } checksumAsset)
        {
            // 发布真的没带校验和：完整性由大小与验签兜底，这不是旁路。
            return null;
        }

        // 带了却下不来 → 换源试尽仍失败才由调用方拒绝并清空。
        // 从前的"下不来就当作没有"正是被删掉的 fail-open。
        return await DownloadStringWithFailover(checksumAsset.Url, cancel);
    }

    /// <summary>单个候选源的时间预算：跑满就换下一个源，半成品留给续传。</summary>
    private static readonly TimeSpan SourceBudget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 多源下载（用户反馈 2026-10-19：GitHub 直连慢、时坏）：直连打头、镜像垫后，
    /// 一个源出师不利或半路断流就换下一个，半成品字节留给下一个源**续传**
    /// （Range 从已有长度接着要；各源给的是同一条字节流，换源续传不冲突，
    /// 完整性由事后的验签与 SHA-256 把关）。全部源试尽才失败。
    /// </summary>
    private async Task DownloadFileAsync(
        string assetUrl, string path, long expectedSize, IProgress<double>? progress, CancellationToken cancel)
    {
        Exception? last = null;

        foreach (var url in CandidateUrls(assetUrl))
        {
            try
            {
                await DownloadFromAsync(url, path, expectedSize, progress, cancel);
                return;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                // expected: 一个源出师不利（404/被墙/断流/单源超时）——换下一个。
                last = failure;
            }
        }

        throw last ?? new HttpRequestException("no download source succeeded");
    }

    private async Task DownloadFromAsync(
        string url, string path, long expectedSize, IProgress<double>? progress, CancellationToken cancel)
    {
        var have = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (expectedSize > 0 && have > expectedSize)
        {
            // 半成品比正身还长（换过版本的残留）：不续，从头来。
            File.Delete(path);
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        if (have > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        }

        using var budget = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(SourceBudget);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            have = 0;   // 源没理会续传：从头下。
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable
            && expectedSize > 0 && have == expectedSize)
        {
            return;     // 半成品恰好已经齐了。
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }

        await using var source = await response.Content.ReadAsStreamAsync(budget.Token);
        await using var target = File.Open(path, have > 0 ? FileMode.Append : FileMode.Create);

        var copied = 0L;
        var buffer = new byte[64 * 1024];
        int read;

        while ((read = await source.ReadAsync(buffer, budget.Token)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), budget.Token);
            copied += read;

            if (expectedSize > 0)
            {
                progress?.Report(Math.Min(1.0, (double)(have + copied) / expectedSize));
            }
        }

        // 声明了总长却在 EOF 前短了：连接断在中途——换源重试（半成品可续传）。
        var total = have + copied;
        if (expectedSize > 0 && total < expectedSize)
        {
            throw new IOException($"下载中断：收到 {total} 字节，共 {expectedSize} 字节。");
        }
    }

    private async Task<byte[]> DownloadBytesWithFailover(string url, CancellationToken cancel)
    {
        Exception? last = null;

        foreach (var candidate in CandidateUrls(url))
        {
            try
            {
                using var response = await GetAsync(candidate, HttpCompletionOption.ResponseContentRead, cancel);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                // expected: 换下一个源（签名与校验和都是 KB 级小件）。
                last = failure;
            }
        }

        throw last ?? new HttpRequestException("no download source succeeded");
    }

    private async Task<string> DownloadStringWithFailover(string url, CancellationToken cancel)
    {
        Exception? last = null;

        foreach (var candidate in CandidateUrls(url))
        {
            try
            {
                using var response = await GetAsync(candidate, HttpCompletionOption.ResponseContentRead, cancel);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                // expected: 换下一个源。
                last = failure;
            }
        }

        throw last ?? new HttpRequestException("no download source succeeded");
    }
}
