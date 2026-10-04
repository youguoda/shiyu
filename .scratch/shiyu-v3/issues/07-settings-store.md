# 07 — 设置只有一个写入口（SettingsStore）

**来源：** 优化报告 O-07、O-20；审计 A2 §1（S1–S7）
**Blocked by:** 02
**Branch:** `v3/settings`（已并 master，70ac4b3）
**Status:** done（0.9.0 验收轮通过后发布；验收记录见文末）

实施记录（2026-10-02）：Core `SettingsStore`（b0d8af1）——`Update(mutate, path)` 锁内对磁盘真值做增量、写盘成功才前进 `Current`、失败抛专用 `SettingsSaveException`（内存与磁盘要么都改要么都没改）；`Load` 解析失败先把原文件改名 `settings.json.bad-<yyyyMMdd-HHmmss>` 再给默认值（`QuarantinedPath` 带回，App 在托盘出现后提示一次）；环境变量旁路（`SHIYU_RELAY_URL`）只叠加在 `Current`、从不落盘也从不出现在 mutate 面前；`Changed` 锁外广播且取广播时刻最新生效值（处理器里级联 Update 不死锁、不倒序）。App 接线（d86d145）——App 持唯一 store，三份分叉的 apply（App.Apply/引导回调/RestoreSettingsFromBackup）合并为唯一 `OnSettingsChanged`；窄条图钉改 `TopmostWanted(bool)` 单字段写入，几何防抖保存同样走 store（无变化早退）；设置窗保存只提交改过项（`SettingsBindings.IsUnchanged/ChangedOnly`）并订阅 `Changed` 让未动过的编辑器跟随最新值；引导完成同样只写改过项；面板语言改完即生效。Core 696 全绿（+10 新测试），App Debug 构建 0 警告 0 错误。开机自启保持既有语义（每次保存照 Windows 现状写回文件）。

**What to build:**
- Core 里的 `SettingsStore { Current; Update(Func<AppSettings, AppSettings>); event Changed }`：所有写入都是"在最新值上做增量"，保存后广播。
- 设置窗只把**改过的**项应用到最新值；窄条置顶钉只上报布尔值；启动引导、重跑引导、备份恢复都走同一个应用路径（现在有三份已经分叉的 apply）。
- 所有窗口订阅 `Changed`：面板的译文语言跟随设置；引导里改的热键、主题本次会话立即生效。
- 加载失败时，先把原文件改名为 `settings.json.bad-<时间>` 再写默认值，绝不覆盖。
- `SHIYU_RELAY_URL` 只在内存中生效，不再被写进文件。

**验收：**
- [x] Core 单测：两个调用方交错修改不同字段，两处修改都保留
- [x] Core 单测：加载损坏的文件时原文件被改名保留
- [ ] 实机：S1（设置窗开着点置顶钉再保存）**已于 2026-10-02 探针实例验收通过**（隔离数据目录 + UIA：开窗即 true → 图钉点击后 false → 设置窗"保存"后仍为 false，旧代码会回滚 true）；S2（引导期间唤出窄条后点置顶钉）——探针跳过引导，留待非探针隔离实例或用户；S3（导入后保存）、S4（重跑引导）同留
- [ ] 实机：改译文语言后不重启，下一次翻译即用新语言（需配置后端，随票 08 一并验）

**验收（2026-10-04，v3 收官）：** 用户在本机走完 0.9.0 验收轮——accept3 → accept18 构建、走查四至七轮；docs/manual-test-v5.md 是发版闸门（"全部通过即可发 v0.9.0 正式版"），验收中发现的问题已在发布前修复（如 B2 d2dd0ff、B4 f94f77e）——随后发布 0.9.0 / 0.9.1，用户确认关闭本票。机器可测部分：探针 28/0/1、测试全绿。关闭前的状态：ready-for-human（S1 已实机验收通过）
