# 11 — 图片能搜、能译：Windows OCR

**来源：** 评审 F-07
**Blocked by:** 10
**Status:** ready-for-agent

**What to build:** 用 `Windows.Media.Ocr`（离线）：① 图片入库后后台 OCR，文字进 FTS，卡片标「含文字」；② 剪贴板是图片时翻译剪贴板热键直接 OCR → 翻译；③ 窄条动作「复制图中文字」。OCR 不占 UI 线程。

**验收：** 复制一张含中文的截图，窄条搜其中的词能命中；翻译剪贴板出译文；探针断言输入延迟。
