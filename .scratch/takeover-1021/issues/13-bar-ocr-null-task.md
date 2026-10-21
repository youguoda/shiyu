# 13 — 窄条「复制图中文字」可能 await 一个 null 的 Task

**来源：** Release 构建警告 `BarModule.cs(78,32): CS8603`
**Status:** done

**What to build:** `_bar.OcrTextOf = id => shell.Ocr?.TextOfAsync(id)` 在 OCR 模块缺席时返回 null 的 Task，窄条一 await
就是空引用。眼下装配总会给 `shell.Ocr` 赋值，碰不到；改成缺席时给 `Task.FromResult<string?>(null)`，零成本拆雷。

**验收：** [x] App 的 Release 构建 0 警告。

## Comments

- 2026-10-21 修复（c953924）。
