# cleat-168 jvm-compat 可选垫片评估

- 里程碑：M19　依赖：无　状态：未开始
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
