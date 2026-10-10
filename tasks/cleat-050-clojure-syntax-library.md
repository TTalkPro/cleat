# cleat-050 `(clojure syntax)` 库

- 里程碑：M4′　依赖：cleat-049　状态：未开始
- 设计依据：[13 §3](../designs/13-syntax-objects-and-scheme-core.md)、[03](../designs/03-language-core.md)（语义规格）

## 工作内容
1. 特殊形式（作为 `clj-special` 注册的核心宏）：`def` `if` `do` `let*` `loop*` `recur` `fn*` `quote` `var` `throw` `try` `monitor-enter` `monitor-exit` `.` `new` `set!` `letfn*` `case*` `deftype*` `reify*` `import*`，降到 Scheme 核心形式 + Clojure 扩展节点（cleat-051）；规则与错误信息以 cleat-013/014 的 `Expander.cs` 与 `ExpanderTests` 为规格。
2. `defmacro` 垫片（使用处上下文）、语法引用（reader 级不变）、`defsyntax` / `syntax-rules`、`&env`、`:inline`、`^:once`。
3. `ExpanderTests` 的用例翻译为 Scheme 侧测试（Lcore 形状允许不同，语义断言相同）。

## 验收标准
- 全部用例通过；`macroexpand-1` 对每个特殊形式返回原形式；卫生示例（`defsyntax my-or`）与 `defmacro` 捕获对照成立。
