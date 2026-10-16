# 10 — 目标框架改为 net9.0-windows10.0.17763.0

**来源：** 评审 D5（F-07、F-10 的前置）
**Status:** ready-for-agent

**What to build:** Shiyu.Windows 与 Shiyu.App（及 Windows 测试）改目标框架；最低系统版本仍是 Win10 1809；CI、发布脚本、安装包冒烟跟着改；确认 WPF 与现有功能不受影响。

**验收：** 构建、测试、探针、安装包冒烟全绿。
