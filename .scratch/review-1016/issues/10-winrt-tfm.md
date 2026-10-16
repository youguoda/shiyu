# 10 — 目标框架改为 net9.0-windows10.0.17763.0

**来源：** 评审 D5（F-07、F-10 的前置）
**Status:** ready-for-human

**What to build:** Shiyu.Windows 与 Shiyu.App（及 Windows 测试）改目标框架；最低系统版本仍是 Win10 1809；CI、发布脚本、安装包冒烟跟着改；确认 WPF 与现有功能不受影响。

**验收：** 构建、测试、探针、安装包冒烟全绿。

## Comments

- 2026-10-16 实现：Shiyu.App、Shiyu.Windows、Shiyu.Windows.Tests 改为 `net9.0-windows10.0.17763.0`（Shiyu.Core 仍是 `net9.0`）。输出路径跟着变，探针的 lib.ps1 / seed.ps1、scan-release-overrides.ps1、README 中英已同步；CI 与发布流水线用 `dotnet publish -o publish`，不写死路径，不用改。
- 体积代价（实测，同一提交前后各发布一次）：
  - 发布目录 150.1 → 174.2 MB（+24.2 MB，几乎全是 `Microsoft.Windows.SDK.NET.dll` 23.6 MB 与 `WinRT.Runtime.dll`）；
  - zip 65.5 → 71.5 MB；
  - 安装包（ISCC 实编）48.3 → 51.8 MB。
- 验收：构建（3 条老警告，无新增）、测试全绿；整套探针 145 项：138 PASS / 5 FAIL / 2 SKIP。5 个 FAIL 是用户正在用桌面时抢不到前台的那几项（settings-front ×4、bar-keycap），同一时段 master 的对照构建同样 FAIL，与本改动无关。
  - 安装包冒烟不能在本机跑（本机装着真拾语，脚本会拒绝），推送后看 CI 的 installer job。
- **待你过目**：+3.5 MB 安装包是否接受（OCR 与自然音色都要它）；推送后 CI 的安装包冒烟。
