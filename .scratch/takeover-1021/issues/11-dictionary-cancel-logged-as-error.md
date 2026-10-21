# 11 — 词典查询被取消也记成 error

**来源：** 用户日志 2026-10-19 16:13 `DictionaryLookupFailed leg=1 ex=TaskCanceledException`（LLM 词典那条腿）
**Status:** needs-triage

**现象：** `LlmDictionaryApi`、`FreeDictionaryApi`、例句补全三处都是 `catch (Exception)` 一律记 error。HTTP 超时与调用方
主动取消（关了面板、换了词）都抛 `TaskCanceledException`，日志里分不出来。影响只是词典卡少一块，不打扰用户。

**可能的修法：** 调用方已取消（`cancellation.IsCancellationRequested`）时安静返回 null、不记日志；真超时照记。
动手前先确认面板关闭时是否真的会取消这条查询。

## Comments
