# cleat-055 "看不到 Scheme"的三道边界

- 里程碑：M4′　依赖：cleat-052　状态：完成
- 设计依据：[13 §7](../designs/13-syntax-objects-and-scheme-core.md)、[14 §1 问题 3](../designs/14-three-layer-architecture.md)、[09 §2](../designs/09-testing.md)

## 目标
用测试把三道边界守住，使 Clojure 用户在语法、值、诊断三个层面都见不到 Scheme。

## 工作内容
1. 语法边界：对每个特殊形式断言 `macroexpand-1` 返回原形式；`macroexpand` 的输出只含 Clojure 数据（无 `case-lambda` / `letrec` / Scheme 符号）；`&form` 与 `defsyntax` 输出经 `syntax->datum` 只含 Clojure 集合。
2. 值边界：审计编译器对 Clojure 代码生成的全部原语调用只落在 `Cleat.Core`（`clojure.lang`）与 `cleat.kernel`；Scheme 的 fixnum / `Cons` / `Unspecified` / 可变字符串 / `SchemeChar` 不出现在 Clojure 值中（语料的数值、字符串、集合 suite 作为回归）；互操作返回值经 cleat 的 `Reflector` 转换而不是 Scheme 的 `Interop`。
3. 诊断边界：reader / 展开 / 宏 / 运行时四类错误的黄金文件（`tests/errors/`，jolt 形态）；错误文本 `Syntax error (file:line:col)`；函数类名 `ns$name`；`(class f)` 为 `ClojureFn`、`(fn? f)` 为真；CLR 栈过滤 `Cleat.Scheme.*` 帧。
4. 函数值跨层：Scheme 过程流入 Clojure 的表示只有一种（`Procedure` 实现 `IFn`，或只经 `cleat.kernel` 包装）；两方向调用测试。

## 验收标准
- 四组测试进入 `dotnet test`；审计清单记入本文件。


## 结论（2026-10-10）

完成。三道边界都由 `dotnet test` 守住：语法边界与值边界在 `tests/Cleat.Host.Tests/SchemeInvisibilityTests.cs`（测试进程里经 `Program.Start` 启动宿主，eval / macroexpand-1 钩子驱动 Clojure 代码，直接检查返回的 CLR 对象；值的检查对快速路径与编译路径各做一遍），诊断边界在 `ErrorGoldenTests.cs` + `tests/errors/` 的 30 个黄金文件（子进程 `cleat input.clj`，标准错误逐字比较）。审计找出 6 处 Scheme 泄漏，全部修正并各有回归测试。clojure.core 未加载（cleat-053），测试只用特殊形式、`def`、`fn*`、互操作与 `cleat.kernel/*`。

### 一、语法边界

- 27 个特殊形式写法（21 个特殊形式 + 多子句 fn*、无初值 def、`. Type (m …)`、`catch` / `finally` / `&` 单独出现）：`macroexpand-1` 与 `macroexpand` 都返回**同一个对象**。
- 用户宏（`def` + `fn*` + `setMacro`）：展开结果、`&form`、`&env` 经 C# 的 `AssertClojureData` 递归检查——只允许 clojure.lang 的集合（含元数据）、long / double / char / string / bool / nil、关键字、符号、Var、BigInt / BigDecimal / Ratio、正则、类型、`LocalBinding`、Clojure 函数；Cleat.Scheme 程序集的任何对象（Symbol、Cons、Unspecified、MultipleValues、SchemeString、Record / 语法对象 / 注解、Procedure、Fraction…）、Int32 等、StringBuilder、BigInteger 一律失败（有反例测试）。`&form` 是原形式（`eq?`），顶层 `&env` 为 nil，`let*` / `fn*`（含自名、变长参数）/ `loop*` 的局部都在 `&env` 里，键为 Clojure 符号、值为 `LocalBinding`。宿主糖（`.m`、`Type.`、`Type/m`、`Type/.m`）的改写结果同样只含 Clojure 数据。
- 例外（不是泄漏）：reader 的位置元数据 `:line` / `:column`（及 ClojureCLR 的 `:source-span`）是 Int32——Clojure 本来如此（JVM 为 `java.lang.Integer`），检查器只对元数据里这些键放行。

### 二、值边界：审计表

| 路径 | 返回 | 结论 | 修正 / 测试 |
|---|---|---|---|
| `$clj-var-ref`（getRawRoot / 动态 Var 的 get） | Var 的值 | 无转换 | — |
| `$clj-var-set!`（`Var.set`） | 新值 | 正确：`(set! *dyn* 2)` 得 Int64 2 | 两条路径测试 |
| `$clj-def` | Var | 正确：`(def x 1)` 得 `#'boundary.test/x`，fn 里的 def 同样 | 测试 |
| `$clj-invoke`（`castclass IFn` + `invoke`） | 被调函数的值 | 正确；Scheme 过程不是 IFn → InvalidCastException | 测试 |
| `$clj-keyword-invoke`（`ILookup.valAt` / `RT.get`） | 值 | 正确 | 测试 |
| `$clj-call` → `ClojureOps`：`VectorOf` `MapOf` `SetOf` `WithMetaOf` | clojure.lang 集合 | 正确 | 测试 |
| `$clj-call` → `CallStatic` / `CallInstance` / `GetStaticField` / `GetInstanceField` / `NewOf`（Cleat.Core `Reflector`） | Reflector 的 Clojure 转换：int / byte → long、float → double、char 保持、void → nil | 正确：`(. "abc" Length)` 为 Int64 3，`(. System.Console WriteLine "x")` 为 nil | 测试（含 `Int32/MaxValue`、`ToByte`、`ToSingle`、`get_Chars`） |
| `SetFieldOf` / 局部 `set!`（`(let ((tmp v)) (set! x tmp) tmp)`）/ deftype 可变字段 | 新值 | 正确 | 测试 |
| `MonitorEnterOf` / `MonitorExitOf` / `ImportOf` / `CheckBootImage` | nil | 正确 | 测试 |
| `DefineType` / `RealType` / `FindNamespaceOf` / `IsInstanceOf` | Type / Namespace / bool | 正确 | 测试（deftype、reify） |
| `$clj-fn` | `AFunction` / `RestFn` 子类，类名 `ns$name`（重名 `__N`），无 Scheme gensym | 正确 | `FunctionClassesHaveClojureNames`（含 ArityException 文本 `passed to: boundary.test/bar`） |
| `$clj-deftype` 方法：实参 `BoxArg`、返回 `ConvertReturn` | Clojure 值 / 声明类型 | 正确：reify 的 `count` 经互操作为 Int64 | 测试 |
| `$clj-case` | 分支的值 | 正确 | 测试 |
| `(clojure syntax)` 生成的 Scheme 核心形式：`if`（总带 else = nil）、非空 `begin`、`let` / `let*` / 命名 let、`$throw`、`$try/catch` / `$try/finally` | 体的值；空 `do` / `let*` / `try`、无 else 的 `if` 为 nil；`(throw 1)` 是 CLR 的 InvalidCastException | 正确 | 测试 |
| fn* / loop* / letfn* / deftype 方法的**重复绑定名** | psyntax 报 `duplicate binding`（0:0） | **泄漏**（Clojure 允许，取最后一个） | `dedupe-ids`：前面的同名者换成临时名；deftype 重复字段报 `Duplicate field name "a" in T`；测试 |
| `letfn*` → `letrec` | 初值不是函数时读未初始化变量抛 **SchemeException** `b: attempt to read uninitialized variable` | **泄漏** | 初值全是 `(fn* …)` 仍用 letrec，否则先绑 nil 再依次赋值；`(letfn* [a b b 1] a)` 得 nil；测试 |
| 编译单元常量表（codegen `constant-index!`，Equals 去重） | `()` 与 `[]`、`'(1 2)` 与 `[1 2]`、`'x` 与 `'^:m x` 合并成一个常量 | **泄漏**（Scheme 的常量相等语义作用到 Clojure 值） | 只有 Scheme 数据按 Equals 去重，其余对象 `eq?` 才共用；镜像重建（Scheme 代码的常量不变，快照与不动点通过）；测试 |
| 驱动编译路径（镜像的 `compile-core` 的 thunk，`clr-static-call ModuleLoader Run`） | 顶层值经 Scheme 互操作：**long → Int32** | **泄漏**（052 的对照测试在 Clojure 里取 `(class x)`，测不出） | Lcore 包成 `($clj-call 'HoldResult #f 核心)`，值经线程槽、`ClojureOps.TakeResult` 原样取回；AOT 单元收集的仍是原 Lcore；两条路径测试 |
| 驱动快速路径（`Driver.TryEval`） | 与编译路径相同 | 正确（无 else 的 `if`、空 `begin` 返回 Unspecified 的分支不可达：`(clojure syntax)` 不产生这两种 Lcore） | 全部值测试跑两条路径 |
| 钩子 eval / load-source / compile / macroexpand-1 | Scheme `Procedure` 由 C# 直接 `Invoke`，不转换 | 正确 | — |
| defmacro 垫片（`clr-static-call RT cons` 拼实参） | 集合不经数值规范化；宏结果经 `ApplyTo` 过程 | 正确 | 宏测试 |
| 字符串 / 字符 / 布尔 / `'()` / 多值 | `System.String`（字面量是常量串）、`System.Char`、`System.Boolean`、`PersistentList.EMPTY`（不是 nil）；Clojure 代码产生不了 `values` | 正确 | 测试 |

**函数值的跨层（决定）**：只有一种表示——Clojure 函数是 clojure.lang 的函数类；Scheme 过程**不**包装成 IFn。Clojure 代码只能经显式的宿主互操作拿到 Scheme 过程（读 `Cleat.Scheme.*` 的静态字段），它对 Clojure 是不透明的宿主对象：不是 IFn，调用它与调用任何非函数一样抛 InvalidCastException（两条路径测试）。

### 三、诊断边界

- 文本按 JVM Clojure 1.12（`clojure.main/ex-str`，本机 `~/.m2` 的 1.12.4 逐条对照）：
  - 读错误 `Syntax error reading source at (文件:行:列).` + 原因；读的时候把源名记进 `Exception.Data`（原来文件里的读错误报成 REPL）。
  - 编译错误 `Syntax error[ (类型)] compiling [特殊形式 ]at (文件:行:列).`（特殊形式名只对特殊形式给出：`compiling recur at`、`compiling at`；无原因异常时类型按原因文本对应到 CLR 的同类异常：ArgumentException / NotSupportedException / InvalidOperationException，对应 JVM 的 IllegalArgument / UnsupportedOperation / IllegalState，其余省略）。
  - 宏错误按 JVM 1.12 的 macroexpand1 分阶段：ArgumentException / InvalidOperationException / IExceptionInfo / 恰为 System.Exception → `Syntax error macroexpanding m at`；其余 → `Unexpected error (类型) macroexpanding m at`；宏函数的参数个数错（去掉 &form / &env 后）→ `Syntax error (ArityException) compiling at`。
  - 执行错误 `Execution error[ (类型)] at (文件:行).`（类型为 System.Exception 时省略，同 JVM 省略 Exception / RuntimeException）。
  - 路径：当前目录之下的相对于当前目录（ex-triage 的规则），无源为 REPL。`ExpanderException` 加 `Reason`、`Symbol`（`:clojure.error/symbol`），`Message` 不变（Scheme 测试仍比较一行的旧形式）。
- psyntax 的措辞不出现：`(clojure namespaces)` 的错误挂钩把 `invalid syntax` 等换成 `Malformed deftype*` 一类；非符号的 `unbound identifier` 给 Clojure 的 "Unable to resolve symbol"。若干 ClojureCLR 措辞改成 JVM 的（`expected matched symbol expression pairs`、`Can't have 2 overloads with same arity`、`Can't create defs outside of current ns`、`Wrong number of args (n) passed to quote`、`Malformed member expression…`、`(new Classname args...)`、`got: x`），`(throw)` 原来误报 "Too many arguments"。
- 栈：缺省不打印（与 Clojure 的 REPL / clojure.main 相同）；`CLEAT_TRACE_ERRORS=clojure` 打印过滤过的栈（`Driver.ClojureStackTrace`：去掉 Cleat.Scheme 程序集、Scheme 生成的 `_Module$` / `#Module$` 代码、反射与动态方法的帧，留下 `user$f.invoke` 等 Clojure 帧）；`CLEAT_TRACE_ERRORS=1`（其他非空值）打印完整的 CLR 异常。测试三种都检查。
- 函数类名在 `(. f GetType)` 与 ArityException 里都是 `ns$name` / `ns/name`，测试断言没有 `g$`、`#`、`_Module`。

黄金文件（`tests/errors/类别/名字/{input.clj,expected.txt}`，expected 末行 `exit: 1`）：
- read（4）：unbalanced-paren、unmatched-delimiter、invalid-number、eof-in-string
- expand（12）：unresolved-symbol、unresolved-call、unknown-namespace、private-var、recur-not-in-tail-position、recur-across-try、recur-arg-count、if-too-few-args、let-odd-bindings、new-unknown-class、assign-to-local、malformed-deftype
- macro（5）：macro-throws、macro-throws-ex-info、macro-throws-argument-exception、macro-runtime-error、macro-arity
- runtime（9）：wrong-arg-count、wrong-arg-count-named-fn、interop-exception、throw-ex-info（`clojure.lang.ExceptionInfo` 经互操作）、throw-exception、divide-by-zero-in-fn、call-non-function、throw-non-exception、nil-instance-call

### 测试

- `Cleat.Host.Tests` 11 → **188** 项：`SchemeInvisibilityTests`（数据检查器的反例、27 个停止点 × 2、用户宏与 &form / &env、宿主糖、51 个值 × 两条路径、set! / deftype / reify、函数类名、Scheme 过程不是 IFn、Clojure 看到的异常是宿主异常）、`ErrorGoldenTests`（30 个黄金文件 + 栈过滤）；`HostProcessTests` 的错误文本改为新格式。测试项目改为引用宿主程序集（进程内启动）。
- `syntax.sps`、`namespaces.sps`、`driver.sps` 的错误文本随措辞 / 格式更新。
- 全量 `dotnet test Cleat.slnx`：Cleat.Core.Tests 129、Cleat.Scheme.Tests 225（含自举不动点、ILVerify、快照——常量去重的改动不影响 Scheme 代码）、Cleat.Host.Tests 188，全部通过。

### 留下的暴露 / 差异

- 执行错误没有出错帧的符号与行（JVM：`at user/f (file.clj:行)`，行为出错帧的行）：cleat 报最内层顶层形式的 文件:行，因为函数类没有行号信息——随调试信息 / 行号表（cleat-042）。
- 原因类型是 CLR 的异常名（宿主部分按目标各一份，designs/14 §2）；少数文本仍与 JVM 不同：`(fn* 5)` 报 "Bad fn clause: 5"（JVM 是 ClassCastException 的文本）、带点的未知命名空间报 "No such namespace"（JVM 是 ClassNotFoundException）、`(Type/Nope)` 在运行时报 MissingMethodException（JVM 编译期）、`set!` 局部的错误符号是 `set!`（JVM 报外层 `fn*`）、`throw` 文本说 "Exception instance"（JVM "Throwable"）。
- REPL 的行号跨读累计（JVM 每次读从 1 起），`*e` / `pst`、`#'user/x` 的打印：随 clojure.main / core_print（cleat-053）。
- `FormatError` 仍会把逃出的 `SchemeException` 的条件字段打印出来：修正 letfn* 之后没有已知的 Clojure 可达路径（编译器自身的错误除外），发现即按本任务的办法修。
- 显式互操作可以拿到 `Cleat.Scheme.*` 的对象（如 Driver 的过程）：不透明的宿主对象，按设计不隐藏。

## 进度
- 2026-10-10 摸底（Release 宿主 + JVM Clojure 1.12.4 对照，`~/.m2` 的 clojure / spec.alpha / core.specs.alpha 组成 classpath）：值边界在两条路径（快速路径与 `CLEAT_NO_SIMPLE_EVAL=1` 的编译路径）上逐项检查 60 个形式的运行时类型，全部是 Clojure 的表示（Int64、Double、Char、String、Ratio / BigInt / BigDecimal、PersistentList+EmptyList、nil、Var、`ns$name` 函数类）。发现的泄漏：(1) `(fn* [a a] a)`、`(loop* [a 1 a 2] a)`、`letfn*` / deftype 方法参数里的重复名字报 psyntax 的 `duplicate binding`（位置 0:0），而 Clojure 允许、取最后一个；(2) `(letfn* [a b b 1] a)` 在运行时抛 SchemeException `b: attempt to read uninitialized variable`；(3) `(deftype*)` / `(reify*)` 报 psyntax 的 `invalid syntax`；deftype 重复字段报 `duplicate binding`；(4) 诊断文本是 `Syntax error (文件:行:列) 原因` 一行，不是 Clojure 的 ex-str 两行（`Syntax error compiling [符号 ]at (…).`、`Syntax error macroexpanding m at`、`Unexpected error (类型) macroexpanding`、执行错误 `at (文件:行)`）；文件里的读错误报成 `REPL`；`(throw)` 报 "Too many arguments"；若干文本是 ClojureCLR 的措辞（与 JVM 不同）。
- 2026-10-10 修正：`ExpanderException` 加 `Reason` / `Symbol`（`:clojure.error/symbol`）；`(clojure namespaces)` 的错误挂钩给出特殊形式名、把 psyntax 的措辞换成 Clojure 的说法，宏异常按 JVM 1.12 的 macroexpand1 分阶段（宏函数 ArityException → compile-syntax-check、恰为 System.Exception → macro-syntax-check）；`(clojure syntax)`：重复绑定名去重（`dedupe-ids`）、letfn* 初值不全是 `fn*` 时先绑 nil 再赋值、deftype 重复字段报 `Duplicate field name`、文本按 JVM 对齐；`Driver.FormatError` 改为 ex-str 两行格式（路径相对当前目录、无源为 REPL、编译错误的原因类型按文本对应），读错误在读时记下源名，`CLEAT_TRACE_ERRORS=clojure` 打印过滤过的栈（`Driver.ClojureStackTrace`）。手工对照 16 个错误输入与 JVM 输出一致（除宿主类型名）。
- 2026-10-10 测试中发现的另外两处值边界泄漏：(5) 编译路径的**顶层值**是 Int32——镜像的 `compile-core` 返回的 thunk 是 `(lambda () (clr-static-call ModuleLoader Run asm))`，Scheme 互操作把返回值规范化（long → fixnum）；fn 内部、快速路径不受影响，所以 052 的对照测试（在 Clojure 里取 `(class x)`）测不出。修正：驱动把 Lcore 包成 `($clj-call 'HoldResult #f 核心)`，值经线程槽、`ClojureOps.TakeResult` 原样取回。(6) 编译单元的常量表（codegen `constant-index!`）按 .NET `Equals` 去重：Clojure 的 `()` 与 `[]`、`'(1 2)` 与 `[1 2]`、`'x` 与 `'^:m x` 被合并成一个常量（`((fn* [b] [b '(1 2) [1 2]]) 0)` 得 `[0 (1 2) (1 2)]`）。修正：只有 Scheme 数据（数、字符、串、符号、布尔、序对、向量、bytevector）按 Equals 去重，其余对象只在 `eq?` 时共用（镜像重建，Scheme 代码的常量不变）。新增 `tests/Cleat.Host.Tests/SchemeInvisibilityTests.cs`（进程内启动宿主，`AssertClojureData` 递归检查）与 `ErrorGoldenTests.cs` + `tests/errors/` 30 个黄金文件；宿主测试 188 项通过。
- 2026-10-10 完成：全量测试通过（Core 129、Scheme 225、Host 188）；结论已写，TASK.md 改为完成。
