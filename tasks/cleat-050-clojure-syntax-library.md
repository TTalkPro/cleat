# cleat-050 `(clojure syntax)` 库

- 里程碑：M4′　依赖：cleat-049　状态：未开始
- 设计依据：[13 §3](../designs/13-syntax-objects-and-scheme-core.md)、[03](../designs/03-language-core.md)（语义规格）

## 工作内容
1. 特殊形式（作为 `clj-special` 注册的核心宏）：`def` `if` `do` `let*` `loop*` `recur` `fn*` `quote` `var` `throw` `try` `monitor-enter` `monitor-exit` `.` `new` `set!` `letfn*` `case*` `deftype*` `reify*` `import*`，降到 Scheme 核心形式 + Clojure 扩展节点（cleat-051）；规则与错误信息以 cleat-013/014 的 `Expander.cs` 与 `ExpanderTests` 为规格。
2. `defmacro` 垫片（输入 `syntax->datum`、输出挂使用处上下文：与 Clojure 完全相同的不卫生语义）、语法引用（reader 级，不变）、`&form` / `&env`（`&env` 为符号 → 绑定的 map）、`:inline` / `:inline-arities`、`^:once`、`macroexpand` / `macroexpand-1` 返回裸 Clojure 数据。**不做** `defsyntax` / `syntax-rules`（用户可见的卫生宏推迟到 cleat-057；元层内部仍由 psyntax 卫生展开，用户看不到）。
3. 兼容性规格：`ExpanderTests` 的全部用例翻译为 Scheme 侧测试（Lcore 形状允许不同，语义断言相同）；`tests/test_clojure/macros.clj`、`special.clj`、`evaluation.clj` 与语料的 `macro` / `special-form` suite 作为回归。

## 验收标准
- 全部用例通过；`macroexpand-1` 对每个特殊形式返回原形式；现有 Clojure 宏（`core.clj` 全部 `defmacro`、`core.async` 风格的 `&env` 遍历宏、字符串拼符号的宏）语义与 JVM 一致。
