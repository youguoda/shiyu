using System.Text.RegularExpressions;

namespace Shiyu.Core.Tests;

/// <summary>
/// 结构上的看门人（票 43）：剪贴板的<b>写入</b>只在 WindowsClipboardWriter 里发生。
///
/// 为什么要钉这一条：拾语自己的写入——重新复制一条历史、取词借出后的还原、反向输入框的译文与
/// 还原——都靠"拾语是剪贴板 owner"被监听识别为"我自己的手"，从而不产生历史条目、不弹徽标
/// （WindowsClipboardMonitor 按 owner 跳过）。owner 来自写入时打开剪贴板用的窗口，这件事只有
/// 写入器做对了。任何绕开它的写入（别处直接 EmptyClipboard / SetClipboardData）都会被当成
/// "用户复制了东西"：译文被记进历史、还弹出翻译徽标。单测摸不到真实剪贴板，所以从源码结构上
/// 看住它；探针再用"历史条目数不变"做一次端到端的核对。
/// </summary>
public class ClipboardWriteGateTests
{
    private static readonly Regex Write = new(@"\b(EmptyClipboard|SetClipboardData)\s*\(", RegexOptions.Compiled);

    private static readonly string[] Allowed =
    [
        // 唯一的写入器。
        "src/Shiyu.Windows/WindowsClipboardWriter.cs",

        // 平台调用的声明处（只有签名，不是调用）。
        "src/Shiyu.Windows/NativeMethods.cs",
    ];

    [Fact]
    public void Only_the_writer_writes_the_clipboard()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (Allowed.Contains(relative)
                || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (Write.IsMatch(line) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}:{index + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "剪贴板的写入只经 WindowsClipboardWriter（拾语作为 owner，监听才会跳过它——"
            + "否则译文会被记进历史、还弹徽标）：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_reverse_inputs_clipboard_goes_through_the_writer()
    {
        // 反向输入框的写入与还原落在 WindowsReversePasteClipboard 上：它必须经 writer。
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Shiyu.Windows", "WindowsReversePasteClipboard.cs"));

        Assert.Contains("writer.SetText(", source);
        Assert.Contains("writer.SetFiles(", source);
        Assert.Contains("writer.SetRich(", source);
        Assert.Contains("writer.Clear(", source);
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
