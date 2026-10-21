# 25 — 设置保存对杀毒/索引器的瞬时拒绝不设防

**来源：** SettingsStoreTests 并发压测实录（2026-10-19，master 上同样红）
**Status:** ready-for-human

**What to build:** AppSettings.Save 用唯一临时名 + 瞬时拒绝（IOException/UnauthorizedAccessException）自动重试三次（20/40ms），仍失败才抛 SettingsSaveException。

**验收：** SettingsStoreTests 的并发压测（双线程各 250 次读改写）在杀毒实时扫描开启的机器上稳定通过，且目录里不留 .tmp 草稿。

## Comments

- 2026-10-19 实测：并发压测 3/3 红（`UnauthorizedAccessException: Access to the path is denied`），master 同样红——先于当天的工作存在。根因：500 次快速保存复用同一个 `settings.json.tmp` 名，杀毒实时扫描占着刚关上的文件一小拍，下一次 WriteAllText/Move 撞「拒绝访问」；一次瞬时拒绝 = 整次设置更新丢失（托盘一句人话）。机器负载越高越容易现形。
- 修复：每次保存唯一临时名（消除同名冲突）+ 三次重试（吸收瞬时拒绝，写入本身幂等不会双重生效）；成功即搬走不留草稿（压测加了「目录无 .tmp 残留」断言）。
- 复测：压测 3/3 绿；全套 2106 + 26 绿。
- 2026-10-21 接手复测：全量 Core 测试下并发压测仍偶发红（1/1 次全量里红 1 次；单独跑 10/10 绿）——同样是 `UnauthorizedAccessException`，但这回卡在 `File.Move` 覆盖 **settings.json 本身**：唯一临时名只解决了临时文件的冲突，扫描器占着的是刚写完的目标文件，20+40 ms 的重试在机器忙时不够。
- 修复（cd0e769）：临时文件只写一次，只重试「替换目标」这一步，退避 25/50/100/200/400 ms（最坏约 0.8 s，正常第一次就成）；新增确定性测试 `A_save_waits_out_a_brief_hold_on_the_settings_file`（另一线程不带删除共享地占住 settings.json 150 ms，期间保存应等过去并落盘），改前红、改后绿；全量 Core 连跑 5 遍 2108 绿。
