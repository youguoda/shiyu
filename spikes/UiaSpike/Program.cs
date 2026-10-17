// UIA 取词 spike（评审 2026-10-16 F-11 · D4）：TextPattern.GetSelection 在记事本、Edge、VS Code、Word 里读不读得到选区。
//
// 只用自己写的测试文档，不碰剪贴板，不发键盘消息：选区用 UIA 自己的 FindText + Select 做，再用 GetSelection 读回来比对。
// 不碰用户已经开着的会话：Edge 与 VS Code 用一次性的用户目录另起一份；记事本、Word 只在没在跑时才测（它们会把新文档
// 开进已有的窗口里），测完只关自己起的进程。
//
// 用法：dotnet run -c Release --project spikes\UiaSpike [-- --only notepad,edge,code,word]
// 结果：标准输出一张表，另存 %TEMP%\shiyu-uia-spike\result.md。

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

const int Trials = 20;

// 20 个要选的片段：中文、英文、数字、混排、标点、跨行，都在测试文档里各出现一次。
string[] snippets =
[
    "剪贴板历史",
    "clipboard history",
    "2026-10-16",
    "翻译 translation 混排",
    "拾语（Shiyu）",
    "Hello, world!",
    "离线词典",
    "selection capture",
    "第三段的开头",
    "UI Automation",
    "测试文档",
    "quick brown fox",
    "数字 12345",
    "email@example.com",
    "https://example.com/path",
    "最后一行",
    "Ctrl+C 兜底",
    "TextPattern",
    "选中的文字",
    "the end",
];

var lines = new[]
{
    "这是拾语（Shiyu）的 UIA 取词测试文档：剪贴板历史、离线词典。",
    "First line in English: clipboard history and selection capture.",
    "日期 2026-10-16，数字 12345，邮件 email@example.com。",
    "第三段的开头：翻译 translation 混排；链接 https://example.com/path 。",
    "Hello, world! The quick brown fox jumps over the lazy dog.",
    "UI Automation 的 TextPattern 能不能读到选中的文字？",
    "读不到就 Ctrl+C 兜底。",
    "最后一行，the end.",
};

var only = args.SkipWhile(arg => arg != "--only").Skip(1).FirstOrDefault()?.Split(',') ?? [];
bool Wanted(string app) => only.Length == 0 || only.Contains(app, StringComparer.OrdinalIgnoreCase);

var root = Path.Combine(Path.GetTempPath(), "shiyu-uia-spike");
var docs = Path.Combine(root, "docs-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(docs);

var txt = Path.Combine(docs, "uia-spike.txt");
File.WriteAllText(txt, string.Join("\r\n", lines), new UTF8Encoding(true));

var html = Path.Combine(docs, "uia-spike.html");
File.WriteAllText(html,
    "<!doctype html><meta charset=\"utf-8\"><title>uia-spike</title><body>"
    + string.Concat(lines.Select(line => "<p>" + System.Net.WebUtility.HtmlEncode(line) + "</p>"))
    + "</body>", new UTF8Encoding(false));

var rtf = Path.Combine(docs, "uia-spike.rtf");
File.WriteAllText(rtf, Rtf(lines), Encoding.ASCII);

var results = new List<(string App, string Status, int Ok, string Notes)>();

if (Wanted("notepad"))
{
    results.Add(Running("Notepad")
        ? ("记事本", "跳过：已经开着，不往用户的记事本里加标签页", 0, "")
        : Measure("记事本", "uia-spike", () => Process.Start(new ProcessStartInfo("notepad.exe", Quote(txt)) { UseShellExecute = true })));
}

if (Wanted("edge"))
{
    var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    var profile = Path.Combine(root, "edge-profile");
    results.Add(File.Exists(edge)
        ? Measure("Edge", "uia-spike", () => Process.Start(edge,
            $"--user-data-dir={Quote(profile)} --no-first-run --no-default-browser-check --new-window {Quote(new Uri(html).AbsoluteUri)}"))
        : ("Edge", "跳过：没装", 0, ""));
}

if (Wanted("code"))
{
    var code = CodePath();
    var profile = Path.Combine(root, "code-profile");
    var extensions = Path.Combine(root, "code-extensions");
    results.Add(code is not null
        ? Measure("VS Code", "Visual Studio Code", () => Process.Start(code,
            $"--user-data-dir {Quote(profile)} --extensions-dir {Quote(extensions)} --new-window --disable-workspace-trust {Quote(txt)}"))
        : ("VS Code", "跳过：没装", 0, ""));
}

if (Wanted("word"))
{
    var word = new[]
    {
        @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
        @"C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE",
    }.FirstOrDefault(File.Exists);
    results.Add(word is null
        ? ("Word", "跳过：没装", 0, "")
        : Running("WINWORD")
            ? ("Word", "跳过：已经开着，不往用户的 Word 里开文档", 0, "")
            : Measure("Word", "uia-spike", () => Process.Start(word, $"/q {Quote(rtf)}")));
}

var report = new StringBuilder();
report.AppendLine("| 应用 | 结果 | 读对 / 20 | 备注 |");
report.AppendLine("|---|---|---|---|");
foreach (var (app, status, ok, notes) in results)
{
    report.AppendLine($"| {app} | {status} | {ok} | {notes} |");
}

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine(report.ToString());
File.WriteAllText(Path.Combine(root, "result.md"), report.ToString(), new UTF8Encoding(true));
return 0;

(string App, string Status, int Ok, string Notes) Measure(string app, string titleHint, Func<Process?> launch)
{
    var before = Process.GetProcesses().Select(process => process.Id).ToHashSet();
    Process? started = null;
    try
    {
        started = launch();
        var seen = new List<string>();
        var window = WaitForWindow(titleHint, before, TimeSpan.FromSeconds(40), seen);
        if (window is null)
        {
            var titles = string.Join("；", seen.Distinct().Take(5).Select(Shorten));
            return (app, "窗口没等到", 0, $"40 秒内没找到标题含 {titleHint} 的新窗口；这期间的新窗口：{titles}");
        }

        Thread.Sleep(2500);  // 让文档渲染完
        var target = FindTextElement(window);
        if (target is null)
        {
            return (app, "没有 TextPattern", 0, "窗口里找不到支持 TextPattern 的控件");
        }

        var pattern = (TextPattern)target.GetCurrentPattern(TextPattern.Pattern);
        var supports = pattern.SupportedTextSelection;
        var ok = 0;
        var findFailed = 0;
        var wrong = new List<string>();
        foreach (var snippet in snippets.Take(Trials))
        {
            try
            {
                var found = pattern.DocumentRange.FindText(snippet, backward: false, ignoreCase: false);
                if (found is null)
                {
                    findFailed++;
                    continue;
                }

                found.Select();
                Thread.Sleep(80);
                var selection = pattern.GetSelection();
                var read = selection.Length > 0 ? selection[0].GetText(-1).Trim() : string.Empty;
                if (read == snippet)
                {
                    ok++;
                }
                else
                {
                    wrong.Add($"「{snippet}」→「{Shorten(read)}」");
                }
            }
            catch (Exception failure)
            {
                wrong.Add($"「{snippet}」→ {failure.GetType().Name}");
            }
        }

        var notes = $"选区模式 {supports}；控件 {target.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}"
            + (findFailed > 0 ? $"；FindText 找不到 {findFailed} 个" : string.Empty)
            + (wrong.Count > 0 ? "；读错：" + string.Join("，", wrong.Take(3)) + (wrong.Count > 3 ? $" 等 {wrong.Count} 个" : "") : string.Empty);
        return (app, ok == Trials ? "全对" : ok > 0 ? "部分" : "读不到", ok, notes);
    }
    catch (Exception failure)
    {
        return (app, "出错", 0, failure.GetType().Name + ": " + Shorten(failure.Message));
    }
    finally
    {
        // 只关这一趟新起的进程（含子进程）；用户原来开着的一个都不碰。
        foreach (var process in Process.GetProcesses().Where(process => !before.Contains(process.Id)))
        {
            try
            {
                if (IsOurs(process))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // 已经退出、或拿不到权限的进程：不是这一趟起的就本来不该碰。
            }
        }

        started?.Dispose();
    }
}

bool IsOurs(Process process)
{
    var name = process.ProcessName;
    return name.Equals("msedge", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Code", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Notepad", StringComparison.OrdinalIgnoreCase)
        || name.Equals("WINWORD", StringComparison.OrdinalIgnoreCase);
}

AutomationElement? WaitForWindow(string titleHint, HashSet<int> before, TimeSpan timeout, List<string>? seenTitles = null)
{
    var watch = Stopwatch.StartNew();
    while (watch.Elapsed < timeout)
    {
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                if (!before.Contains(window.Current.ProcessId))
                {
                    seenTitles?.Add(window.Current.Name);
                    if (window.Current.Name.Contains(titleHint, StringComparison.OrdinalIgnoreCase))
                    {
                        return window;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                // 窗口正在消失。
            }
        }

        Thread.Sleep(500);
    }

    return null;
}

AutomationElement? FindTextElement(AutomationElement window)
{
    var candidates = window.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
    AutomationElement? best = null;
    double bestArea = -1;
    foreach (AutomationElement candidate in candidates)
    {
        try
        {
            var rect = candidate.Current.BoundingRectangle;
            var area = rect.IsEmpty ? 0 : rect.Width * rect.Height;
            if (area > bestArea)
            {
                best = candidate;
                bestArea = area;
            }
        }
        catch (ElementNotAvailableException)
        {
            // 跳过。
        }
    }

    return best;
}

static bool Running(string name) => Process.GetProcessesByName(name).Length > 0;

static string Quote(string value) => "\"" + value + "\"";

static string Shorten(string text) => text.Length <= 24 ? text.ReplaceLineEndings(" ") : text[..24].ReplaceLineEndings(" ") + "…";

static string? CodePath()
{
    var candidates = new[]
    {
        @"D:\scoop\apps\vscode\current\Code.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"),
        @"C:\Program Files\Microsoft VS Code\Code.exe",
    };
    return candidates.FirstOrDefault(File.Exists);
}

static string Rtf(IEnumerable<string> paragraphs)
{
    var body = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Microsoft YaHei;}}\f0\fs22 ");
    foreach (var paragraph in paragraphs)
    {
        foreach (var ch in paragraph)
        {
            if (ch is '\\' or '{' or '}')
            {
                body.Append('\\').Append(ch);
            }
            else if (ch < 128)
            {
                body.Append(ch);
            }
            else
            {
                body.Append(@"\u").Append((short)ch).Append('?');
            }
        }

        body.Append(@"\par ");
    }

    return body.Append('}').ToString();
}
