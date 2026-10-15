# cleat-097 刷新 clojure-test-suite 与 test_clojure 基线

- 里程碑：M9　依赖：cleat-095　状态：未开始

## 背景
`tests/cts-baseline.edn`（jank 的 clojure-test-suite：2026-10-11，6866 过 / 31 fail / 6 error）与 `tests/test-clojure-baseline.edn` 都是 M8 之前
在 Scheme 编译器上测的；自举后的 Clojure 编译器还没有对着这两套大基线验收。

## 工作内容
1. `tools/build.cs cts cts` / `cts test-clojure` 在当前编译器上重跑，与基线比较：新通过的下调基线（`--write`），回归逐条修。
2. 剩下的失败逐条分类（宿主差异 / 未实现 / 编译器缺陷），缺陷开后续任务或就地修。

## 验收标准
- 两条基线与当前结果一致（门禁通过）；每个失败在基线文件的注释或偏离说明里有归类。
