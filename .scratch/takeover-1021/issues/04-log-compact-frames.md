# 04 — 日志的栈帧被截断，看不出崩在哪个界面

**来源：** 用户日志 2026-10-19 14:12 `AppCrash`（见票 06）
**Status:** done

**What to build:** `Log.FormatLine` 原来只留前 3 帧、最多 600 字。WPF 的第一帧光参数表就有四百多字，到第三帧就被
截没了。改为每帧压成「类型.方法 文件:行」（去参数表、去目录、跳过异步接缝行），一行最多 8 帧、900 字。

**验收：**
- [x] `A_layout_crash_keeps_enough_frames_to_name_the_list`：用那次崩溃的真实栈形状，压完能看到 `GroupItem.MeasureOverride`
  与应用自己的帧；`Compact_frames_stay_bounded`：40 帧只留 8 帧、不超长。LogTests 30/30 绿。
- [x] 「日志绝无正文」不受影响：帧里只有类型与方法名（原有的公开签名测试照旧绿）。

## Comments

- 2026-10-21 修复（2e9b508）。
