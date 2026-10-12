# cleat-057 Clojure 的用户级卫生宏（defsyntax）

- 里程碑：后续　依赖：cleat-053　状态：完成（2026-10-12）
- 设计依据：[13 §1](../designs/13-syntax-objects-and-scheme-core.md)、[14 §2](../designs/14-three-layer-architecture.md)（两种引用形式不能合并）

## 目标
在不改变任何现有 `defmacro` 行为的前提下，给 Clojure 用户提供 `defsyntax` / `syntax-case` / `syntax-rules`。

## 工作内容
1. 拼写：`defsyntax`、模式用向量 / 列表、`...` 省略号、卫生模板的引用形式（不能复用 reader 级的 `` ` ``，待定拼写）。
2. `syntax->datum` / `datum->syntax` 的 Clojure 版；与 `defmacro` 互调。
3. 示例与测试：`my-or` 不被捕获、`free-identifier=?` 比较字面量、与 `defmacro` 捕获对照。

## 验收标准
- 不改动 cleat-050 的任何兼容性测试；卫生示例通过；`macroexpand` 对用户仍只显示 Clojure 数据。

## 结论（2026-10-12）

完成。`clojure.syntax`（按需 `require`，随附 `.clj.dll`）给 Clojure 用户 `defsyntax` / `syntax-rules` / `syntax-case` / `syntax` /
`with-syntax` 与 `syntax->datum`、`datum->syntax`、`identifier?`、`free-identifier=?`、`bound-identifier=?`、`generate-temporaries`。
现有 `defmacro` 行为不变（cleat-050 的 238 项兼容测试原样通过）。

```clojure
(ns demo (:require [clojure.syntax :refer [defsyntax syntax-rules]]))
(defsyntax my-or
  (syntax-rules ()
    [(_) false]
    [(_ e) e]
    [(_ e1 e2 ...) (let [t e1] (if t t (my-or e2 ...)))]))
(let [t 5] (my-or false t))   ;=> 5（同样的 defmacro 得 false）
```

### 做了什么

| 层 | 改动 |
|---|---|
| boot | `add-mark` 像序对一样递归进注解（宏输出的列表注解包着已带 wrap 的子项）；导出 `stx-mark*` / `stx-subst*`。种子一步重生成，镜像生成的种子与重建后再生成的逐字节相同 |
| `(clojure namespaces)` | defmacro 垫片跟踪 wrap 与头部不同的标识符（按符号对象身份，引用相等的表），输出里原样放回；全部同 wrap 时走原快速路径。`:hygienic` 宏走语法对象路径（`mixed->syntax`）。顶层预展开遇卫生宏头部停止。宏输出里的裸注解也能报位置 |
| `(clojure syntax)` | 特殊形式 `syntax-case*` / `syntax*`（模式与模板为 Clojure 数据常量，可 AOT）；运行时引擎：模式 → `syntax-dispatch` 树（缓存）、模板实例化（定义处命名空间的顶层上下文、省略号多层、`(... ...)` 转义）；`datum->syntax` 的上下文可为标识符、任何语法对象或命名空间 |
| C# | `SyntaxOps`（引擎的槽与 clojure.syntax 的入口）、`ClojureOps.SyntaxMatch` / `SyntaxNoMatch` / `SyntaxTemplate` / `IdentityTable`；reader 的特殊形式表加两项 |
| Clojure | `src/clojure/clojure/syntax.clj`，`boot-order.edn` 登记为 `:boot false` |

### 测试

- `tests/scheme/clojure/syntax.sps` +23 项（共 261）：不捕获对照 defmacro、经 defmacro（`when2`）的模板仍卫生、字面量按
  `free-identifier=?`（局部同名的 `else` 不匹配）、嵌套省略号、条件、`datum->syntax` 有意捕获、`(... ...)`、集合模板、
  `macroexpand-1` 只给数据、两个新特殊形式是停止点、五种错误文本。
- `tests/Cleat.Host.Tests/HygienicMacroTests.cs` 4 项（子进程）：从源码跨命名空间使用（模板的自由名字 `helper` 按定义处解析，
  不被使用处的同名函数或局部捕获）；顶层使用与 `macroexpand`；两个命名空间 AOT 后删源码、新进程加载使用；错误是 Clojure 的语法错误。

- `dotnet test Cleat.slnx`：601 全部通过（Core 129、Scheme 233、Examples 1、Host 238，含两段自举不动点）。

### 已知限制

- `macroexpand` / `macroexpand-1` 返回数据，引入的标识符按名字显示；在数据层展开的代码遍历器（`clojure.walk/macroexpand-all`、
  `core.async` 的 `go`）里，卫生宏的展开会丢卫生。
- 宏用 `(symbol (name s))`、`with-meta` 重造用户的符号时，跟踪不到，退回按名字解析（与今天的 defmacro 相同）。
- 模板错误（省略号深度不够）在使用时才报。

## 进度
- 2026-10-12 设计定稿（分析后的三个决定）：
  - **拼写**：新命名空间 `clojure.syntax`（按需 `require`，不进 clojure.core，不改现有 Clojure 表面）：`defsyntax`、`syntax-rules`、
    `syntax-case`、`syntax`（模板）、`with-syntax`、`syntax->datum`、`datum->syntax`、`identifier?`、`free-identifier=?`、
    `bound-identifier=?`、`generate-temporaries`。模式用 Clojure 列表 / 向量，`_`、`...`、字面量表；子句是向量 `[模式 模板]` /
    `[模式 条件 体]`。模板的引用形式不复用 reader 级的 `` ` ``（14 §2）：`(syntax 模板)`。两个新特殊形式 `syntax-case*`、`syntax*`
    （与 `fn*` / `let*` 同一约定，宏展开到它们；`macroexpand-1` 停在它们上）。卫生宏的 Var 元数据 `:hygienic true`、`:macro true`，
    根值是"语法对象 → 语法对象"的普通 fn。
  - **运行时引擎而不是 psyntax 的 `syntax-case` 代码生成**：模式与模板留作 Clojure 数据常量（`$clj-call "SyntaxMatch"` /
    `"SyntaxTemplate"`），匹配用 psyntax 的 `syntax-dispatch`（模式树首次使用时由数据转换并缓存），模板在使用时实例化：
    模板标识符挂**定义处命名空间的顶层上下文**（隐藏形式 `%topid` 在该命名空间环境里求值得到的标识符），模式变量代入匹配到的
    语法对象，再由 `do-macro-call` 加新标记。这样定义卫生宏的命名空间可以 AOT（psyntax 生成的代码会把带查找 rib 的语法对象当常量，
    进不了 fasl），`syntax-case` 的体是普通 Clojure 代码（模式变量是局部，kind 为 `(pattern . 深度)`），不用改 boot 的编译器。
  - **defmacro 垫片要保住经过它的标识符的上下文**（本任务真正的难点）：Clojure 几乎所有形式（`let`、`when`、`fn`…）都是 defmacro，
    卫生模板里的 `(let [t e1] …)` 经垫片"剥成数据、整个输出挂头部上下文"后，模板的 `t` 与用户的 `e1` 都变成 `let` 头部的 wrap——
    卫生全丢，而且用户子形式还解析不到自己的局部。改为：垫片先遍历输入，把 wrap 与头部不同的标识符按**符号对象**（reader 给每个
    出现各一个对象，strip 得回原对象）记下语法对象；宏输出里同一个符号对象原样放回，其余符号照旧挂头部上下文。全部标识符与头部同
    wrap（普通 Clojure 代码）时走原快速路径，所以现有行为与性能不变。宏用 `(symbol (name s))` / `with-meta` 重造的符号退回按名字
    解析——与今天相同。输出的列表 / 集合保留注解（位置、stripped 为输出数据本身），为此 boot 的 `add-mark` 要像序对一样递归进注解
    （cleat-057 唯一的 boot 改动，另导出 `stx-mark*` / `stx-subst*`；种子重生成一步，不动点成立）。
  - 顶层求值的数据级预展开（`Compiler.eval` 的 macroexpand）遇到卫生宏头部停止，交给语法对象路径；用户的 `macroexpand` /
    `macroexpand-1` 照旧返回 Clojure 数据（引入的标识符按名字显示）。
