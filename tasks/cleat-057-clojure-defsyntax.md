# cleat-057 Clojure 的用户级卫生宏（defsyntax）

- 里程碑：后续　依赖：cleat-053　状态：未开始（2026-10-10 决定：先保证 Clojure 语法兼容，卫生宏不急）
- 设计依据：[13 §1](../designs/13-syntax-objects-and-scheme-core.md)、[14 §2](../designs/14-three-layer-architecture.md)（两种引用形式不能合并）

## 目标
在不改变任何现有 `defmacro` 行为的前提下，给 Clojure 用户提供 `defsyntax` / `syntax-case` / `syntax-rules`。

## 工作内容
1. 拼写：`defsyntax`、模式用向量 / 列表、`...` 省略号、卫生模板的引用形式（不能复用 reader 级的 `` ` ``，待定拼写）。
2. `syntax->datum` / `datum->syntax` 的 Clojure 版；与 `defmacro` 互调。
3. 示例与测试：`my-or` 不被捕获、`free-identifier=?` 比较字面量、与 `defmacro` 捕获对照。

## 验收标准
- 不改动 cleat-050 的任何兼容性测试；卫生示例通过；`macroexpand` 对用户仍只显示 Clojure 数据。
