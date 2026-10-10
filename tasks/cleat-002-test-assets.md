# cleat-002 引入测试资产

- 里程碑：M0　依赖：cleat-001　状态：未开始
- 设计依据：[09 §1](../designs/09-testing.md)

## 目标
把三套符合性资产放进仓库，建立运行它们的 runner 骨架（此时还没有实现，runner 只需能解析与计数）。

## 工作内容
1. 复制 jolt `test/chez/corpus.edn`、`test/conformance/known-divergences.edn`、`profile.edn`、`SPEC.md` 到 `tests/conformance/`，保留 EPL-2.0 来源说明；写 `tests/conformance/README.md`。
2. 以 git 子模块引入 jank 的 `clojure-test-suite`（`tests/clojure-test-suite/`），固定提交。
3. 从 ClojureCLR 复制 `Clojure.Tests/clojure/test_clojure/` 到 `tests/test_clojure/`（EPL 头保留），列出依赖的外部库（test.check、tools.namespace…）并标记第一版不跑的文件。
4. runner 规格：`tools/build.cs conformance` 读语料，按 `:portability` 过滤，对每条用宿主求值 `:actual` 与 `:expected`，`=` 比较，输出 `{:pass :fail :skip}` 与偏离表差异；现在只实现"解析 + 统计 + 偏离表核对"。

## 验收标准
- 语料解析出的用例数与 `profile.edn` 头部的计数一致。
- 子模块可 `--recurse-submodules` 克隆。

## 结论（2026-10-10）

完成。
- `tests/conformance/`：jolt `corpus.edn`（jolt-lang/jolt `d9d454a2`）、`known-divergences.edn`、`profile.edn`、`SPEC.md`，附 `LICENSE-EPL-2.0.txt`（**jolt 是 EPL-2.0，不是设计文档初稿写的 MPL**，01 §6、09 §1、NOTICE 已改正）。实测语料 7168 行：`:common` 4842、`:jvm` 2324、2 行无 `:portability`（`java.io` 的 2 条，按 `:jvm` 处理）、1066 个 suite、230 条 `:throws`、1 对重复 id（jolt 自己也保留，runner 另按行号区分）。与 `profile.edn` 头部的 5754 + 237 不一致是语料在 profile 生成后又增长所致，验收标准按实测数字记录。
- `tests/clojure-test-suite/`：jank-lang/clojure-test-suite 子模块，固定 `95d4a91`（含 `deps-clr.edn`，253 个 `.clj*` 文件）。
- `tests/test_clojure/`：ClojureCLR `Clojure.Tests/clojure/test_clojure/` 84 个文件（EPL）。依赖的外部库：`clojure.test.generative`（4 处）、`clojure.data.generators`、`slam.hound.prettify`、`net.cgrand.enlive-html`（各 1 处）——第一版不跑 `generators.clj`、`pprint`、`clojure_xml.clj` 与依赖这些库的文件；`clojure.test-helper` 由 `Clojure.Tests/clojure/test_helper.clj` 提供，M6 时一并复制。
- runner：`dotnet run tools/build.cs -- conformance --stats` 解析语料（自带只认语料子集的 EDN 读取器）、按 `:portability` 统计、核对 jolt 偏离表的条目都在语料中（104 条，0 条过期）；对宿主求值在 cleat-018 接入。
