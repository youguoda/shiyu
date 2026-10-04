# 06 — 全局异常处理、本地日志、静默 catch 治理

**来源：** 优化报告 O-05、O-24
**Blocked by:** 03（已完成）
**Branch:** `v3/log`（已并 master，5 提交 4f8094a→576d8be）
**Status:** done（0.9.0 验收轮通过后发布；验收记录见文末）

实施+验收记录（2026-10-02）：Log 门面 20 事件枚举、公开签名无 string 参数（反射测试钉住）、FileLogSink 按天滚动/保留 7 天/1MB 轮转/IO 失败静默；三处理器（UI 线程 Handled 挺住+托盘一次 5 分钟节流）；OnExit 先还剪贴板债；4 个 async void 全保护；catch 三分法 60 处（16 接日志/21 收窄/23 expected）+ CI 静态检查 `tools/checks/no-silent-catch.ps1`（对基线 60 违规先红、本分支绿）。Core 728。**实机验收（探针实例）**：`SHIYU_DEBUG_TICK_CRASH=1` 注入 Tick 异常 → 进程存活、`logs/shiyu-20261002.log` 出现 `error AppCrash ex=InvalidOperationException: debug tick crash probe` 含栈帧。

**What to build:**
- `DispatcherUnhandledException`、`TaskScheduler.UnobservedTaskException`、`AppDomain.UnhandledException` 三个处理器。
- Core 里一个 `Log` 门面：参数只接受事件枚举和异常，**不接受任意字符串**，从类型上杜绝把剪贴板内容、译文、密钥写进日志。
- 日志写到 `%LOCALAPPDATA%\Shiyu\logs\shiyu-yyyyMMdd.log`，保留 7 天，单文件不超过 1 MB。
- UI 线程上的非致命异常：记日志，托盘提示一次，继续运行。
- 4 个 `async void`（`App.ShowPanel`、`LibraryWindow` 两处、`PanelWindow.OnSwapDirection`）、更新窗"安装"、`AppSettings.Save` 都要有保护。
- `OnExit` 先归还划词借走的剪贴板，再做其他清理。
- 45 处静默 `catch (Exception)` 逐一归类：预期且无害的收窄类型并注释；预期外的记日志；影响用户的在界面上说明。
- CI 检查：`catch (Exception)` 块内必须有 `Log.` 调用或 `// expected:` 注释。

**验收：**
- [ ] 调试构建里的隐藏命令在 `DispatcherTimer.Tick` 中抛异常：进程存活，日志多一行，托盘提示一次
- [ ] CI 静态检查为绿
- [ ] 日志文件中搜不到任何测试用的剪贴板文本

**验收（2026-10-04，v3 收官）：** 用户在本机走完 0.9.0 验收轮——accept3 → accept18 构建、走查四至七轮；docs/manual-test-v5.md 是发版闸门（"全部通过即可发 v0.9.0 正式版"），验收中发现的问题已在发布前修复（如 B2 d2dd0ff、B4 f94f77e）——随后发布 0.9.0 / 0.9.1，用户确认关闭本票。机器可测部分：探针 28/0/1、测试全绿。关闭前的状态：ready-for-human（崩溃探针已实机验收通过）
