# 02 — 设置保存：settings.json 本身被占住时丢掉这次改动

**来源：** 接手基线（全量 Core 测试偶发红）；review-1016 票 25 的续
**Status:** done（验收是确定性测试，实机没有更多可看的）

**What to build:** 0.17.2 的修复给临时文件起了唯一名字，但杀毒实时扫描与索引器占着的是**刚写完的目标文件**：
`File.Move(tmp, settings.json, overwrite)` 被拒，20 + 40 ms 的重试在机器忙时不够，三次都败就抛
`SettingsSaveException`——用户那一下改动没了，托盘说一句「设置没能写入磁盘」。

**验收：**
- [x] 新测试 `A_save_waits_out_a_brief_hold_on_the_settings_file`：另一线程不带删除共享地开着 settings.json 150 ms，
  期间保存应等过去并落盘、不留 `.tmp`。改前红（`UnauthorizedAccessException`，与全量偶发红同一个异常），改后绿。
- [x] 一直被占住时仍抛 `SettingsSaveException`、内存与磁盘都不动（原测试照旧绿）。
- [x] 全量 Core 连跑 5 遍 2108 全绿。

## Comments

- 2026-10-21 修复（cd0e769）：临时文件只写一次，只重试「替换目标」这一步，退避 25 / 50 / 100 / 200 / 400 ms
  （最坏约 0.8 s，正常第一次就成）；`finally` 里清掉没搬走的临时文件。保存跑在 UI 线程上，所以退避有上限：
  占得比这更久就是真拒绝。
