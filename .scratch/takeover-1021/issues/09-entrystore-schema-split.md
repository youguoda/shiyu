# 09 — EntryStore 的建表与迁移拆成独立的 partial 文件

**来源：** 接手摸底（`EntryStore.cs` 1765 行，其中约 500 行是 schema v17 的建表与逐版迁移）
**Status:** ready-for-agent

**What to build:** `EntryStore` 已经是 partial（`EntryTags` / `EntryGroups` / `EntryBackup` / `TranslationLog`…各一个文件），
把 `CreateSchema` / `Migrate` / `ReadSchemaVersion` 及其常量搬进 `EntryStoreSchema.cs`。**只搬家，SQL 一个字不改。**

**验收：**
- [ ] 迁移相关测试（`SchemaMigrationTests`、`ImageSizeMigrationTests`、`StorageScaleTests`、`SubtypeStoreTests`、`OcrStorageTests`、`VocabularyTests`）与全量 Core 全绿。
- [ ] `git diff --color-moved` 显示迁移代码是整块搬动。

## Comments

- 迁移路径有逐版本测试守着（v2、v10–v16 都有用例），拆的风险低；测试矩阵 G-11（升级黄金 fixture）仍 open，可顺手补。
