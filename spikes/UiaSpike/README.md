# UIA 取词 spike（评审 2026-10-16 F-11 · D4）

量 `TextPattern.GetSelection()` 在记事本、Edge、VS Code、Word 的测试文档上读不读得到选区。
一次性工具，不在 `Shiyu.sln` 里；结论已写进 ADR-0003 与 `.scratch/review-1016/issues/09-uia-spike.md`。

## 用法

```powershell
dotnet run -c Release --project spikes\UiaSpike                 # 全部
dotnet run -c Release --project spikes\UiaSpike -- --only code  # 只测一个（notepad/edge/code/word）
```

## 方法（刻意的结构性测法）

- 每个应用一份**自己写的测试文档**（txt / html / rtf，20 个中英混排片段），放进 `%TEMP%\shiyu-uia-spike\docs-<随机>\`；
- **不碰剪贴板、不发键盘消息**：选区用 UIA 自己的 `FindText` + `Select` 做，再用 `GetSelection` 读回比对；
- **不碰用户开着的会话**：Edge 与 VS Code 用一次性的用户目录另起一份；记事本与 Word 只在没在跑时才测；
  测完只关这一趟新起的进程（按「启动前不存在的 PID」关，不按进程名）。

## 结果（本机 2026-10-17，Windows 11 26300）

| 应用 | 结果 | 读对 / 20 | 备注 |
|---|---|---|---|
| 记事本 | 全对 | 20 | 选区模式 Single；控件 Document |
| Word | 全对 | 20 | 选区模式 Multiple；控件 Document |
| Edge | 部分 | 4 | 选区整体偏 1–2 字符（「clipboard history」→「lipboard history」），Chromium 选区端点的已知毛病 |
| VS Code | 未能隔离 | — | 一次性用户目录的启动被并进已开着的实例（launcher 退出、无新进程、无新窗口），测不成 |

## 结论

UIA 取词**只在一部分应用里靠得住**（记事本、Word 这类原生文本控件），Chromium 系（Edge）的选区端点偏移是
系统性的，VS Code 无法隔离复测。与评审建议一致：**UIA 先试、Ctrl+C 兜底**——白名单里的应用走 UIA，
其余一切照旧走现有的 Ctrl+C 取词。
