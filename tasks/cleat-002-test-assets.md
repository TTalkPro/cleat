# cleat-002 引入测试资产

- 里程碑：M0　依赖：cleat-001　状态：未开始
- 设计依据：[09 §1](../designs/09-testing.md)

## 目标
把三套符合性资产放进仓库，建立运行它们的 runner 骨架（此时还没有实现，runner 只需能解析与计数）。

## 工作内容
1. 复制 jolt `test/chez/corpus.edn`、`test/conformance/known-divergences.edn`、`profile.edn`、`SPEC.md` 到 `tests/conformance/`，保留 MPL-2.0 头与来源说明；写 `tests/conformance/README.md`。
2. 以 git 子模块引入 jank 的 `clojure-test-suite`（`tests/clojure-test-suite/`），固定提交。
3. 从 ClojureCLR 复制 `Clojure.Tests/clojure/test_clojure/` 到 `tests/test_clojure/`（EPL 头保留），列出依赖的外部库（test.check、tools.namespace…）并标记第一版不跑的文件。
4. runner 规格：`tools/build.cs conformance` 读语料，按 `:portability` 过滤，对每条用宿主求值 `:actual` 与 `:expected`，`=` 比较，输出 `{:pass :fail :skip}` 与偏离表差异；现在只实现"解析 + 统计 + 偏离表核对"。

## 验收标准
- 语料解析出的用例数与 `profile.edn` 头部的计数一致。
- 子模块可 `--recurse-submodules` 克隆。
