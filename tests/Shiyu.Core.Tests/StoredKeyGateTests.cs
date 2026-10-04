using System.Text.RegularExpressions;

namespace Shiyu.Core.Tests;

/// <summary>
/// 结构上的看门人：已存的密钥字符串只在 AppSettings 里被读写。别处要知道
/// "有没有自备密钥"一律问 <c>Backend.IsConfigured</c>，要拿密钥走 <c>KeyFor</c>，
/// 要写走 <c>WithApiKey</c>——所以 OwnKeyDictionary 一类 App 层判断（单测摸
/// 不到 WPF 层）不可能再退回去直接看字符串、绕开来源绑定。
/// </summary>
public class StoredKeyGateTests
{
    private static readonly Regex DirectUse = new(@"\bBackendApiKey\b", RegexOptions.Compiled);

    // (仓库相对路径, 行内须包含的标记)：确属"不是访问该属性"的例外。
    private static readonly (string Path, string Marker)[] Allowed =
    [
        // DPAPI 的附加熵是个字符串常量，恰好含这个词。
        ("src/Shiyu.Windows/DpapiSecretProtector.cs", "Shiyu.BackendApiKey.v1"),
    ];

    [Fact]
    public void Nothing_outside_AppSettings_touches_the_stored_key_directly()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == "src/Shiyu.Core/AppSettings.cs"
                || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (!DirectUse.IsMatch(line)
                    || line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || Allowed.Any(entry => entry.Path == relative && line.Contains(entry.Marker)))
                {
                    continue;
                }

                offenders.Add($"{relative}:{index + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "已存的密钥只在 AppSettings 里读写（票 29）：要知道有没有自备密钥请问 Backend.IsConfigured，"
            + "取密钥走 KeyFor，写密钥走 WithApiKey，备份恢复走 KeepingKeyOf：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>从测试程序集的位置向上找仓库根（含 src/Shiyu.Core 的目录）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "src", "Shiyu.Core")))
        {
            dir = dir.Parent!;
        }

        return dir is not null
            ? dir.FullName
            : throw new InvalidOperationException("找不到仓库根：测试必须从仓库内运行。");
    }
}
