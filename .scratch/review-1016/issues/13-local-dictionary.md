# 13 — 本地词典（ECDICT 常用词子集）

**来源：** 评审 F-08
**Status:** ready-for-agent

**What to build:** 内置 ECDICT 常用词子集（中文释义、音标、考试标签），查词零延迟、离线；英文词先查本地，dictionaryapi.dev 降为例句补充，大模型仍兜底生僻词与中文词。许可与出处写进第三方声明。

**验收：** 断网划词 "example" 立即出中文释义与音标；体积增量写进 CHANGELOG。
