# 22 — 冷呼出复测

**来源：** 评审 F-21
**Status:** ready-for-agent

**What to build:** 用 `tools/probes/measure-cold-summon.ps1` 复测热路径与冷路径；热路径若 > 300 ms，轻量模式改为隐藏后延迟 5 分钟再释放。

**验收：** 复测数字写进票；超标则改完再测。
