# 01 — 本机能构建、能测试

**来源：** 接手摸底（本机没有 .NET SDK，也没有 `gh`）
**Status:** done

**What to build:** 装 .NET 9 SDK，用 CI 的同一组命令构建与测试，拿到本机基线。

**验收：** `dotnet build Shiyu.sln -c Release` 0 错误；Core 与平台层两组测试的结果记进测试矩阵 §6。

## Comments

- 2026-10-21：`mise use -g dotnet@9.0.319`（装在 `%LOCALAPPDATA%\mise\dotnet-root`，下载约 16 分钟）。
  在 PowerShell 5.1 里 mise 的目录钩子不生效，命令行要能找到 `dotnet` 得让 `%LOCALAPPDATA%\mise\shims` 在 PATH 上（已在）。
- 基线：Release 构建 0 错误；Core 2107 用例里 1 个偶发红（`Concurrent_updates_on_the_same_field_are_all_counted`，
  单独跑 10/10 绿，全量负载下才现形）→ 票 02；平台层 26/26 绿（CI 从没跑过它们）→ 票 05。
- 生产代码唯一的编译警告（`BarModule.cs:78` CS8603）→ 票 13。修完后 App 0 警告。
- 探针 `run-all` 本轮没跑：它会在桌面上开窗，留到重构（票 08）前后对照时一起跑。
