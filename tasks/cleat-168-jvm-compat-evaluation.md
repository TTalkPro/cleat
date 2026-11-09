# cleat-168 jvm-compat 可选垫片评估

- 里程碑：M19　依赖：无　状态：取消（2026-11-05：用户决定不追求 Java 部分的完全兼容）
- 设计依据：[29 §1 第 3 条 / §7](../designs/29-usability-and-engineering.md)、[03 §4](../designs/03-language-core.md)

## 问题

偏离表（`tests/conformance/cleat-divergences.edn`）521 行中 408 行是 `:host` 类，主要是 Java 成员名和 Java 类。
"不模拟 Java 类库"是既定决策，本任务不推翻它，只评估一个**可选**、移植库时显式启用的垫片库能覆盖多少。

## 工作内容

1. 408 行 `:host` 偏离按三类分组：垫片可覆盖（成员名别名，如 `.length` / `.getMessage` / `.toString`；少量 `java.util` /
   `java.io` 类型映射）、需要改库、不可能（依赖 JVM 语义）。
2. 设计 `cleat.jvm-compat` 的启用方式：显式 require 后，在宿主调用的成员决议失败时查别名表（`Reflector` / 编译期决议两条路径），
   默认关闭，不影响不启用时的行为和性能。
3. 估算实现成本，以及启用后语料 `:common` 行的预计通过增量。

## 验收

- 分组统计与成本估计写进任务书；是否实施由用户裁决，实施另立任务。
- 本任务只做评估，不改产品代码。

## 取消（2026-11-05）

**用户决定**：cleat 不必完全兼容 Java 部分。"不模拟 Java 类库"（designs/03 §4）维持原状，也不做可选的 `cleat.jvm-compat` 垫片；
偏离表里 `:host` 类的 408 行继续按 `:host/jvm-api` 等类别登记，移植库时按 CLR 的写法改（`.Message`、`Int64/MaxValue`、`clojure.clr.io` …）。
本任务没有改过任何代码。相关的现状说明：README 的"与 Clojure 的偏离"一节（整数装箱模型在 cleat-166 时更正了措辞）。
