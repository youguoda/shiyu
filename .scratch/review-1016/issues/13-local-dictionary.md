# 13 — 本地词典（ECDICT 常用词子集）

**来源：** 评审 F-08
**Status:** ready-for-human

**What to build:** 内置 ECDICT 常用词子集（中文释义、音标、考试标签），查词零延迟、离线；英文词先查本地，dictionaryapi.dev 降为例句补充，大模型仍兜底生僻词与中文词。许可与出处写进第三方声明。

**验收：** 断网划词 "example" 立即出中文释义与音标；体积增量写进 CHANGELOG。

## Comments

- 2026-10-16 实现：
  - 数据：ECDICT 固定提交 `bc015ed2…`（`ecdict.csv` SHA-256 `1a6947e0…`）。
    - 筛选规则：单个英文词、有中文释义，并且 oxford / collins / 考试标签 / 词频名次 ≤ 35,000 有其一。
    - 规模：42,431 个词头 + 30,765 条词形（went → go）。
    - 生成器在 `tools/dictionary/`（可复现，逐字节相同），说明与重新生成步骤见其 README。
  - 体积（用户要的实际数字）：`dictionary.db` 3,837,952 字节；安装包（LZMA2）约 +1.3 MB，zip（deflate）约 +1.8 MB。CHANGELOG 已写。
  - 查词顺序：Core `LocalDictionary`（只读 SQLite）→ 没有再走原来的免费词典 + 大模型兜底。
    - 键规整：小写、弯撇号、各种短横、首尾标点、's。
    - 音标换成 IPA 写法；领域行（[医]）拿领域当标签、排在通用义项后。
  - 本地卡先上屏，dictionaryapi.dev 的例句按词性补到对应义项下（`IDictionaryEnricher`，3s 预算，补不上就没有，不挡本地卡）。卡头新增「go 的变形 · 中考 · 高考 …」一行。
  - 许可：`THIRD-PARTY-NOTICES.md`（MIT 全文）随程序复制到安装目录；README 中英改了词典卡与联网说明。
  - 顺手修了一个旧 bug：词典卡的释义行是横向 StackPanel，长释义不折行、被裁掉，改成 DockPanel。
  - 测试 21 个（含对随程序发布那份 db 的抽查：example / went / ran / studies）。
  - 新探针 probe-panel-dictionary 进 run-all，3 项 PASS：冷查 6 ms、热查 0.3 ms；面板上 example、went 的卡都对。
  - 中文词仍由大模型生成（ECDICT 是英汉方向）。
- **待你实机验收**：断网（或拔网线）后划词 "example" → 立即出中文释义、音标、考试标签；划 "went" → 给出 go 并注明变形。
