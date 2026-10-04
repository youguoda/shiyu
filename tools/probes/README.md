# 拾语视觉探针（票 15 / O-26）

对五个界面（窄条、翻译面板、设置窗、管理窗、快速条）做截图与像素/UIA 断言的可重复探针。
一条命令跑完，结果只写 `%TEMP%\shiyu-probe-results`（截图 + `results.txt`），**仓库里只有脚本和本文档**。

## 怎么跑

```powershell
# 1) 先构建本工作树的调试版（探针模式只存在于 DEBUG 构建）
dotnet build src\Shiyu.App\Shiyu.App.csproj -c Debug

# 2) 跑全部（约 1 分钟）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\run-all.ps1

# 只跑某一个窗口
powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\probe-bar.ps1
```

输出是每个检查一行的 `PASS / FAIL / SKIP(原因)` 表，末尾有总数。
退出码恒为 0——红绿要看表，不看进程码。

### 结果怎么读

- `defect:*` 的 **FAIL 是好事**：它表示评审（`docs/review/2026-10-01-ui-design.md`）列出的已知视觉缺陷
  在当前代码上复现了（先红）。对应的修复票落地后，同一断言应当翻绿，变成回归护栏。
- `window:*` / `data:*` 的 FAIL 才是探针本身或应用出了问题（窗口没开、种子数据没读出来）。
- `SKIP` 附带原因，最常见的是「用户在场」跳过了需要真实鼠标的断言（见下）。

## 探针模式（应用侧）

探针跑的是**你自己工作树构建的** `Shiyu.App.exe`，靠两个环境变量与用户日常实例隔离（`App.xaml.cs`，`#if DEBUG`）：

- `SHIYU_DATA_DIR=<目录>`：数据目录与 `settings.json` 全部指向该目录（绝不读写用户真实设置）；
  单实例互斥量改为 `Shiyu-probe-<目录哈希>`，与用户的 `Local\Shiyu` 永不相撞（同目录的探针之间仍互斥）。
  同时**不注册热键、不装 Win+V/划词钩子、不监听剪贴板、不查更新、不弹首用引导**——用户实例不受任何影响。
- `SHIYU_PROBE_CMD=bar|panel|library|settings|quickbar`：启动即直接打开对应窗口（走与热键相同的内部方法）。
  `SHIYU_PROBE_ITEM` 让设置窗直接深链到某个设置项；`SHIYU_PROBE_TEXT` 给面板喂要翻译的句子。

`run-all.ps1` 开头有两项并存验证：由脚本自己握住 `Local\Shiyu` 互斥量再启动探针（模拟用户实例在场），
以及同目录双探针互斥（用户实例在跑时会自动 SKIP 这一项，避免广播唤起真窗口）。

## 在场检测（为什么有的断言会 SKIP）

悬停卡片的动作托盘需要**真实鼠标**（WPF 的 hover 只认真实指针），而抢一个正在用电脑的人的鼠标是不可接受的。
`lib.ps1` 的 `Test-UserPresent` 在交互前采样两次光标位置（间隔约 2 秒）：位置变了 = 有人在用 → 该断言记
`SKIP(原因)`，其余非侵入断言照常跑。唯一允许真实鼠标的操作就是这一处悬停；结束时会把光标放回原位。
（局限：在场但恰好没动鼠标会被误判为不在场；不在场但后台程序动了光标会被误判为在场。取安全侧。）

## 数据纪律

探针绝不用真实数据：`seed.ps1` 把数据目录重置为合成内容——12 条文本（中英混排、长/短、链接/邮箱/颜色/路径子类型）、
3 张程序生成的 PNG（免依赖的极小 PNG 编码器，不引 System.Drawing）、2 条文件列表、3 个标签、2 个分组、2 收藏、1 置顶；
正文一律「占位正文」字样。种子通过**临时编译的小工具**写入，该工具引用本工作树刚构建的 `Shiyu.Core.dll`、
直接调用应用自己的 `EntryStore` API——schema 永不漂移。生成器还会写一份探针专用的 `settings.json`
（自备密钥指向一个无 scheme 的地址，让面板翻译**确定性地**失败并抛出原始英文异常，不碰网络）。

## 脚本为什么 ASCII-only

PowerShell 5 对无 BOM 的非 ASCII `.ps1` 会按系统 ANSI 读出乱码，中文断言会静默失配。
因此本目录所有 `.ps1` 只含 ASCII；需要匹配非 ASCII 目标（如「←→」键帽）时，在运行时用码位构造：
`[string][char]0x2190 + [char]0x2192`。中文出现在 C# 种子源码里时同样写成 `\uXXXX` 转义。

## 断言清单（当前基线）

| 检查 | 目标缺陷（评审出处） | 当前 |
|---|---|---|
| `defect:bar-double-radius` | 外壳双圆角（§3.1 P0，实测弧半径 ~12 DIP，应与 DWM 的 8 对齐） | 红（测得 15.7 DIP，含描边/AA 系统偏置，阈值 13.5 为经验标定） |
| `defect:bar-keycap-arrows-clipped` | 按住 Ctrl 后「←→」键帽被裁成横线（§3.2 P0） | 红（徽章内墨迹仅 2.7 DIP 高） |
| `defect:bar-tray-delete-danger-color` | 删除钮危险色未生效（§3.4 P0，R1 吞色） | 红（删除与邻居字形同为 rgb 87,96,106）；需真实鼠标，在场则 SKIP |
| `defect:panel-raw-english-error` | 错误态透传 .NET 英文异常（§3.6 P0） | 红（'An invalid request...'） |
| `defect:panel-double-radius` | 面板外壳双圆角（§3.6 P0） | 红 |
| `defect:settings-segment-contrast` | 选中分段文字对比 <4.5:1（§3.9 P0，R1） | 红（2.98:1，字色恰为 #1F2328） |
| `defect:library-bottom-overlap` | 底部按钮组相撞（§3.8 P0） | 绿（当前构建默认宽度下未复现，作为回归护栏保留） |
| `defect:quickbar-tiny-main-text` | 主文本回落 12 DIP、小于自己的元信息行（§3.7 P0） | 红（行盒 16 vs 18.7 DIP） |
| `defect:quickbar-invisible-selection` | Enter 目标几乎看不见（§3.7 P0，Aero2 焦点样式） | 红（选中行 0 个令牌 accent 像素） |

## 怎么加新断言

1. 在对应 `probe-<window>.ps1` 里用现成积木：`Get-WindowShot`/`Save-Shot`（PrintWindow 截图）、
   `Get-UiaTree`（只读 UIA 枚举，元素带 Name/ClassName/Type/矩形）、`Send-ProbeKey`（PostMessage 键，
   **永远不要发 Enter**——Enter 会往用户的前台窗口粘贴）、`Get-Pixels`/`Get-Px`/`Get-CropPixels`（像素取样）、
   `[Shiyu.Probe.Pixels]` 里的 C# 快速像素分析（色块/墨迹盒/墨迹核心色/计数）。
2. 度量先于阈值：把测得值原样写进 detail（如对比度、半径、像素数），阈值给依据。
3. 命名：复现已知缺陷用 `defect:<window>-<short-name>`，FAIL=缺陷在；数据/环境健全性用其它前缀，FAIL=探针出问题。
4. 保持 ASCII：匹配中文 UI 时用序数特征（元素顺序/尺寸/码位构造的字符串），不要往 `.ps1` 里写中文字面量。

## 其它注意事项

- 探针宿主进程会先请求 Per-Monitor-V2 DPI 感知——没有它，跨显示器的窗口矩形/光标/UIA 坐标会被系统
  虚拟化，和 PrintWindow 的像素对不上（本机踩过：截出 2x2 的「窗口」）。
- 找窗口靠 pid + DIP 宽度（384/420/460/560/1150）匹配，因为 WPF 窗口类名是每实例随机 GUID；
  窗口的实际缩放由「测得宽度 / DIP 宽度」推导，不信 `GetDpiForWindow`（跨进程会报错值）。
- 窗口出现后会被挪到主屏固定位置再测量：窄条/面板类浮层默认开在光标旁，多屏机器上光标可能在任何一块屏。
- 输入法会跟着激活的搜索框在探针进程名下挂一个状态窗口（`FyPY_Status`），与探针窗口无关，宽度过滤天然排除。

## 免费引擎网络探针（票 41 / ADR-0013）

`probe-free-engines.ps1` 与上面那组视觉探针**不是一回事**：它是纯 PowerShell，**不启动拾语、不读它的数据**，
只对免费引擎依赖的两个**非正式**网页接口发真实请求——必应翻译页面（取会话）与 `ttranslatev3`、腾讯交互翻译
`transmart.qq.com/api/imt`。接口随时可能变形，这是发版前自己发现的办法（发布检查清单里有对应一行）。

```powershell
# 发版前的那一遍：直连，全部检查（约 17 个请求）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\probe-free-engines.ps1 -Route Direct

# 直连 + 走系统代理各一遍（Route 缺省就是 Both）；代理那一遍可以只做冒烟，3 个请求
powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\probe-free-engines.ps1 -Route SystemProxy -Quick

# 只取一次必应页面并存下来（HTML 夹具的来源；页面里有一枚真令牌，别原样提交）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\probe-free-engines.ps1 -Route Direct -SessionOnly -SavePage out.html
```

- **两条路径**：`Direct` 用 `HttpClientHandler.UseProxy = $false`；`SystemProxy` 用默认 handler（Windows 系统代理 /
  `HTTP(S)_PROXY`）。本机开着 127.0.0.1:7890 之类的代理时两条路径的结果真的不一样（经代理访问 www.bing.com 会
  跳到 cn；谷歌 gtx 直连超时、经代理首发 429），没开代理的大陆用户看到的是直连的结果。
- **检查项**：`bing:session`（页面能解析出令牌 / IG / 第一个 data-iid，TTL 是毫秒）、`bing:<源>-><目标>`（每种目标语言
  一条，同时校验译文的文字系统，所以"200 OK 但语言不对"不算过）、`tencent:<源>-><目标>`（同上，第一条用 `auto`
  源语言）、`info:tencent-auto`（TranSmart 接不接受 `auto`）、`info:tencent-empty`（`text_list` 里的空行是否原位返回）。
  语言码表是 `FreeEngineLanguages` 的副本，两边要一起改。
- **输出**：每项一行 `PASS / FAIL / SKIP`，带耗时（毫秒）；末尾汇总并报告发出的请求数。有 FAIL 时退出码为 1
  （与上面的视觉探针不同——这条要能当发版闸门用）。**离线**（没有任何可用网络接口）时 SKIP，退出码 0；
  网络在、接口连不上则是 FAIL——用户那边也是同样的结果。
- **别刷接口**：这是别人的服务器。完整一遍约 17 个请求（`auto` 被拒时 18），同一个目标引擎连续两次失败就不再问
  （剩下的项记 SKIP）；不要放进循环或定时任务。
- 与上面的视觉探针一样，脚本只含 ASCII：中文测试句在运行时用码位拼出来。
