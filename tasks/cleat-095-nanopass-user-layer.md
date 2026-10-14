# cleat-095 cleat.nanopass 第二层：给用户的特性与上游测试

- 里程碑：M8（不阻塞阶段 B / C）　依赖：cleat-084　状态：完成
- 设计依据：[19 D2](../designs/19-clojure-self-hosting.md)、[07 D2 ~ D4](../designs/07-nanopass-port.md)；取代 [cleat-056](cleat-056-nanopass-clojure-surface.md)

## 背景
cleat-084 原本包含两层；第一层（编译器用到的特性）在 cleat-084 完成，第二层不在关键路径上，拆到这里（2026-10-14）。

## 工作内容
1. 显式 catamorphism `~[f :> x -> y z]`（指定处理器、额外实参、多个返回值：处理器的返回值向量 `[v …]` 与解构）。
2. 多层省略号：模式与模板里的单项（不只是整段接入）在第二层以下也能用（cleat-084 只支持整段接入）。
3. `define-parser`（按语言生成 S 表达式 → 记录的函数；现在有按描述解释的 `cleat.nanopass/parse`）、`trace-define-pass` / `echo-define-pass`。
4. 不以关键字开头的产生式（上游允许，cleat-083 拒绝）、同一层多个 `...`（若上游测试需要）。
5. 上游 `tests/compiler.ss` / `tests.ss` 翻译为 `tests/nanopass/`，全部通过。
6. 用户文档：与 cleat-094 的 `cleat.nanopass` 文档合并。

## 验收标准
- 上游测试全部通过；宏展开结果确定。

## 结论（2026-10-15）

完成。`cleat.nanopass.pass` 重写了模式 / 模板的省略号处理，语言层加了 `=>` 与无关键字产生式；上游 `tests/unit-tests.ss` 与 `tests/compiler.ss`
翻译到 `tests/nanopass/cleat/upstream/` 全部通过（unit-tests 20 个测试 110 条断言；compiler 的 20 趟 pass 跑 alltests 的 164 + 30 个表达式）。

**pass 层**（`src/clojure/cleat/nanopass/pass.clj`）
- catamorphism 的完整形式：`~[y z …]`（处理器有多个返回值时依次绑定）、`~[x 实参 … -> y …]`（显式实参）、`~[f :> x 实参 … -> y …]`（指定处理器）。
- 多个返回值：处理器 `-> Expr [free*]` 返回向量 `[节点 free*]`；自动复制的子句返回 `[节点 返回值表达式…]`；pass 的缺省体丢掉多出来的返回值；
  `-> * [sexpr]` 返回一个值。
- 省略号区段里的子模式（含嵌套的产生式与更深的 `...`）：按区段的字段逐元素编译成 `(mapv (fn [元素…] …) 向量…)`，元素级的绑定变成列向量、
  元素级的 catamorphism 提到外层（depth + 1，guard 之后求值）；模板里带产生式的子模板按各 `~` 表达式（本层的序列）逐元素构造（`lifted-template`）。
- 处理器：输入可以是终结符或 `*`（普通的体）；形参带缺省值 `[ir [env ()]]`；按名字凑额外形参，凑不齐时按缺省值，再不行换别的处理器（上游的
  argument-name matching：P1 ~ P12）；catamorphism 选处理器时优先返回值个数、结果元变量的输出非终结符相符的；经非终结符交替到达的终结符交给
  该非终结符的处理器；缺子句而输出语言没有对应产生式的情况改为运行时报错（上游也是）。
- `trace-define-pass` / `echo-define-pass`、`pass-input-parser` / `pass-output-unparser`（pass 的 defn 带 `:nanopass/input` / `:nanopass/output` 元数据）。

**语言层**：`=>` 美化（终结符 → 函数，产生式 → 模板；`unparse` 第二个参数 `raw?`，`unparse-L` 同）；不以关键字开头的产生式（`(e0 e1)`，按形状匹配，
名字 appN）；同一关键字几个产生式按形状区分（`(if e1 e2)` / `(if e1 e2 e3)`，第二个起名字带序号）；`define-parser`。

**不做 / 改写**：带点对的产生式（`(e0 . e1)`，Clojure 没有非正规表）；错误信息原文的比较；以 `?` 结尾的元变量（写 `e2`）；`(true)` / `(false)`
产生式在翻译里叫 `(tru)` / `(fls)`（Clojure 的布尔字面量）；上游测试驱动的 `check-eval` 本来就是空操作，所以不需要 Scheme 解释器。

编译器（`cleat.compiler.*`）不改源码，但它用的 `cleat.nanopass` 变了，编译器包的字节随之变化：种子重生成（`clojure-seed`），不动点与基准见提交信息。

