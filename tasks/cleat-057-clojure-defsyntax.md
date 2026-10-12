# cleat-057 Clojure 的用户级卫生宏（defsyntax）

- 里程碑：后续　依赖：cleat-053　状态：搁置（2026-10-12，见文末「搁置原因」）
- 设计依据：[13 §1](../designs/13-syntax-objects-and-scheme-core.md)、[14 §2](../designs/14-three-layer-architecture.md)（两种引用形式不能合并）

## 目标
在不改变任何现有 `defmacro` 行为的前提下，给 Clojure 用户提供 `defsyntax` / `syntax-case` / `syntax-rules`。

## 工作内容
1. 拼写：`defsyntax`、模式用向量 / 列表、`...` 省略号、卫生模板的引用形式（不能复用 reader 级的 `` ` ``，待定拼写）。
2. `syntax->datum` / `datum->syntax` 的 Clojure 版；与 `defmacro` 互调。
3. 示例与测试：`my-or` 不被捕获、`free-identifier=?` 比较字面量、与 `defmacro` 捕获对照。

## 验收标准
- 不改动 cleat-050 的任何兼容性测试；卫生示例通过；`macroexpand` 对用户仍只显示 Clojure 数据。

## 搁置原因（2026-10-12）

做过一版（提交 4636831，随即整体回退）：`clojure.syntax` 的 `defsyntax` / `syntax-rules` / `syntax-case`，特殊形式
`syntax-case*` / `syntax*` 加运行时模式与模板引擎，defmacro 垫片按符号对象跟踪上下文不同的标识符，boot 的 `add-mark` 递归进注解
（种子重生成）。测试全部通过，但结论是**意义不大：在 Clojure 里无法保证完整的卫生**。用户难以判断宏何时悄悄失去卫生，
这比明确地不卫生更糟。原因在语言层面，不是实现问题：

1. **数据层展开是 Clojure 的公开契约**：`macroexpand` 必须返回普通数据；`clojure.walk/macroexpand-all`、core.async 的 `go`、
   tools.analyzer 在数据层展开再求值，卫生宏经过它们标记即丢失。
2. **`defmacro` 本身不卫生**：卫生模板里几乎每个形式（`let`、`when`、`fn`…）都是 defmacro。按符号对象跟踪上下文只是启发式，
   宏按名字重建符号（`(symbol (name s))`）或用 `with-meta` 换掉符号就退回按名字解析。
3. 只有全程停在语法对象层时才有保证（Racket 的前提是整个生态都在语法对象上），Clojure 没有这个前提。

成本也不小：改 boot 展开器并重生成种子、defmacro 垫片多一条跟踪路径、reader 与特殊形式表多两项。
元层内部仍由 psyntax 卫生展开（cleat-050），不受影响。cleat-056 不再依赖本任务。
