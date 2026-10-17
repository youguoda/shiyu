# 09 — UIA 取词 spike

**来源：** 评审 F-11 · D4
**Status:** ready-for-human

**What to build:** 测 UIA `TextPattern.GetSelection()` 在记事本、Edge、VS Code、Office（装了才测）的测试文档上的覆盖率，每个 20 次；不碰聊天软件、不碰剪贴板。报告一张表，结论写进 ADR-0003。覆盖率够再另开实现票（UIA 先试、Ctrl+C 兜底）。

**验收：** spike 报告与 ADR-0003 的结论段。

## Comments

- 2026-10-17 实测（spikes/UiaSpike，本机 Windows 11 26300；只用测试文档，不碰剪贴板、不发键盘，选区用 UIA 自己的 FindText + Select 做再读回比对，各 20 次）：

  | 应用 | 结果 | 读对 / 20 | 备注 |
  |---|---|---|---|
  | 记事本 | 全对 | 20 | Single；Document 控件 |
  | Word | 全对 | 20 | Multiple；Document 控件 |
  | Edge | 部分 | 4 | 选区整体偏 1–2 字符（「clipboard history」→「lipboard history」），Chromium 端点毛病是系统性的 |
  | VS Code | 未能隔离 | — | 一次性用户目录的启动被并进已开着的实例（launcher 退出、无新进程、无新窗口），测不成 |

- **结论（照评审建议）**：UIA 先试、Ctrl+C 兜底。UIA 只对原生文本控件（记事本、Word）靠得住；Chromium 系端点偏移让它不合格；VS Code 无法隔离复测，按不可靠对待。实现票（另开）应该维护一个 UIA 白名单，其余一切照旧走现有 Ctrl+C 取词，白名单命中且取词失败时也回落 Ctrl+C。
- 工具保留在 spikes/UiaSpike（不在 sln 里），README 有方法与结果；重跑 `dotnet run -c Release --project spikes\UiaSpike`。
