# cleat-032 符合性基线：语料、clojure-test-suite、test_clojure

- 里程碑：M6　依赖：cleat-031、cleat-002　状态：未开始
- 设计依据：[09](../designs/09-testing.md)

## 工作内容
1. 语料 runner 跑新系统（编译执行）：`:common` 全量 + 我们新增的 `:clr` 行；偏离分类写入 `known-divergences.edn`。
2. clojure-test-suite：逐命名空间运行，建立 `tests/cts-baseline.edn`（通过 / 失败数），优于或劣于基线都报错。
3. test_clojure：可运行的命名空间集合与通过数。
4. 修复高价值失败（按语料 suite 排序），直到 `:common` 通过率 ≥ 99% 且剩余全部分类。
5. 把数字写入 09 §3。

## 验收标准
- 三个基线记入 09 §3；`dotnet test --filter Category=Conformance` 通过（按基线判定）。
