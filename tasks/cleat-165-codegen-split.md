# cleat-165 codegen 拆分：infer / classes

- 里程碑：M19　依赖：cleat-158、cleat-170　状态：未开始
- 设计依据：[29 §1 第 7 条 / §6](../designs/29-usability-and-engineering.md)、[19](../designs/19-clojure-self-hosting.md)

## 问题

`src/clojure/cleat/compiler/codegen.clj` 有 4140 行、369 个顶层定义，`cg*` 一个函数 330 行；
类型推导（`infer-walk`、`infer-var-types-into!`）与类生成（`genclass-class!`、`clj-fn-class!`）
混在其中；运行时签名入口由 [cleat-170](cleat-170-sigspec-removal.md) 删除。
analyzer 和 codegen 之间只有 loops / front / closures 三个 pass，
合计约 520 行。

## 工作内容

1. 拆出 `cleat.compiler.infer`：类型推导成为 codegen 之前的独立 pass，输出带类型注解的 L3（`languages.clj` 增加对应语言或注解约定）。
2. 拆出 `cleat.compiler.classes`：fn 类、deftype / reify / proxy / gen-class 的类生成。
3. `cg*` 按节点种类改成分派表或多方法，单个函数以约 80 行为指导值。
4. 同步 `all.clj`、`boot-order.edn`、`pipeline.clj`；更新 designs/04 的现状注记。

## 验收

- **纯重构**：拆分后 `clojure-seed` 的 stage3 与改前逐字节相同。如果做不到（例如定义顺序影响常量池编号），
  Lcil 文本差异只能出在可解释的符号名上，并在本任务书里逐处列出。
- 29 §9 的全套门禁；r7rs 只需 1 轮冒烟（产物等价时不需要配对）。
