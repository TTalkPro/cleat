# cleat-050 `(clojure syntax)` 库

- 里程碑：M4′　依赖：cleat-049　状态：完成
- 设计依据：[13 §3](../designs/13-syntax-objects-and-scheme-core.md)、[03](../designs/03-language-core.md)（语义规格）

## 工作内容
1. 特殊形式（作为 `clj-special` 注册的核心宏）：`def` `if` `do` `let*` `loop*` `recur` `fn*` `quote` `var` `throw` `try` `monitor-enter` `monitor-exit` `.` `new` `set!` `letfn*` `case*` `deftype*` `reify*` `import*`，降到 Scheme 核心形式 + Clojure 扩展节点（cleat-051）；规则与错误信息以 cleat-013/014 的 `Expander.cs` 与 `ExpanderTests` 为规格。
2. `defmacro` 垫片（输入 `syntax->datum`、输出挂使用处上下文：与 Clojure 完全相同的不卫生语义）、语法引用（reader 级，不变）、`&form` / `&env`（`&env` 为符号 → 绑定的 map）、`:inline` / `:inline-arities`、`^:once`、`macroexpand` / `macroexpand-1` 返回裸 Clojure 数据。**不做** `defsyntax` / `syntax-rules`（用户可见的卫生宏推迟到 cleat-057；元层内部仍由 psyntax 卫生展开，用户看不到）。
3. 兼容性规格：`ExpanderTests` 的全部用例翻译为 Scheme 侧测试（Lcore 形状允许不同，语义断言相同）；`tests/test_clojure/macros.clj`、`special.clj`、`evaluation.clj` 与语料的 `macro` / `special-form` suite 作为回归。

## 验收标准
- 全部用例通过；`macroexpand-1` 对每个特殊形式返回原形式；现有 Clojure 宏（`core.clj` 全部 `defmacro`、`core.async` 风格的 `&env` 遍历宏、字符串拼符号的宏）语义与 JVM 一致。

## 结论（2026-10-10）

完成。Clojure 的 21 个特殊形式、字面量、调用、宿主互操作语法糖与 defmacro 垫片都在按需库 `(clojure syntax)` 里，**展开为 Scheme 核心形式 + C# 实现的运行时过程，今天就能在 Scheme 镜像上编译运行**；只有 `deftype*` / `reify*` 等 cleat-051。规则与错误文本以 C# 展开器为规格逐条照搬，`ExpanderTests` 的全部分组翻译为语义断言。

### 特殊形式展开成什么

| 形式 | 展开结果 | 今天可运行 |
|---|---|---|
| `def` | 展开期按 `LookupVar(sym, true)` intern（`^:dynamic` 即时 `setDynamic`、`:arglists` 去引号、earmuffs 警告写 `*err*`）；运行时 `bindRoot`、`setMeta`（元数据 map 作为表达式求值，含 `:line` / `:column`（long）/ `:file` / `:doc`），值为 Var；初值把 Var 名作为 fn 的名字 | 是 |
| `if` | `(if (let ((t 测试)) (if (null? t) #f t)) then else)` | 是 |
| `do` | `begin`（非末尾形式不在尾位置）；`(do)` 为 nil | 是 |
| `let*` | `let*`（每个初值能看到之前的绑定） | 是 |
| `loop*` / `recur` | `(let* 绑定 (let 标签 ((x x) …) 体))`；`recur` = 调用标签，只允许在尾位置（IronScheme 降为循环）；参数个数、尾位置、跨 try 三种报错 | 是 |
| `fn*` | `ClojureOps.MakeFn` → `ClojureFn`；固定参数子句合成一个 `case-lambda`，变长子句单独一个过程（剩余参数为 ISeq / nil）；每个子句体是命名 let（recur 回到子句开头，变长子句的 recur 直接传剩余 seq）；自名用 `letrec`；表单元数据经 `withMeta`；`^:once` 接受、无语义 | 是 |
| `letfn*` | `letrec`（绑定名作为 fn 名） | 是 |
| `quote` | 常量（`syntax->clj`，未改动部分为原对象） | 是 |
| `var` | Var 常量（`LookupVar(sym, false)` 的规则与报错） | 是 |
| `throw` | `$throw` | 是 |
| `try` | `$try/catch`（每个 catch 一个 CLR 类型名 + 单参过程，局部带类型提示）外套 `$try/finally`；体与 catch 内禁止 recur | 是 |
| `monitor-enter` / `-exit` | `Monitor.Enter` / `Exit` | 是 |
| `.` | `Reflector.Call` / `GetField`（`-name`）；静态类型来自 `MaybeType`，实例类型来自 `^Tag` 或局部的提示；`(type-args …)`；实例为 nil 抛 NullReferenceException；返回值按 Reflector 的 Clojure 转换（int → long） | 是 |
| `new` | `Reflector.New` | 是 |
| `set!` | 局部：只允许可变字段（deftype），否则 "Cannot assign to non-mutable"；Var：`Var.set`；`Type/Field` 与 `(.-f x)` / `(. x f)`：`Reflector.SetField`；值为新值 | 是 |
| `case*` | `ClojureOps.CaseIndex` 按 ClojureCLR `CaseExpr` 的规则（`:int` 非整数走 default、`Util.hash` + shift / mask、`equiv` / 同一性、skip-check）算出分支下标，再 `cond` 分派；分支在尾位置 | 是（cleat-051 可换成 switch） |
| `import*` | 运行时 `importClass` 到 `*ns*`；类名在展开期检查 | 是 |
| `deftype*` / `reify*` | 接口与方法签名按 C# 的规则在展开期用反射选定（提示、显式 `Iface.name`、Object 的虚方法、全部报错）；展开为 `(ClojureOps.Unsupported "deftype*" '形状 (lambda (this 字段… 参数…) 体) …)`——形状是 C# Lcore `deftype` 节点去掉方法体的 Clojure 数据，方法体照常展开（字段可变性、recur 都检查），运行时报 "needs the cleat-051 compiler" | 否，需 cleat-051 |
| `catch` / `finally` / `&` 单独出现 | C# 的报错 | — |

调用：一律 `ClojureOps.Invoke`（`IFn.invoke`，超过 8 个实参经 `applyTo`）；以 Var 开头的调用先看 `:inline` / `:inline-arities`（私有检查同 C#）、`(instance? 字面类型 x)` → `Reflector.Is`；`(nil …)` 报 "Can't call nil, form: …"；关键字、map、向量、集合、Var 作函数都走 IFn。字面量：关键字、nil、正则、字符、BigInt、Ratio 等为常量；集合元素全为常量（含 `(quote x)` 与嵌套常量集合，按**值**重建，修正了 C# 展开器把 `[1 'a]` 当成 `[1 (quote a)]` 的问题）时整个是常量，否则 `RT.vector` / `RT.map` / `RT.set` 构造（map / set 运行时检查重复键）；用户元数据（去掉 reader 的源位置）常量时直接附上，否则 `withMeta`。宿主语法糖 `.method`、`Type.`、`Type/member`、`Type/.method`、`Type/new` 是 `macroexpand-1` 可见的改写（与 Clojure 相同）；`Type/Field` 作为值是静态成员读取；`long/1` 数组类型。

### 函数值（IFn 适配）

`Cleat.Scheme.Clojure.ClojureFn : clojure.lang.AFunction`（不是 `Procedure`）：`fn?`、`IObj`、`IComparer`、`MethodImplCache` 都来自 AFunction；`invoke(0..20, 20+params)` 按位图分派到固定子句过程或变长过程，`applyTo` 对变长函数把实参序列的尾部原样传入（`(apply f 1 (repeat 1))` 成立），否则 `ApplyToHelper`；参数个数不符抛 `ArityException(n, "ns$name")`（消息 "passed to: ns/name"，类名规则同 Clojure：外层 fn 名 + `$` + 自名 / def 名 / `fn__id`）。Clojure 代码只经 IFn 调用它，Scheme 代码看不到它；Scheme 过程 → IFn 的方向目前不需要（Clojure 代码看不到 Scheme 过程）。cleat-051 以真实函数类与 arity 专用调用取代它，cleat-055 审计值边界。

### 值边界：为什么运行时操作都是 C# 过程

实测 Scheme 的 `clr-call` / `clr-static-call` 对返回值（包括声明为 object 的）做 `Interop.ConvertFrom`：long → fixnum（Int32）、枚举 → 符号、字符串规范化；实参方向也会把 long 规范成 fixnum。因此 Clojure 的值一律不经 Scheme 互操作：展开结果以常量 `((quote 过程) 实参 …)` 调用 `ClojureOps` 的过程（`Procedure` 子类，调用与返回不转换）；`(clojure syntax-kinds)` 的 children（否则向量里的 `1` 变成 Int32）、defmacro 垫片的 `applyTo`（否则宏返回的 `5` 变成 Int32）、cleat-049 的 Var 读（原来是 `clr-call getRawRoot`，同样会把 long 变成 Int32——cleat-049 的已知缺陷，一并修正）也都改走这些过程。测试断言 `1`、`(first [1 f])`、`(.Length "abc")`、`(:line (meta (def x 1)))` 的类型都是 `System.Int64`，`Int64/MaxValue` 原样。

### 真值

Scheme 的 `if` 只把布尔 false 当假（`Builtins.IsTrue` 认任何装箱的 false，与 Clojure 的 `false` 一致），`nil` = CLR `null` = Scheme `'()` 在 Scheme 里为真，所以 Clojure 的 `if` 展开为 `(if (let ((t 测试)) (if (null? t) #f t)) …)`；`()`（空列表对象）、`0` 为真。

### 上下文与卫生

- 尾位置、recur 目标、词法局部、外层 fn 名、def 给 fn 的名字放在参数 `current-ctx`：过程式形式在调用 `expand` 的动态范围内参数化它；转换器式形式把子形式包进 `%ctx`（按 gensym 键登记的隐藏特殊形式）。recur 目标的标识符以语法形式放在 `%ctx` 里，才能带上外层命名 let 的替换与标记。调用与集合的子形式逐个在非尾上下文里 `expand`，核心形式手工拼装（`(rator rand …)`、`(quote d)`）。
- 引入绑定的特殊形式（`let*` `loop*` `fn*` `letfn*` `try` `case*` `set!` 局部、`deftype*` 方法）是**转换器**（经 `do-macro-call`）：卫生标记保证模板里的 `let` / `case-lambda` / 临时变量不被用户的同名局部捕获、也不捕获用户的名字；`recur` / `quote` / `var` / `import*` / `catch` 等不引入绑定的是**过程式**（`clj-form`，返回核心形式）。两种都 `macroexpand-1? = #f`：`macroexpand-1` 对 21 个特殊形式都返回原对象（测试逐个断言）。
- 头部的特殊形式不被词法绑定遮蔽：`clj->scheme` 把头部是特殊形式名的列表的头部换成专用 gensym（`(let* [if false] (if if 2 3))` 得 3）；不在头部的同名符号照常解析（`(let* [do 5] do)` 得 5，`(let* [k def] k)` 报 unresolved）。

### defmacro 垫片

`&form` 为原 Clojure 形式（`syntax->clj`，未改动部分 `eq?`）；`&env` 为词法局部的 `符号 → Cleat.Core.Expand.LocalBinding` map（顶层为 nil，与 Clojure 的 LOCAL_ENV 相同；含 let / loop / fn 参数 / catch / deftype 字段）；宏输出经 `clj-datum->syntax*` 挂使用处上下文——与 Clojure 完全相同的不卫生语义（测试：宏引入的 `x` 捕获用户的 `x`、用字符串拼出的符号可被 def / 调用、输出的元数据保留）。宏函数的 `ArityException` 名字等于 `ns$宏名` 时实参数减 2 后再包成 `Syntax error`；宏体异常按 C# 的 phase 规则包装。私有宏经别名 / 全名引用由解析规则拒绝（"var: other/pm is not public"，即 `resolve` 的文本）。`:inline` / `:inline-arities` 在以 Var 开头的调用处应用（结果带原形式的源位置与 `:tag`）。入口 `clojure-eval` / `clojure-expand` / `clojure-macroexpand-1` / `clojure-macroexpand`（返回裸 Clojure 数据）在 `(clojure syntax)`。

### boot 改动（一处，通用；按 05 §5 一步重生成种子）

- `core.sls`：`chi-expr` 的 `call` 分支经新 `chi-call`、无效表达式经新 `chi-datum`——查绑定扩展表的保留种类 `$application` / `$datum`，登记了就先交给它，返回 `#f` 照常（+18 / −2 行）。Clojure 侧只处理 Clojure 来源的应用（注解的 stripped 是 Clojure seq）与 Clojure 展开期间的数据，Scheme 代码不受影响。再导出展开器已有的 `syntax-ext-kind` / `syntax-ext->list`（拿到带 wrap 的向量 / map 子项），图例 `(ironscheme syntax-ext)` 在两个构建脚本里各 +2 行。
- 种子：新镜像 `seed` 与旧种子 `seed --stage0` 逐字节相同（+12,834 / −12,819 行）；新种子构建出的镜像与旧种子构建的逐字节相同（dll `4b858540e7d5b3d8…`，lcil `2f9f01f59bc95977…`）。

### 文件

- `src/scheme/clojure/syntax.sls`（`(clojure syntax)`，1020 行）；`namespaces.sls` 改动：头部特殊形式、`clj-form` 种类与 `register-hidden-clj-form!`、解析后备 / Var 调用 / `&env` 提供者三个挂钩、Var 读改走 `ClojureOps`（含 `:const`）、完整的 defmacro 垫片；`def` 移到 `(clojure syntax)`；入口改名 `eval-in-ns` 等（R6RS 库体只在其变量被引用时执行，所以公开入口放在 `(clojure syntax)`，用到入口就会登记特殊形式）。
- C#：`src/Cleat.Scheme/Clojure/{ClojureFn,ClojureOps,HostSyntax}.cs`（685 行）。

### 测试

- 新增 `tests/scheme/clojure/syntax.sps`，**238 项**（约 10 秒）：`ExpanderTests` 的 ConstantsAndCollections、SymbolsAndVars、ControlFormsAndLocals、Functions、DefAndSetBang、MacrosInlineAndEnv、Invocations、HostInterop、TryCatchFinally、CaseStar、DeftypeAndReify 逐条翻译（能运行的比较求值结果，`deftype*` / `reify*` 比较展开结果里的形状数据与运行时报错，错误一律比较 Clojure 文本含 `t.clj:行:列`），加上 21 个特殊形式与 2 个调用的 `macroexpand-1` 停止点、5 种宿主糖的 `macroexpand-1`、long 不变形、真值、头部特殊形式不被遮蔽、recur 的各种非尾位置、`&env`、不卫生捕获、字符串拼符号、宏输出元数据（macros.clj 的 `->metadata-test` 形态）、macros.clj 的宏 `c`、special.clj 的 `(quote 1 2 3)` 与 `&` 绑定。clojure.core 未加载，`list` `=` `inc` `first` 等在测试里用 `def` + `fn*` + 互操作定义（这本身也验证了 fn* 与互操作）。
- `namespaces.sps`（cleat-049，57 项）改用真正的 `let*` / `set!` / `def` 与新入口，全部通过；查找度量顺带改善（完整展开一个 Var 引用约 6 ~ 8 µs，原来 38 µs，因为不再展开 `clr-call` 模板）。
- `dotnet test Cleat.slnx`：Cleat.Core.Tests 129 通过；Cleat.Scheme.Tests **220 通过**（219 + `clojure/syntax.sps`，含自举不动点与快照），该程序集 4 分 16 秒。

### 未做 / 留给后续

- `deftype*` / `reify*` 生成真实类型、`case*` 的 switch、关键字调用的内联缓存、arity 专用的 IFn 调用与 Var 的直接链接：cleat-051（今天分别是占位、下标 + cond、IFn.invoke、`ClojureOps.Invoke`）。常量（Var、过程、集合）以 `(quote 对象)` 进 Lcore，只适用于进程内求值；AOT 需要 cleat-051 / 052。
- 以 **Var 对象**（而不是符号）开头的宏调用形式 `(#<Var> …)`：C# 的 `IsMacro(op is Var)` 分支与它的私有检查文本 "Var: … is not public" 没有实现（这种形式只能由宏构造；按普通调用处理）。私有宏经符号引用时报的是解析文本 "var: q/m is not public"，与 C# 的 "Var: #'a/m is not public" 不同。
- 顶层 `(do …)` 拆分（`IsTopLevelDo`）与 reader 的语法引用决议（`ReaderResolver`）：cleat-052 的加载器 / reader 层，未翻译。
- `*warn-on-reflection*`、`AllowUnresolvedVars`、`by-ref`、未限定的带 `` ` `` 等触发字符的类型名后备解析：未做（`by-ref` 报 C# 的错误）。
- 函数打印：`ClojureFn.ToString()` 是类名（`ns$f`），`#object[…]` 形式要等 core_print。
- 值边界的系统审计（Scheme 过程 / Cons / Unspecified 不流入 Clojure、诊断文本黄金文件）：cleat-055。

## 进度
- 2026-10-10 设计定稿（先回答"特殊形式展开成什么"）：
  - **目标：Scheme 核心形式 + 运行时过程，今天就能运行**（方案 a）。只有 `deftype*` / `reify*`（要生成真实 CLR 类型）展开为占位调用：体照常展开（报错照常），运行时抛 "needs cleat-051"，形状以带引号的数据留在展开结果里供测试。`case*` 不必等 051：键的计算与比较（`Util.IsNonCharNumeric` / `ConvertToInt` / `Util.hash` + shift / mask，再 `equiv` / 同一性）由一个 C# 过程算出分支下标，再 `cond` 分派，语义与 ClojureCLR 的 `CaseExpr` 相同。
  - **值边界的实测发现**：Scheme 的 `clr-call` / `clr-static-call` 对返回值（含声明为 object 的）做 `Interop.ConvertFrom`：`long` → fixnum（Int32）、枚举 → 符号、字符串规范化。Clojure 的值因此**不能**经 Scheme 互操作流动（13 §7 的值边界）。决定：Clojure 代码的运行时操作全部是 C# 实现的 Scheme 过程（`Cleat.Scheme.Clojure.ClojureOps`，`Procedure` 子类，调用与返回都不转换），展开结果里以常量 `((quote 过程) 实参 …)` 调用；`(clojure syntax-kinds)` 的 `children` 与 defmacro 垫片的 `applyTo` 也改走这些过程（否则宏返回的 `5` 与向量里的 `1` 会变成 Int32）。
  - **函数值**：`fn*` → `Cleat.Scheme.Clojure.ClojureFn : clojure.lang.AFunction`（于是 `fn?`、`IObj`、`IComparer`、`MethodImplCache` 都有），包着 Scheme 过程：固定参数子句合成一个 `case-lambda`，变长子句单独一个"必需参数 + 1"的过程，剩余参数以 `ISeq`（`ArraySeq` / `applyTo` 的惰性尾部）传入，与 `RestFn` 相同；参数个数不符抛 `ArityException`（名字 `ns$name`，与 Clojure 的类名规则相同）。**所有 Clojure 调用都走 `IFn.invoke`**（`ClojureOps.Invoke`），不是 Scheme 的过程调用；不改 `Calls.Proc`。Scheme 过程 → IFn 的方向目前不需要（Clojure 代码看不到 Scheme 过程），留 cleat-055 审计。
  - **真值**：Scheme 的 `if` 只把布尔 false 当假（`Builtins.IsTrue`：任何装箱的 false 都是假，与 Clojure 的 `false` 一致）；`nil` 是 CLR `null` = Scheme `'()`，在 Scheme 里为真。Clojure 的 `if` 展开为 `(if (let ((t 测试)) (if (null? t) #f t)) then else)`。
  - **boot 必须改一处（chi-expr，两个通用挂钩）**：Clojure 的 (1) 非自求值字面量（关键字、`nil`、向量 / map / set、正则、BigInt…）在 psyntax 里是 `other`，(2) 头部不是宏 / 特殊形式的应用（局部变量、关键字、`((fn* …))`）走 `chi-application` 生成 Scheme 调用——两者都没有扩展点。按需库里无法绕开，所以给 `chi-expr` 加两个查绑定扩展表的分支（保留种类名 `$application` / `$datum`，不加导出名、不改图例）：登记了就先交给它，返回 `#f` 照常。只有 Clojure 来源的形式（注解的 stripped 是 Clojure 的 `ISeq`）与 `in-clojure` 期间的数据才被处理，Scheme 代码不受影响。按 05 §5 重生成种子。
  - **上下文（尾位置 / recur 目标 / 局部 / &env / 类型提示）**：特殊形式实现为"过程式"绑定扩展（`(e expand) → 核心形式`），在调用 `expand` 的动态范围内参数化当前上下文；绑定形式（`let*` / `loop*` / `fn*` / `letfn*` / `catch`）用 Scheme 模板加 `%ctx` 包装子形式（`%ctx` 是按 gensym 名登记的特殊形式，只能由展开器产生），recur 目标的标识符以语法形式放在 `%ctx` 里（才能带上外层 `let` 的替换）。调用与集合的子形式在非尾位置逐个 `expand`，核心形式手工拼装。
  - **头部位置的特殊形式不被词法绑定遮蔽**（Clojure 规则）：`clj->scheme` 把头部是特殊形式名的列表的头部换成该特殊形式专用的 gensym 标识符（注解的 stripped 仍是原符号，`syntax->datum` 不变）。
- 2026-10-10 boot 改动与种子：`core.sls` 的 `chi-expr` 加 `chi-call` / `chi-datum`（保留种类 `$application` / `$datum`），并导出 `syntax-ext-kind` / `syntax-ext->list`（宏与特殊形式要拿到带 wrap 的向量 / map 子项；图例 `(ironscheme syntax-ext)` 两个构建脚本各 +2 行）。C#：`src/Cleat.Scheme/Clojure/`（`ClojureFn`、`ClojureOps`、`HostSyntax`）。`(clojure syntax-kinds)` 的 children 改走 `ClojureOps`；`(clojure namespaces)`：头部特殊形式 gensym、过程式特殊形式 `clj-form`、解析后备钩子、Var 读改为 ClojureOps、defmacro 垫片完整版（&env 提供者、ArityException 修正）、`def` 移到 `(clojure syntax)`；入口改名 `eval-in-ns` 等，公开入口 `clojure-eval` 等在 `(clojure syntax)`（R6RS 库体只在其变量被引用时执行）。`src/scheme/clojure/syntax.sls` 初版可运行：字面量、if / do / let* / loop* / recur / fn*（ArityException）/ try / . / new / 糖 / 关键字调用冒烟通过，long 保持 Int64。下一步：测试文件。
- 2026-10-10 完成：`tests/scheme/clojure/syntax.sps` 238 项、`namespaces.sps` 57 项通过；种子按 05 §5 重生成（两条路径逐字节相同，新种子构建的镜像与旧种子的逐字节相同）；全量测试通过（Core 129、Scheme 220）；结论已写，TASK.md 改为完成。
