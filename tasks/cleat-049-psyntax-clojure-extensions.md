# cleat-049 psyntax 的 Clojure 扩展（含原 PoC 判据）

- 里程碑：M4′　依赖：cleat-048　状态：完成
- 设计依据：[14 §6](../designs/14-three-layer-architecture.md)、[13 §3](13-syntax-objects-and-scheme-core.md)

## 工作内容
1. 命名空间模型：带名字的交互环境 + ns 表；绑定种类 `clj-var`（`def` 建标签 → Var，重定义复用标签）；`refer` / `alias` / `import` / 私有性；`resolve` / `ns-map` 查同一张表；`in-ns` 切换。
2. 交互 rib 的哈希索引（`extend-rib!` 同步，`id->label` 先查索引）。
3. `macroexpand-1` API 与绑定种类 `clj-special`（不展开）。
4. 错误钩子：`ExpanderException` 文本与 `:clojure.error/*` 数据；宏体异常按 phase 包装。
5. 度量：合成 1000 个顶层定义后的标识符查找耗时。

## 验收标准（也是 14 §8 的通过判据）
- `(ns a) (def x 1) (ns b (:require [a :as q])) q/x`、重定义后 `resolve`、私有 Var 的拒绝；实现无特判堆砌（代码评审）；查找为 O(1)。不通过则按 14 §8 转方案 B。

## 结论（2026-10-10）

**判据通过：命名空间模型干净。** Clojure 的命名空间就是 `clojure.lang.Namespace`，展开器不另建表：每个命名空间对应一个 boot 的"查找环境"，顶层 rib 不存条目，标识符经一个过程现查 `Namespace` 的映射与别名；词法绑定照常在它之前遮蔽。boot 里加的都是与 Clojure 无关的通用挂钩，Clojure 的部分全在按需加载的 `(clojure namespaces)`。`(ns a) (def x 1) (ns b (:require [a :as q])) q/x` 得 1，重定义后先编译的引用看到新值、`resolve` 与展开器一致，查找不随定义数增长。

### 表示的选择

- **Clojure 符号 → 标识符**：wrap 前转换（`clj->scheme`）。`clojure.lang.Symbol` 变成驻留的 Scheme 符号，名字为 `Symbol.ToString()`（`x` 或 `ns/name`，与 Clojure 的打印 / 读入往返相同；`(symbol nil "a/b")` 这类病态符号不可逆，Clojure 自己打印也有同样的歧义），包成注解 `(make-annotation Scheme符号 源位置 原符号)`。boot 的 `id?` / `id->sym` 改看注解的 **expression**（Scheme reader 的原子注解 expression 与 stripped 相同，行为不变），`syntax->datum` 照旧取 stripped，于是得回原 Clojure 符号及其元数据（`^:dynamic`、`^:private`、`^Type`）。符号自己没有位置，取外层列表的 `:line` / `:column`，即 C# 展开器报错时的位置。
- **列表**：非空 Clojure 列表 / seq 转成 Scheme 列表，注解的 stripped 为原对象——psyntax 的应用、宏调用、`syntax-case` 都按序对工作，不用改；未被改动的部分 `syntax->datum` 得回原对象（`eq?`，`macroexpand-1` 对非宏调用返回同一个对象）。空列表 `()` 保持原对象，因为 Scheme 的 `'()` 就是 CLR `null`，即 Clojure 的 `nil`。向量 / map / set 用 048 的 `rebuild` 转换子项。模板产物里的 Scheme 符号 / 序对由 `scheme->clj` 转回（`syntax->clj` = 它 ∘ `syntax->datum`）。
- **标签**：每个 Var 一个 gensym（Var 为宏时用第二个），绑定 `(kind . value)` 放在标签的 symbol-value——就是 psyntax 已有的全局标签表（`imported-label->binding`），`label->binding` 不用改。Var 对象即身份：`(def x 2)` 拿到同一个 Var（`Namespace.intern` 的语义），标签不变；`refer` 进来的 `x` 与 `q/x` 解析到同一个标签（`free-identifier=?` 为真）。

### boot 镜像的改动（`core.sls` +95 行，其中代码 74 行；`syntax-ext.sls` 只改注释；构建脚本图例各 +2 行）

| 改动 | 说明 |
|---|---|
| `id?` / `id->sym` | 注解看 expression 而不是 stripped |
| `make-lookup-rib`、`find-label`、`extend-rib!` | rib 的 cache 字段可以是过程 `(sym mark*) → label \| #f`；`find-label`（`id->label` 本来就先查它）调用该过程；查找 rib 不能 `extend-rib!`。这就是 14 §6 要的 O(1) 索引——表在 `Namespace` 里（持久哈希映射），不在 rib 里 |
| `lookup-env` 记录、`make-lookup-environment` | 新的环境种类，`eval` / `core-expand` / `expand->core` / `environment?` 都认；顶层没有 interaction-env 的"伪造绑定"，解析不到即 unbound |
| `chi-expr` 的绑定扩展分支 | `expand-form` 为 `#t` 表示绑定值是转换器：走 `do-macro-call`（卫生标记照旧）；`expand-reference` 为 `#f` 时报 invalid expression 而不是崩溃 |
| `macro-step`、`environment-macroexpand-1` | 宏调用（`macro` 一族、`global-macro`、`expand-form #t` 且 `macroexpand-1?` 的扩展）展开一步并返回 datum，否则返回原对象；头部未绑定也返回原对象（Clojure 的 `macroexpand-1` 不报错） |
| 图例 `(ironscheme syntax-ext)` | 加导出 `make-lookup-environment`、`environment-macroexpand-1` |

**没有做**交互环境 rib 的哈希索引：Clojure 不走 rib 路径。实测 Scheme 的 `new-interaction-environment`（初始 rib 已有整个 `(ironscheme)` 的几千项）在 10 / 1000 / 3000 个 `define` 后展开一个最早定义的符号要 7.9 / 60 / 165 µs，线性；只影响 Scheme REPL，需要时另开任务。

种子按 05 §5 一步重生成：新镜像 `seed` 与旧种子 `seed --stage0` 逐字节相同（+12,885 / −12,748 行）；新种子构建出的镜像与旧种子构建的逐字节相同（dll `6cae4aec7c8ff2f4…`，lcil `d707bbe5f1859623…`）。

### 按需库：`(clojure namespaces)`（`src/scheme/clojure/namespaces.sls`，466 行，其中代码 351 行，不进镜像）

- 转换：`clj->scheme`、`scheme->clj`、`syntax->clj`、`clj-datum->syntax*`（使用处上下文）。
- 解析：`resolve-in`（照 C# `Expander.Resolve` 与 Clojure 的 `Compiler.maybeResolveIn`：别名 → `Namespace.find`、`FindInternedVar`、私有性、带点的类名、`ns` / `in-ns`、`GetMapping`），返回对象或错误原因；展开器的查找过程、`clj-resolve` / `clj-ns-resolve`（解析不到为 nil，私有抛 `InvalidOperationException`）和错误文本共用它，所以三者不会不一致。
- 绑定种类（经 048 的绑定扩展表）：`clj-var`（引用 → `getRawRoot`，动态 Var 为 `get`；`set!` → `Var.set`）、`clj-macro`（值为 defmacro 的最小垫片：剥成 Clojure 数据、`applyTo (&form &env args…)`、结果按使用处上下文转回；宏体异常按 C# 的规则包成 `:macro-syntax-check` / `:macroexpand` 阶段；引用宏的值报 `Can't take the value of a macro`）、`clj-type`（常量）、`clj-special`（`macroexpand-1?` 为 #f）。`register-clj-special!` 按未限定名字登记；本任务只登记了 `def`（照 C# `ParseDef` / `LookupVar(sym, true)`：在 `*ns*` 里 intern、`^:dynamic`、元数据带行列与文件、`Can't create defs outside of current namespace` 等报错）。
- 环境与入口：`namespace-environment`（每个 Namespace 缓存一个查找环境）、`clojure-eval` / `clojure-expand`（→ Lcore）/ `clojure-macroexpand-1`，都在 `*ns*` 里、在 `syntax-error-hook` 下运行。
- 运行时操作：`in-ns!`、`with-ns-binding`、`refer!`（拒绝私有 Var）、`alias!`、`import!`——它们只调 `Namespace` 的方法，展开器下一次查找即可见。
- 错误：`clojure-error-hook` 把 psyntax 的语法错误改抛 `Cleat.Core.Expand.ExpanderException`（`Syntax error (file:line:col) …`，数据为 `:clojure.error/*`）；文件取注解里的 `:file`，否则 `*file*`；"unbound identifier" 换成 `resolve-in` 给出的原因（`Unable to resolve symbol: nope in this context`、`No such namespace: q`、`No such var: q/zz`、`var: q/p is not public`）。

### 需要的"特例"（逐条）

1. **特殊形式先于命名空间映射、只认未限定名字**：单独一张表，查找过程先查它。这是 Clojure 自己的规则（`Compiler.specials`），不是适配 psyntax 的特判。与 Clojure 的差别：词法绑定能遮蔽特殊形式的名字（Clojure 里头部位置的 `def` 永远是特殊形式），留给 cleat-050 决定是否需要。
2. **`ns` / `in-ns` 在任何命名空间都解析到 `RT.NSVar` / `RT.InNSVar`**：照搬 Clojure 编译器自己的特例，在 `resolve-in` 里两行。
3. **每个 Var 两个标签（值 / 宏）**：绑定种类在查找时按 `Var.IsMacro` 选，因为 `setMacro` 可以发生在标签建立之后。
4. **查找不看标记**：Clojure 按名字解析；卫生宏引入的标识符的 subst 里没有命名空间 rib，不受影响（`let*` 测试里的宏引入名与用户名互不捕获由 psyntax 照常保证）。这是语义选择，不是分支。

除此之外没有别的分支：`refer` / `alias` / `import` / `intern` / `ns-unmap` 都是对 `Namespace` 的普通调用，展开器零改动即同步；词法遮蔽、重定义、`resolve` 一致性都是表示的直接结果。

### 测试（`tests/scheme/clojure/namespaces.sps`，57 项，约 15 秒）

`SchemeScriptTests` 改为也收录 `tests/scheme/clojure/*.sps`（测试名 `clojure/namespaces.sps`）。源码由 C# `LispReader` 读入（带行列）；clojure.core 未加载，`ns` / `:require [a :as q]` / `:refer` 由测试里的 `ns-form!` 调命名空间原语完成，`let*` / `set!` / `dummy-special` 用 `register-clj-special!` 临时登记（转换到 Scheme 的 `let` / `set!` / 常量）。覆盖：判据场景；`def` 返回 Var；重定义后先编译的引用得 2、标签不变、`resolve` / `ns-resolve` 返回同一 Var；`let*` 遮蔽 Var、嵌套遮蔽、遮蔽不影响 `q/x`；私有 Var（本 ns 可见、经别名 / 全名拒绝且文本与 Clojure 相同、`resolve` 抛错、`refer` 拒绝）；`refer` 一个 Var（`ns-map` 里是同一 Var、标签相同、未 refer 的不可见、本地 `def` 替换 refer 并警告）；`in-ns` 切换与别名作用域；`import` 与全名类型；`^:dynamic`、线程绑定下的读与 `set!`；错误文本（未解析符号带外层形式的 `file:line:col`、裸符号为 `0:0`、`No such var`、`*file*`、`:clojure.error/*` 数据、`def` 的报错）；`macroexpand-1`（宏 Var 一步一步展开、返回 Clojure 数据、在 `dummy-special` 和 `def` 处停、函数调用与不可解析的头部返回同一对象；宏值的错误）；符号 / 形式往返（元数据、`eq?`、标识符）；规模。宏函数暂用 map 充当（map 是 IFn，`(m &form &env)` = `(get m &form &env)`），因为还没有 `fn*`，也还没有 Scheme 过程 → IFn 的桥（14 §4 的函数值跨层）。

**度量**：10 / 1000 / 3000 个 `def` 后，一次标识符解析（查找过程：`Namespace.GetMapping` + Var → 标签）约 1.7 / 2.1 / 2.3 µs，完整展开一个 Var 引用（含 `clj->scheme`、`syntax-type`、绑定扩展生成 Lcore）约 38 / 39 / 39 µs——不随定义数增长（持久哈希映射的 log32 深度）。对照：Scheme 交互环境线性（见上）。单次引用的展开比 Scheme 符号（7.9 µs）重，主要是 `clj-var` 每次展开一个 `clr-call` 模板与注解；cleat-051 的 `var-ref` 节点会取代模板。

`dotnet test Cleat.slnx`：Cleat.Core.Tests 129 通过；Cleat.Scheme.Tests **219 通过**（218 + 新脚本，含自举不动点与 44 个快照），该程序集 3 分 46 秒。

### 留给后续

- 常量：Var、元数据 map 以 `(quote 对象)` 进 Lcore，只适用于进程内 `eval`；AOT 需要 cleat-051 的 `var-ref` 节点与加载时 intern（cleat-052）。
- Clojure 的字面量（关键字、`nil`、向量 / map 作表达式）在 psyntax 的 `syntax-type` 里是 `other`（`nil` 即 `'()`），需要 cleat-050 加常量挂钩；`quote` 要经 `syntax->clj`。
- defmacro 垫片是最小版：`&env` 为 nil，未做 `ArityException` 的参数个数修正与私有宏检查（cleat-050）。`Type/member`、`.method`、`Class.` 糖属于 cleat-050。
- 顶层 `(do …)` 拆分与形式中途 `in-ns`：加载器的事（cleat-052）；本模型每次查找读的是环境所属的命名空间，`clojure-eval` 用 `*ns*` 选环境。
- Var → 标签的表不是弱表，`ns-unmap` 掉的 Var 留在表里（量小，必要时换 `ConditionalWeakTable`）。
- 交互环境 rib 的哈希索引（Scheme REPL）未做，见上。

## 进度
- 2026-10-10 设计定稿：
  - Clojure 符号 → 标识符：wrap 前把 Clojure 数据转成 psyntax 数据（`(clojure namespaces)` 的 `clj->scheme`）：符号 → 驻留的 Scheme 符号（名字 = `Symbol.ToString()`，即 `ns/name`），包成注解 `(make-annotation Scheme符号 源位置 原Clojure符号)`；非空列表 → Scheme 列表（注解的 stripped 为原对象）；向量 / map / set 经 048 的 rebuild 转子项。`id?` / `id->sym` 改看注解的 expression（Scheme reader 的原子注解 expression 与 stripped 相同，行为不变），于是 `syntax->datum` 对未改动部分得回原 Clojure 对象（含元数据），模板产物里的 Scheme 符号 / 序对由 `syntax->clj` 转回。
  - 命名空间 = 环境：boot 加"查找 rib"（rib 的 cache 字段是过程 `(sym mark*) → label | #f`，`find-label` 调用它）与环境记录 `lookup-env`（`make-lookup-environment`，`eval` / `core-expand` / `expand->core` 都认）；每个 Namespace 一个环境，rib 的过程直接查 `Namespace` 的映射 / 别名（不复制表）。标签 = 每个 Var 一个 gensym（Var 为宏时另一个），绑定放在标签的 symbol-value（即 psyntax 现有的全局标签表），种类 `clj-var` / `clj-macro` / `clj-type` / `clj-special`。
  - 绑定扩展的 `expand-form` 为 `#t` 表示"绑定值是转换器"：`chi-expr` 走 `do-macro-call`（卫生标记照旧），新 API `environment-macroexpand-1` 只在 `macroexpand-1?` 为真时展开一步。
- 2026-10-10 boot 改动完成（`core.sls`：`id?` / `id->sym` 看注解 expression、`make-lookup-rib` 与 `find-label`、`lookup-env` 记录与 `core-expand` 分支、`chi-expr` 的 `expand-form #t`、`macro-step` / `environment-macroexpand-1`；两个构建脚本的图例加 2 个名字）；`dotnet build` 生成新镜像，048 的 35 项仍通过。下一步：`src/scheme/clojure/namespaces.sls` 与测试。
- 2026-10-10 `src/scheme/clojure/namespaces.sls`（`(clojure namespaces)`）与 `tests/scheme/clojure/namespaces.sps` 完成，57 项通过（约 15 秒）；`SchemeScriptTests` 改为也收录 `tests/scheme/clojure/`。查找耗时：10 / 1000 / 3000 个 def 后每次标识符解析约 1.7 / 2.1 / 2.3 µs，完整展开一个引用约 38 ~ 39 µs（不随定义数增长）；对照 Scheme 交互环境 10 / 1000 / 3000 个 define 后展开一个符号 7.9 / 60 / 165 µs（线性）。下一步：重生成种子、全量测试。
- 2026-10-10 种子已重生成：新镜像 `seed` 与旧种子 `seed --stage0` 结果逐字节相同（+12,885 / −12,748 行）；新种子构建出的镜像与旧种子构建的逐字节相同（dll `6cae4aec7c8ff2f4…`，lcil `d707bbe5f1859623…`）。下一步：全量测试。
- 2026-10-10 完成：全量测试通过（Core 129、Scheme 219）；结论已写，TASK.md 已改为完成。
