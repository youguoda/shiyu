# 03 — 应用内更新：关掉更新窗后下载不停，再开窗两个下载抢暂存区

**来源：** 用户日志 2026-10-19 11:19 `UpdateApplyFailed window=1`：`UpdateStaging.Reset` 删暂存区时
`installer.zip.partial` 被另一个进程占着。
**Status:** ready-for-human

**What to build:**
- 链路：更新窗以 `CancellationToken.None` 下载；关窗只把 `_window` 清空，下载照跑、开着半成品。再开窗点
  「下载并安装」→ 新下载先 `Reset` → 删不掉抛 `IOException` → `catch (IOException)` 里又 `Reset` 一次 → 再抛，
  异常逃出 `DownloadAsync`「永远返回一句话」的约定。顺带的毛病：前一次下载在关窗后跑完，600 ms 后会自行重启拾语。
- Core：拒绝与失败路径改用不抛的 `UpdateStaging.TryReset`；`UnauthorizedAccessException` 一并收成「写入失败：…」。
  下载开始前那次 `Reset` 保持严格——清不掉就不该开始下。
- App：`UpdateWindow` 关闭即取消检查与下载；下完那一拍若窗口已关，不再重启。关窗导致的检查取消不进日志。

**验收：**
- [x] 新测试 `A_staging_area_held_by_an_earlier_download_is_refused_in_words`：开着半成品时 `DownloadAsync` 返回
  「写入失败」而不抛；放手后再下一次能装上。改前红（异常文本与用户日志一字不差），改后绿。
- [ ] **待你实机验收**（有新版本可装时）：托盘「检查更新」→「下载并安装」→ 下载途中关掉更新窗 → 再打开 →
  「下载并安装」：应正常下完并重启到新版；关窗期间拾语不应自己重启。

## Comments

- 2026-10-21 修复（b258521）。行为变化：关掉更新窗 = 这次不装了，下载随之停下（半成品已清掉，下次从头下）。
  若更想要「关窗后台继续下、下完提示而不自动重启」，告诉我再改。
