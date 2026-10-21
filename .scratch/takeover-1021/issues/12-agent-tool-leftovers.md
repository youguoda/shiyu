# 12 — 仓库根的 agent 工具残留：.dag/ 与 .zcode/

**来源：** 接手摸底
**Status:** needs-triage（需要用户决定）

**现象：** `.dag/definition-index.json`（DAG 执行的输入索引，指向 v2 的几张票，最后更新 2026-09-27）与
`.zcode/plans/plan-sess_….md`（一次会话的计划，2026-10-02 起没动过）。`docs/dag-skill-assessment.md` 记录过 DAG 的评估。

**要用户回答：** 还用不用这两套工具？不用就删掉或加进 `.gitignore`；还用就留着，本票 wontfix。

## Comments
