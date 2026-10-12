# cleat-056 Clojure 表面的 nanopass

- 里程碑：后续　依赖：cleat-053　状态：未开始
- 设计依据：[07 D2 ~ D4](../designs/07-nanopass-port.md)（表面设计）、[15 §4](../designs/15-scheme-core-vendoring.md)

## 目标
让 Clojure 用户也能用 nanopass 写语言与 pass，而不重写框架：在 Scheme 侧给原版 nanopass 套一层 Clojure 语法（与 `(clojure syntax)` 实现特殊形式的方式相同；不依赖用户级卫生宏，cleat-057 已搁置）。

## 工作内容
1. `define-language` / `define-pass` / `nanopass-case` 的 Clojure 拼写（07 D2 ~ D4：`(out …)` 模板、`~` / `~@` / `~[…]`、向量与列表等价、`[L NT]` 签名），展开为对原版宏的调用；模板上下文切换直接利用 `let-syntax`（展开期词法作用域），不需要代码遍历器。
2. 语言记录的 Clojure 访问方式（`unparse`、`language->s-expression` 的 Clojure 数据版）。
3. 上游 `tests/` 的一个子集用 Clojure 拼写重写作为验收。

## 验收标准
- 用 Clojure 拼写写出的 pass 与 Scheme 版行为一致；`macroexpand` 对用户仍只显示 Clojure 数据。
