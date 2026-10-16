# 第三方声明

拾语随程序一起分发的第三方数据与它们的许可。

## ECDICT（离线英汉词典数据）

本软件内置的离线英汉词典数据摘自开源项目 ECDICT（https://github.com/skywind3000/ECDICT），作者 Linwei（skywind3000），以 MIT 许可证发布（Copyright (c) 2025 Linwei）。所用数据为该项目提交 bc015ed2e24a7abef49fc6dbbb7fe32c1dadaf8b 中的 ecdict.csv；拾语从中筛选出约 4.2 万个常用英文单词及其词形对照表，释义与音标基本照录（仅还原换行、整理空白），并转存为 SQLite 文件以便离线快速查询。MIT 许可证全文见下方。


- 来源：https://github.com/skywind3000/ECDICT
- 提交：`bc015ed2e24a7abef49fc6dbbb7fe32c1dadaf8b`（`ecdict.csv` 的 SHA-256：`1a6947e04785db63613a92e14903cdae7954f7e84860b10e68e5c7cbb3f9c3cf`）
- 拾语里的文件：`Assets/dictionary.db`（生成方法见仓库 `tools/dictionary/README.md`）

```text
MIT License

Copyright (c) 2025 Linwei

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
