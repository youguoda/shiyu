# 05 — 平台层测试与 Release 产物扫描进流水线

**来源：** 测试矩阵 G-01（P0，10-16 起 open）；O-41（`scan-release-overrides.ps1` 从没进过流水线）
**Status:** ready-for-human（等推送后 CI 首跑的结果）

**What to build:**
- ci.yml 的 windows job：Core 单测之后跑 `Shiyu.Windows.Tests`（Core 红了也照跑，两份 trx 一起上传）；再扫描
  Release 的 `Shiyu.App.dll` / `Shiyu.Core.dll` / `Shiyu.Windows.dll`，有 `SHIYU_` 字面量即失败。
- release.yml：Core 与平台层测试**拆成两步**（pwsh 的多行 `run` 只看最后一条命令的退出码，合在一步里 Core 红了也会放行）；
  publish 之后扫描 `publish/` 下同样三个程序集，扫不过不发布。

**验收：**
- [x] 本机：平台层 26/26 绿；扫描反向验证——Debug 的 `Shiyu.App.dll` 报 FAILED（UTF-16 命中），Release 三个 clean；两份 YAML 能解析。
- [ ] 推送后 CI 的 windows job 绿；若平台层测试在构建机上红，按失败的那个测试决定修测试还是挪进探针。

## Comments

- 2026-10-21 实现（14feab8），本地提交，尚未推送。
