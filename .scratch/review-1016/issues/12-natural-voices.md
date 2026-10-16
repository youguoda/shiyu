# 12 — 朗读用自然音色

**来源：** 评审 F-10
**Blocked by:** 10
**Status:** ready-for-human

**What to build:** 朗读改走 WinRT `Windows.Media.SpeechSynthesis`，按译文语言挑音色，优先自然 / OneCore 音色，找不到回落 SAPI；朗读中按钮显示「停止」。

**验收：** 中文译文朗读用 OneCore / 自然音色；朗读中按钮状态可见。

## Comments

- 2026-10-16 实现：
  - 朗读先走 WinRT `Windows.Media.SpeechSynthesis`（新类 `WinRtSpeech`）：合成成音频流、交给 MediaPlayer 放，不进系统媒体控件。
  - 音色按译文语言挑（Core `SpeechVoicePick`）：名字带 Natural 的优先，其次这种语言的第一个；没有就回落老的 SAPI，WinRT 出错也回落。
  - 本机：WinRT 看得见 6 个 OneCore 音色（David / Zira / Mark / 慧慧 / 瑶瑶 / 康康），SAPI 只有 3 个 Desktop 音色；没有自然音色（Narrator 的自然音色系统不开放给这个接口）。
  - `SpeakingChanged` 事件：两枚朗读钮（底栏、词头）朗读中换成停止的字形与「停止」，读完换回。
  - 测试 3 个（挑音色）。
  - 新探针 probe-panel-speech 进 run-all：语音服务**静音**（音量 0，照常合成、照常报事件，探针不在机器上出声），点朗读后用的是 `winrt:Microsoft David`、按钮出现「停止」并在读完后换回，2 项 PASS。
- **待你实机验收**：翻一句英文成中文，点「朗读」——听得出是慧慧（OneCore）；朗读中按钮写着「停止」，点一下即停。
