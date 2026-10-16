# 03 — 目标窗口以管理员运行：不贴，留在剪贴板并提示

**来源：** 评审 F-03 · v3 票 30（规格以票 30 为准）
**Status:** ready-for-agent

**What to build:** 见 `.scratch/shiyu-v3/issues/30-*.md`：Core 纯函数判定完整性级别（拿不到令牌按高于处理），Win32 薄壳取值；反向输入框与窄条快速粘贴接上；划词取词遇到提权窗口给提示而不是沉默。

**验收：** 管理员 PowerShell 里 Alt+Q 打字回车，看到提示，Ctrl+V 能贴出译文；普通窗口行为不变。
