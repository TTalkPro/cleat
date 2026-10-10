# cleat-044 PoC：Clojure on IronScheme

- 里程碑：决策前　依赖：cleat-017　状态：未开始（待用户决定是否做）
- 设计依据：[13 §6](../designs/13-syntax-objects-and-scheme-core.md)

## 目标
用两周验证方案 C（Clojure 作为 psyntax 之上的库，IronScheme 编译器直接编译 Clojure）的五个不确定点，产出 13 §3 冲突清单的逐条结论与耗时数字，供在 A / B / C 之间决定。

## 工作内容
`spikes/cleat-044/`（Scheme 库 + C# 胶水，在 IronScheme 的宿主上运行；引用 Cleat.Core）：
1. psyntax 的 Clojure 集合遍历补丁；reader 数据 → 语法对象 → `syntax->datum` 往返。
2. 命名的顶层交互环境：`ns` / `in-ns` / `refer` / `alias` / 重定义 / `resolve`。
3. `(clojure syntax)` 最小库：`def` `fn*` `let*` `loop*` `recur` `if` `do` `.` `new`、`defmacro` 垫片、语法引用（quasisyntax 的 Clojure 集合版）；编译 `fib` 与一个用 `PersistentVector` 的函数到 CIL 并运行。
4. `defsyntax` + `syntax-case` 的卫生示例（`my-or`），与 `defmacro` 的捕获对照。
5. `ClojureFn : Procedure, IFn` 接缝；`try/catch` 按类型的核心形式原型。
6. 度量：展开 `core.clj` 前 1000 行的耗时；10 条冲突清单逐条结论。

## 验收标准
- 1 ~ 5 可运行；6 的数字与结论写回本文件与 13 §5；给出采用 / 不采用 C 的建议。
