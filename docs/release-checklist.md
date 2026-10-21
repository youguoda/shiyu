# 拾语发布检查清单

每次打 tag 发布前逐项过一遍。来源：Glossy 调研红旗教训
（docs/research/2026-09-27-glossy-analysis.md §7、票 38）。

## 版本与源码一致性（票 38 守则）

- [ ] tag 名就是版本号：release.yml 从 tag 取版本、以 `-p:Version=` 传给 publish（csproj 里不写版本），人工复核关于页显示的版本与 tag 一致
- [ ] release notes 里描述的**每个功能**，都能在**这个 tag 的源码**里指出来源
      文件/提交——描述超前于源码 = 红旗（Glossy v1.5.x 事故）
- [ ] CHANGELOG 已更新且与 release notes 一致，无 "TODO: translate" 残留

## 构建与产物

- [ ] `dotnet test Shiyu.sln` 全绿：Core 与平台层两组（release.yml 也各跑一遍，不过不发布）；用例数与上一版对比只增不减，基线记在 `docs/testing/test-map.md` §6
- [ ] 免费引擎探针（直连）通过：
      `powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\probe-free-engines.ps1 -Route Direct`
      ——必应与腾讯是非正式网页接口，随时可能变（edge.microsoft.com/translate/auth 就是
      一夜之间 404 的），要在发版前自己发现，而不是等用户来报（票 41、ADR-0013）。
      离线时是 SKIP，不算通过；红了先看是哪一家的哪一步，说明见 tools/probes/README.md
- [ ] release 构建来自 CI，不来自本地 bin 拷贝
- [ ] 安装包与 zip 各附 `.sha256` 与 `.sig`（release.yml 生成并用内置公钥复验）
- [ ] 更新通道（票 28）的 feed 指向新版本，老版本升级路径实测一次
- [ ] 启动后 1 小时的内存读数，与上一版对比（评审 2026-10-16 F-22：WPF 常驻本来就重，要防的是
      悄悄往上涨）。装好新版、照常用着放满 1 小时，读一次记进下面的表：
      `Get-Process Shiyu.App | Select-Object StartTime, @{n='WorkingSetMB';e={[int]($_.WorkingSet64/1MB)}}, @{n='PrivateMB';e={[int]($_.PrivateMemorySize64/1MB)}}`
      比上一版明显高（比如高出两成）先查原因再发

## 内容

- [ ] release notes 三段结构：新功能 / 修复 / 已知问题
- [ ] 隐私声明无变化时复核仍成立（"剪贴板历史不出机器"）
- [ ] 截图与实际界面一致（UI 大改后必须重截）

## 内存读数

每次发布记一行（上面「构建与产物」里那一项）。只比同一时长的读数。

| 版本 | 日期 | 启动后 | 工作集 | 私有 | 备注 |
|---|---|---|---|---|---|
| 0.16.1 | 2026-10-16 | 约 12 小时 | 106 MB | 184 MB | 评审 F-22 的读数，不是 1 小时读数；下一版起记 1 小时 |
| 0.17.0–0.17.4 | — | — | — | — | 五个版本发布时都没有记读数（2026-10-21 接手时补记缺口） |
| 0.17.5 | 2026-10-21 17:43 | 约 6.7 小时 | 107 MB | — | 接手时顺手读的，不是 1 小时读数；私有内存没读 |
| 0.17.5 | 2026-10-21 18:42 | 约 7.7 小时 | 168 MB | 294 MB | 同一进程一小时后：私有比 0.16.1 高约六成，过了「高出两成先查原因」的线——查因见 `.scratch/takeover-1021/issues/07-memory-and-summon.md` |
