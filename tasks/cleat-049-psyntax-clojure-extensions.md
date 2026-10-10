# cleat-049 psyntax 的 Clojure 扩展（含原 PoC 判据）

- 里程碑：M4′　依赖：cleat-048　状态：未开始
- 设计依据：[14 §6](../designs/14-three-layer-architecture.md)、[13 §3](13-syntax-objects-and-scheme-core.md)

## 工作内容
1. 命名空间模型：带名字的交互环境 + ns 表；绑定种类 `clj-var`（`def` 建标签 → Var，重定义复用标签）；`refer` / `alias` / `import` / 私有性；`resolve` / `ns-map` 查同一张表；`in-ns` 切换。
2. 交互 rib 的哈希索引（`extend-rib!` 同步，`id->label` 先查索引）。
3. `macroexpand-1` API 与绑定种类 `clj-special`（不展开）。
4. 错误钩子：`ExpanderException` 文本与 `:clojure.error/*` 数据；宏体异常按 phase 包装。
5. 度量：合成 1000 个顶层定义后的标识符查找耗时。

## 验收标准（也是 14 §8 的通过判据）
- `(ns a) (def x 1) (ns b (:require [a :as q])) q/x`、重定义后 `resolve`、私有 Var 的拒绝；实现无特判堆砌（代码评审）；查找为 O(1)。不通过则按 14 §8 转方案 B。
