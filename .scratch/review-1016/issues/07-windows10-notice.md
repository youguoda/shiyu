# 07 — Windows 10：安装时提示未验收，可继续

**来源：** 评审 F-13 · D6
**Status:** ready-for-human

**What to build:** 安装包在 Windows 10（build < 22000）上提示「拾语未在 Windows 10 上验收，可能有显示问题」，允许继续；README（中英）系统要求一句话说清。真机实测留待有虚拟机时。

**验收：** 安装脚本在 Win10 走提示分支（可用 build 号模拟测试）；README 系统要求清楚。

## Comments

- 2026-10-16 实现：`Shiyu.iss` 的 `InitializeSetup` 在 build < 22000 时弹「拾语还没有在 Windows 10 上验收过……仍要继续安装吗？」，默认「是」，静默安装照装、日志留一行。`/SMOKEWINBUILD=19045` 假装是 Windows 10（运行时参数，只决定要不要多问一句）。CI 冒烟（`smoke-installer.ps1`）的安装那一步带上它，核对日志里有提示、安装照常完成。README 中英、ADR-0015 第 10 条、CHANGELOG 已写。
- 本机实证（2026-10-16 16:44，本机 build 26300 模拟 19045）：日志依次是 `Windows build 19045 is before Windows 11` → `Message box (Yes/No)`（中文文案完整）→ `User chose Yes.` → `Starting the installation process.`，即提示、可继续都成立。
  - 教训：那次是在用户机器上真起了安装界面，被点了「是 → 安装」，差点升级掉真安装（被占用的 DLL 拦住、中止后回滚，0.16.1 完好，已核对）。以后安装包只靠 ISCC 编译 + CI 冒烟 + 日志验证，不在用户机器上起安装界面。
- **待你实机验收（有 Windows 10 虚拟机时）**：
  - 在 Win10 22H2 上双击安装包，开头出现这句提示，点「是」能照常装完；
  - 顺手跑一遍 run-all 与手工清单 A–D，看显示降级到什么程度；
  - 推送后看 CI 的 installer job 里多出的那条 `[PASS] on Windows 10 setup says it is not verified there, then installs anyway`。

## Win10 实测发现（2026-10-17，用户在 VMware 的 Win10 虚拟机）

- **窄条、反向输入框、翻译弹窗背景透明，透出桌面。** 根因：三扇壳窗的材质底 `Brush.SurfaceMaterial` 刻意半透明（等 DWM 在背后画 Acrylic），Win10 拒收材质后 `DegradeShell` 只补了描边+阴影+边距，没补不透明底。
- **修复**：`Backdrop.DegradeShell` 增加 `Brush.Surface` 不透明底（随主题走、幂等）——三扇窗与预览窗共用这一个咽喉。Win11 上的强制降级探针 `probe-degraded-shell`（已进 run-all）渲染核对：不透明底、12 DIP 边距带阴影、描边都在；PNG 人工过目。
- 待办：把修复带给 Win10 虚拟机复测（发 0.17.1 或自带构建）。
