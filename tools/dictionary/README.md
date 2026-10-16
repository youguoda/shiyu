# 离线词典生成器

拾语的离线英汉词典（`src/Shiyu.App/Assets/dictionary.db`，评审 2026-10-16 F-08）由这里的小工具从
[ECDICT](https://github.com/skywind3000/ECDICT)（MIT，作者 Linwei / skywind3000）裁出来。独立工具，不在
`Shiyu.sln` 里，CI 不构建它；词典文件本身提交在仓库里，发布时随程序复制到安装目录。

## 现在这一份

| 项 | 值 |
|---|---|
| ECDICT 提交 | `bc015ed2e24a7abef49fc6dbbb7fe32c1dadaf8b`（2025-03-28；`ecdict.csv` 最后一次改动在 `82c98725…`，2025-01-02） |
| `ecdict.csv` SHA-256 | `1a6947e04785db63613a92e14903cdae7954f7e84860b10e68e5c7cbb3f9c3cf`（65,933,428 字节） |
| 入选规则 | 单个英文词且有中文释义，并且 oxford = 1、collins ≥ 1、有考试标签、或词频名次（frq / bnc）≤ 35,000 之一 |
| 词头 / 词形 | 42,431 / 30,765（examples → example，went → go） |
| `dictionary.db` | 3,837,952 字节，SHA-256 `fa08584ff635204adec10acda85f788d282cd12adf8432a4b95eebfb78214238` |
| 压缩后 | zip（deflate）约 1.8 MB，安装包（LZMA2）约 1.3 MB |

同样的 CSV、同样的参数，生成的文件逐字节相同（不写时间戳）。

## 重新生成

```powershell
# 1. 取固定提交的 CSV（约 63 MB，不要提交进仓库）
curl.exe -L -o ecdict.csv https://raw.githubusercontent.com/skywind3000/ECDICT/bc015ed2e24a7abef49fc6dbbb7fe32c1dadaf8b/ecdict.csv
(Get-FileHash ecdict.csv -Algorithm SHA256).Hash   # 应与上表一致

# 2. 生成并自检（回读行数、integrity_check、词形不与词头重叠、每个原形都是词头）
dotnet run -c Release --project tools\dictionary -- build ecdict.csv src\Shiyu.App\Assets\dictionary.db `
    --commit bc015ed2e24a7abef49fc6dbbb7fe32c1dadaf8b --rank 35000 --audit out\audit

# 3. 抽查
dotnet run -c Release --project tools\dictionary -- lookup src\Shiyu.App\Assets\dictionary.db example went polish
```

改了阈值或换了 ECDICT 提交，同步更新上表、`THIRD-PARTY-NOTICES.md` 里的提交与词数、以及 CHANGELOG 里的体积。

## 文件格式（App 照此读，见 `Shiyu.Core/LocalDictionary.cs`）

```sql
CREATE TABLE words(word TEXT PRIMARY KEY, phonetic TEXT, translation TEXT NOT NULL, tag TEXT) WITHOUT ROWID;
CREATE TABLE forms(form TEXT PRIMARY KEY, lemma TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;  -- 来源、提交、sha256、许可、计数
PRAGMA user_version = 1;
```

- 键一律小写。查词：去空白、小写、弯撇号换直撇号；先查 `words`，没有再查 `forms` 得到原形，再查 `words`。
- `translation` 每行一个词性：`n. 例子, 样本` / `vt. ...`；领域标注行如 `[医] ...`。
- `phonetic` 是 ECDICT 自己的记法：`'` 主重音、`,` 次重音、`:` 长音，央元音多用西里尔字母 `ә`（U+04D9），
  `^` 代 `ɡ`；约 15% 的词没有音标（NULL）。App 显示前换成 IPA 的写法。
- `tag`：`zk gk ky cet4 cet6 toefl ielts gre`，空格分隔。
