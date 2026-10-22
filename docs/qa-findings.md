# 拾语 QA 发现记录（调研报告 P0 执行）

- 记录人：测试开发（代理会话）
- 基线：master @ c934178（0.18.2 + 测试计划文档）
- 约定：只加测试代码，不改软件代码；每条发现带严重级（S1 致命 / S2 严重 /
  S3 一般 / S4 轻微 / T 可测性）、复现步骤、实际结果、建议。

---

## F-01 · T 可测性 · 翻译记录保留期边界无法在应用级直测

- **现象**：`TranslationLogger.Log(original, translated, origin, template)`
  无时间注入点（内部走 TimeProvider 盖「现在」戳）。应用级探针无法种下
  「31 天前的翻译记录」来测 `TranslationLogRetentionDays` 的清扫边界。
- **绕过**：store 层 `EntryStore.LogTranslation(..., at)` 带时间戳——探针
  改走 store 层回填（probe-retention 已用），UI 入口语义不受影响。
- **建议**：无需改产品。若未来要在应用级测更多时间维度（备份时间戳、
  保留期档位阶梯），可考虑给 DebugOverrides 加统一时钟偏移注入。
- **状态**：已绕过，P0-1 四项检查全绿。

## P0-1 · 保留期边界联动清扫——端到端通过（2026-10-23）

- 探针：probe-retention（两次启动驱动：第一次种边界数据并把三类保留期设为
  7 天、保护开关开；第二次启动的清扫判定）。
- 结果 **4/4 PASS**：
  - 31 天普通条目删除 ✓、3 天条目保留 ✓（flow:retention-history）
  - 31 天收藏条目/置顶条目/收藏图片条目及其原图文件全部保留 ✓（flow:retention-favorites）
  - 未保护的 31 天图片**原图文件**被删（缩略图承诺兑现）✓（flow:retention-images）
  - 回填的 31 天翻译记录被清扫 ✓（flow:retention-translation，rows=0）
- 已入 run-all。

## P0-2 · 备份还原到非空库——两种语义实测安全（2026-10-23）

- 探针：probe-backup-nonempty（导出当前库 → 三条「备份之后」条目让库与归档
  不同 → 并入一次、整库替换一次，全程记录）。
- 实测语义：overwrite=false = 并入（Added=0、SkippedExisting=18、既有条目全
  保留、库计数不变）；overwrite=true = 整库替换（归档 18 条精确落地、备份后
  的三条消失、图片原图回来）。全部或无（O-03）的承诺成立。
- 2/2 PASS，已入 run-all。

## P0-3 · 恶意内容链路——18 项单元全绿（2026-10-23）

- 新增 MalContentTests 18 条：危险 scheme（javascript:/file:/vbscript:/data:）
  一律不可点开（IsOpenable 的 http/https/mailto 白名单钉死）；恶意 Markdown
  （脚本注入、嵌套括号、1MB 文本）解析与 Plain 不抛；路径穿越串按纯存在性
  判定（不存在的目标 = 不可打开，无执行面）；\0/控制字符条目入库-查询往返
  无损。
- 初版两条失败系测试自身写错（Alive 对真实系统文件返回 True 是正确语义；
  临时库清理撞 SQLite 连接池延迟释放），已按真实语义修正——非产品缺陷。

## P0-4 · 更新中断——4 项单元全绿（2026-10-23）

- 新增 UpdateInterruptionTests 4 条（本地 TcpListener 假发布源 + 测试自产
  ECDSA 密钥对经构造器注入，不动内置公钥）：截断下载拒收并清暂存 ✓；校验和
  不符拒收并清暂存 ✓；缺 .sig 资产在任何下载前拒收 ✓；完整包正确暂存
  （staged + pending）✓。
- 注：应用级（托盘/更新窗）的中断呈现仍属手动验收项。

## P0-5 · soak 探针建成（2026-10-23）

- probe-soak.ps1 + DEBUG 命令 soak：混合负载（历史写入、翻译记录写入、预览
  开收）每 30 秒采样工作集/私有/GC/句柄；SHIYU_PROBE_MINUTES 参数（默认 5，
  8 小时过夜跑用 480）。2 分钟验证 PASS 2/2（完成 + 工作集漂移 0MB）。8 小时
  正式曲线待空闲机过夜跑；不入 run-all（时长不适合全量批次）。

## 最终全量（2026-10-23，P0 五项全部落地后）

- 单元：Core 2180 全绿 + Windows 26 全绿。
- run-all：227 项检查 PASS 226 / FAIL 0 / SKIP 1（唯一 SKIP = 同目录互斥的
  真实例证在场；用户在场时 bar-dock 全体让位属守卫设计）。
