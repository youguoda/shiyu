# 21 — 定期加密备份

**来源：** 评审 F-20
**Status:** ready-for-human

**What to build:** 可选：每周一次自动备份到用户选的文件夹（复用现有备份格式与加密），保留最近 N 份（默认 4）；失败只记日志与托盘提示一次。

**验收：** 开启后到期即生成一份备份；超过 N 份删最旧的。

## Comments

- 2026-10-16 实现：
  - 设置 → 常规 →「定期备份」开关，下有「备份到」（文件夹 / 口令 / 上次备份与「立即备份一次」）与「保留份数」（默认 4，1–52）。
  - Core `AutoBackupSchedule`：每周一份、失败隔一天再试、只修剪自己命名的文件（「拾语自动备份 yyyy-MM-dd HHmmss.shiyubk」）。"上次"看文件夹里最新那份的文件名，不另记在设置里。
  - App `AutoBackupModule`：启动 2 分钟后看一眼、之后每小时，后台线程跑 `BackupArchive.Export`（与手动备份同格式同加密）。失败记日志 + 托盘提示一次（成功后才重新算）；「立即备份一次」的结果写在设置行上。
  - 口令：无人值守问不了，只能存。`BackupPasswordVault` 本机 DPAPI、单独 entropy、进出都是字节；设置里只放受保护的形式。不随备份走（ToBackupJson 去掉），恢复备份时定期备份整套保留本机的（KeepingAutoBackupOf）。
  - 测试：16 个 Core（时间表 / 修剪 / 设置）+ 3 个 Windows（口令往返、entropy 隔离）；新探针 probe-auto-backup（已进 run-all）在探针自己的目录里真跑一遍：写出加密的一份、修剪到 4 份、手动导出与别的文件不动、本机打不开的口令说人话且不写文件——3 项 PASS。
- **待你实机验收**：开「定期备份」→ 选一个空文件夹 → 设口令 → 点「立即备份一次」→ 文件夹里出现「拾语自动备份 ….shiyubk」；用「导入」+ 口令能读回来。

## 自动验收（2026-10-17，受你委托；probe-auto-backup，已进 run-all）

- **PASS flow:auto-backup-written**：立即备份一次 → 新出现一份**加密**备份（文件头 SHIYUBK1）；
- **PASS flow:auto-backup-pruned**：只留最近 4 份自动备份；手动导出的备份与无关文件原样在；
- **PASS flow:auto-backup-import**：把这份加密备份导进一个空库，17 条 + 3 张图**原样回来**（口令对了才打得开）；
- **PASS flow:auto-backup-locked**：口令换成这台机器打不开的，说人话、不写文件。
- 剩下唯一人工的一步：在你自己选的文件夹（比如同步盘）上点一次「立即备份一次」，看看文件落位。
