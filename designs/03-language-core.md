# 03 核心语法、展开器与 core 库

## 1. 范围：Clojure 核心语法

| 层 | 内容 | 来源 |
|---|---|---|
| reader | 数字（含 `N`、`M`、`r` 基数、`0x`、ratio）、字符串、字符、符号、关键字（含 `::`）、列表 / 向量 / map / set、`'` `` ` `` `~` `~@` `#'` `#(…)` `#_` `#?` `#?@` `#"…"` `#{…}` `#inst` `#uuid` `#=` `^meta` `@` `#:ns{…}` `#::{…}` | `LispReader`（02 §7） |
| 特殊形式 | `def`、`if`、`do`、`let*`、`loop*`、`recur`、`fn*`、`quote`、`var`、`throw`、`try`（`catch` / `finally`）、`monitor-enter`、`monitor-exit`、`.`、`new`、`set!`、`letfn*`、`case*`、`deftype*`、`reify*`、`import*` | 与 Clojure 相同的 21 个 |
| 不支持的 | `parse-eval*`（ClojureCLR 专用）；`proxy` 全量、`gen-class`、`await*` 由 cleat-043 支持（见 §4），`definterface` 以 `deftype*` 的接口生成实现（M3） | — |
| 核心库 | `clojure.core`、`core.protocols`、`core_deftype`、`core_proxy`（子集）、`core_print`、`gvec`、`instant`、`uuid`、`string`、`set`、`walk`、`edn`、`data`、`template`、`test`、`stacktrace`、`repl`、`main`、`pprint`、`zip`、`java.io` → `clojure.clr.io`、`clojure.clr.shell`、`reflect`（子集） | ClojureCLR `Clojure.Source/clojure/` |

## 2. 展开器（`Cleat.Core.Expander`，C#）

输入一个顶层形式（reader 输出的数据），输出 **Lcore**（§3）。它是 Clojure `Compiler.analyze` 去掉发射后的部分，预计 1500 ~ 2000 行 C#。职责：

1. **宏展开**：`macroexpand-1` 规则同 Clojure（符号头 → 查 Var `:macro` → 调 `(fn [&form &env & args])`；`:inline` 在展开器里按 `inline-arities` 应用；`(.method x)`、`(Type/member)`、`(Type. …)`、`Type/.method`、`Type/new` 的糖改写为 `.` / `new`；`^:once`）。`&env` 是词法环境 map（符号 → `LocalBinding` 信息），与 Clojure 相同。
2. **符号决议**：词法变量（`let*`/`fn*`/`loop*`/`letfn*`/`catch` 绑定、`deftype*` 字段与 `this`）→ `(local x)`；特殊形式；Var（`ns/name`、别名、`refer`）→ `(var-ref #'ns/name)`，未解析报 "Unable to resolve symbol"；宿主类型（`System.String`、`String`（`import`）、`|…|` 语法、数组 `String[]`）→ `(host-type "全名")`；`Type/Static`、`Type/.Instance` 等 → 相应 `host-*` 节点。
3. **特殊形式的语法检查与规范化**：`fn*` 名字、参数向量、`&` 剩余参数、最多 20 个固定参数、只许一个变长 arity、arity 不冲突；`loop*`/`recur` 参数个数与尾位置（`recur` 不能跨 `try`）；`case*` 的三种 switch 形态（`:int`、`:hash-equiv`、`:hash-identity`，由 `case` 宏预哈希后以数据进入 Lcore）；`deftype*` / `reify*` 的接口与方法签名**在展开器里用反射查出**（接口的方法、参数类型、返回类型），以数据写入 Lcore；`try` 的 `catch` 类型决议；`.`/`new` 的类型提示与成员名保持符号。
4. **类型提示**：`^String`、`^long`、`^"System.String[]"`、`^{:tag …}` 统一为 `(tag "全名")` 附在节点上；`*warn-on-reflection*` 的警告在这里给出（它能查到类型；编译器不查）。
5. **源位置**：每个节点带 `{:line :column :file}`，供编译器生成序列点与错误信息。
6. **不做的**：不做闭包分析、不做常量折叠、不发射——这些在编译器里。

展开器与运行时同进程、依赖当前 `*ns*` 与已加载的 Var，所以**不纯**；它的输出 Lcore 是纯数据。`eval` = 展开 → 编译 → 发射 → 执行，逐个顶层形式（`do` 顶层展开为逐个子形式，与 Clojure 相同）。

## 3. Lcore 文法（编译器的固定输入）

实现：`src/Cleat.Core/Expander/Lcore.cs`（cleat-013）。Lcore 是 Clojure 数据：节点为以符号开头的列表，类型名是字符串，Var 以限定符号 + 标志 map 表示。

```
e ::= (const d)                                   ; 字面量 / quote；d 为任意数据（含集合、正则、Type、记录）
    | (local x)                                   ; 词法变量；x 为唯一化符号 name__N（元数据 :tag）
    | (var-ref ns/name {:dynamic b :redef b})     ; 读 Var
    | (the-var ns/name)                           ; #'ns/name
    | (if e e e) | (do e ... e)
    | (let [x e ...] e) | (loop [x e ...] e) | (recur e ...)
    | (fn name|nil once? ((x ...) rest|nil e) ...)  ; 每子句：固定参数、剩余参数、体；name 是自引用的局部
    | (letfn [x (fn ...) ...] e)
    | (def ns/name {:dynamic b :init b} meta-e init-e|nil)
    | (set! target e)                             ; target: (local x)（deftype 可变字段）| (var-ref …)
    | (host-set-field "T"|nil "name" static? target|nil e)
    | (throw e)
    | (try e ((catch "T" x e) ...) finally-e|nil)
    | (monitor-enter e) | (monitor-exit e)
    | (case (local x) shift mask low high default-e ((hash test then-e) ...) switch-type test-type skip-check)
    | (host-call "T"|nil "member" static? target|nil (args ...))   ; 成员种类（字段 / 属性 / 方法）由发射器按 Clojure 规则决定；元数据 :type-args
    | (host-field "T"|nil "name" static? target|nil)             ; (.-name x)
    | (host-new "T" (args ...)) | (host-type "T") | (instance-of "T" e) | (import "T")
    | (deftype tagname "T" ((field "name" x "T" mutable? volatile?) ...) ("I" ...)
               ((method "I"|nil "name" this ((x "T") ...) "R" e) ...) opts)   ; reify 同节点，opts 含 :reify true
    | (call e (args ...))
    | (vector e ...) | (map k v ...) | (set e ...) | (with-meta e meta-e)
    | (keyword-invoke :k e)
    | (tag "T" e)                                 ; 带类型提示的表达式
```

- 符号已唯一化、Var 已决议、类型名已是字符串：编译器不再查任何表。节点带 `:line` / `:column` 元数据。
- `case` 的全部预处理在 `case` 宏 + 展开器完成；`deftype` 的接口方法签名是数据；`(tag …)` 是字符串。
- 文本形式：Lcore 可以写成 EDN（常量用 05 §3 的中立编码），用于快照测试与 Lcore 种子（08 §5）。

## 4. 与 Clojure 的明确偏离

| 项 | Clojure / ClojureCLR | cleat |
|---|---|---|
| 反射互操作 | DLR 动态站点（CLR） | `Reflector` 缓存委托；语义相同 |
| `proxy` | 生成子类 | 编译期生成真正的子类（cleat-043）：按原签名覆写基类的 public / protected 虚方法与抽象方法、实现接口，构造函数按原签名转发；fn 映射语义同 JVM；`proxy-super` 经生成的非虚入口直接调基类实现（JVM 临时改映射，并发不安全） |
| `gen-class` | JVM：只在 AOT 编译时生成；ClojureCLR：每个类一个程序集 | 不论是否 AOT 都在当前编译单元生成（cleat-043）；全部选项；`:main` 为 CLR 入口 `Main(string[])`；`assembly-load` 类名桥接；attribute 与 by-ref 参数不支持 |
| `definterface` | 生成接口 | 支持（M3 发射器生成接口类型） |
| 原始类型签名 `IFn.LL` 等 | 支持 | 第一版不支持；`^long` 参数装箱传递（后续拆箱） |
| 直接链接 | 可选 | 默认开（00 D13） |
| `*compiler-options*` `:disable-locals-clearing` 等 | 支持 | 局部变量清除见 cleat-042 第 5 项 |
| `ParseEval` | ClojureCLR 专有 | 不支持 |
| `^:async` / `await*` | ClojureCLR（.NET 11 运行时 async） | 支持（cleat-043）：运行时 async 可用时（.NET 11；.NET 10 要 `DOTNET_RuntimeAsync=1`）同 ClojureCLR，否则 await 同步等待、async 体返回已完成的 Task；库 `clojure.clr.async.task.alpha` |
| 尾调用 | 无 | 无（00 D14） |
| JVM 异常类名 | JVM：java.lang 隐式导入；ClojureCLR：不认 | 作为隐式导入映射到 Cleat.Core 实际抛出的 CLR 类型（下表，cleat-032） |
| `str` / 打印的浮点数与布尔 | JVM：`1.0`、`1.0E100`、`Infinity`、`true`；ClojureCLR：`1`、`1E+100`、`∞`、`True` | 照 JVM（`Cleat.Core.Runtime.JvmText`，cleat-032） |
| 字符串 / 字符 / 布尔 / 符号的 `hash` | JVM：`Murmur3.hashInt(String.hashCode)` 等；ClojureCLR：Murmur3 字符串哈希（命名空间部分用随进程变化的 .NET 字符串哈希） | 照 JVM：值与 JVM 相同、跨进程稳定，哈希集合 / 映射的遍历顺序与 JVM 相同（cleat-032） |
| `format` 的 `%e %f %g %a` | java.util.Formatter（最短十进制数字、HALF_UP、两位指数） | clojure.lang.Printf 照 Formatter 重写（cleat-032）；`%h` 对字符串为 String.hashCode |
| clojure.string 的 `split` / `replace` / `index-of` / `last-index-of` | java.lang.String / Pattern 语义 | 照 JVM：结果不含捕获组、limit 0 去尾空串、Java 的替换串语法（`\$`、`$1`）、from 下标按 Java 截断（cleat-032） |
| `re-matches` / 未参与匹配的组 | 整个输入匹配；组为 nil | 照 JVM（原来 `^p$` 包裹、组为 ""，cleat-032） |
| `#?` 读条件 | JVM 取 `:clj` | 取 `:cljr`（与 ClojureCLR 相同） |
| Clojure fn 对象 | 每次求值新建 | 同 JVM（没有自由变量的 fn 不再是单例，cleat-032） |
| 限定方法 `T/m`、`T/.m`、`T/new` 与 `^[…]` param-tags（Clojure 1.12） | JVM：QualifiedMethodExpr | 支持（cleat-192）：分析器改写成普通形式——值位置是 `fn*`（带 param-tags 时一个元数，原始类型实参经 `RT.xCast` 强制转换、引用类型带提示静态决议；不带时按重载的元数各一个子句，运行期反射）；静态字段 / 属性照旧读字段；调用位置的 param-tags 同样转换实参 |
| Var 的 `:tag`、静态属性 / 字段、loop 局部的类型 | JVM：VarExpr / StaticFieldExpr / LocalBinding 的 javaClass | 同 JVM（cleat-192）：参与宿主调用的静态决议；fn 的 Var（有 `:arglists`）的 `:tag` 是返回类型，不当作值的类型；原始类型提示的 Var 不用 |
| Java API（`.getMessage`、`Long/MAX_VALUE`、`java.io.File` …） | JVM 类库 | 不模拟；写 CLR 的名字（`.Message`、`Int64/MaxValue`、clojure.clr.io）。语料里这些行记为 `:host/jvm-api`（tests/conformance/cleat-divergences.edn） |

### 4.1 JVM 异常类名（cleat-032，`Cleat.Core.Runtime.JvmNames`）

短名进每个命名空间的默认导入（`RT.DefaultImports`，不覆盖 System 里同名的类型），全名（`java.lang.X` 等）经 `RT.classForName`。`catch` 与构造（`(IllegalArgumentException. "…")`）都用映射后的 CLR 类型；`(catch :default e …)` 捕获 `System.Exception`。

| JVM 名 | CLR 类型 | 依据（Cleat.Core 在此情形实际抛出的） |
|---|---|---|
| `Throwable`、`Exception`、`RuntimeException` | `System.Exception` | CLR 没有 Error / RuntimeException 层次；`ex-info` 是 `ExceptionInfo : Exception` |
| `IllegalArgumentException` | `System.ArgumentException` | 参数错误、`case` 无匹配、`ArityException`、format 的异常都派生自它 |
| `IllegalStateException`、`UnsupportedOperationException` | `System.InvalidOperationException` | `nth not supported`、`count not supported`、事务状态等 |
| `ClassCastException` | `System.InvalidCastException` | 强制转换失败；非数值操作数（`(+ 1 "a")`） |
| `NullPointerException` | `System.NullReferenceException` | 对 nil 调成员 |
| `ArithmeticException` | `System.ArithmeticException` | 除零、`*-exact` 溢出（含 `DivideByZeroException`、`OverflowException` 子类） |
| `NumberFormatException` | `System.FormatException` | 数字解析失败 |
| `IndexOutOfBoundsException`、`StringIndexOutOfBoundsException` | `System.ArgumentOutOfRangeException` | `nth` / `assoc` 越界、`subs` |
| `ArrayIndexOutOfBoundsException` | `System.IndexOutOfRangeException` | 数组下标越界（与上一行没有公共基类：`(catch IndexOutOfBoundsException …)` 接不住数组越界，语料记为偏离） |
| `InterruptedException` | `System.Threading.ThreadInterruptedException` | |
| `java.util.concurrent.TimeoutException` | `System.TimeoutException` | |
| `java.util.regex.PatternSyntaxException` | `System.Text.RegularExpressions.RegexParseException` | |
| `java.io.IOException` / `FileNotFoundException` / `EOFException` | `System.IO.IOException` / `FileNotFoundException` / `EndOfStreamException` | |
| `java.util.IllegalFormatException` 及其子类（`IllegalFormatConversionException`、`MissingFormatArgumentException` …） | `clojure.lang.IllegalFormatException` 及同名子类（Printf.cs；基类为 cleat-032 新增，派生自 `ArgumentException`） | format |

不映射：`Error`、`AssertionError`（`assert` 抛 `System.Exception`，与 ClojureCLR 相同）、`IllegalAccessError` 等 java.lang.Error 一支。

### 4.2 `clojure.lang.Compiler` 门面（cleat-189，`src/Cleat.Core/Runtime/Compiler.cs`）

cleat 的编译器是 C#（`Cleat.Compiler`，按需加载，[designs/30](30-csharp-compiler.md)），没有 JVM / ClojureCLR 的 `Compiler` 类。
Cleat.Core 里的静态类 `clojure.lang.Compiler` 只提供库与测试常用的公开成员，转到 cleat 的对应物：

| 成员 | cleat |
|---|---|
| `Compiler+CompilerException`（JVM `Compiler$CompilerException`） | cleat 的分析错误 `ExpanderException` 是它的子类：`(catch Compiler+CompilerException e …)` 接得住语法错误、未解析的符号 / 类名、宏展开错误。消息 `Syntax error (文件:行:列) 原因`；`ex-data` 有 `:clojure.error/source line column phase`（与 `symbol`）；宏抛出的原异常在 `.InnerException`；`PHASE_*` 关键字同 JVM |
| `munge`、`demunge`、`CHAR_MAP` | `clojure.lang.Munge` |
| `eval`、`load`（1 ~ 4 参，ClojureCLR 的第 4 参 relativePath 不用）、`loadFile`、`macroexpand1`、`macroexpand` | 运行时钩子 `RuntimeHooks.Eval` / `Macroexpand1`、`Loader.LoadSource` / `LoadFile`（编译器按需加载） |
| `specials` | 特殊形式表（值为 nil 的映射，只用来查键） |
| `SOURCE`、`SOURCE_PATH`（ClojureCLR 名 `SourceVar`、`SourcePathVar`）、`COMPILE_FILES`、`COMPILE_PATH`、`COMPILER_OPTIONS` | `*source-path*`、`*file*`、`*compile-files*`、`*compile-path*`、`*compiler-options*` 这几个 Var 本身 |

不提供（移植时的替代写法）：

- 分析器内部：`Compiler/analyze`、`analyzeSeq`、`Compiler$LocalBinding`、`$ObjExpr`、`$FnExpr`、`$HostExpr/maybeSpecialTag` 等。
  要分析形式用 tools.analyzer.clr（packages/tools.analyzer）；宏的 `&env` 里的局部是 `Cleat.Core.Expand.LocalBinding`（字段 `Name`、`Tag`）；
  `maybeResolveIn` 用 `clojure.core` 里同规则的 `ns-resolve` / `resolve`。
- 编译器状态 Var：`LOADER`（没有类加载器，绑定它的代码删掉即可）、`METHOD`、`LOCAL_ENV`、`LOOP_LOCALS`、`NEXT_LOCAL_NUM`、`LINE`、`COLUMN`、`LINE_BEFORE` 等。
- 执行期异常不包装：JVM 的 `Compiler.load` 把顶层形式执行时的异常包成 `CompilerException`（phase `:execution`），cleat 原样传出（位置记在 `Exception.Data`，报告时用），
  `catch` 原异常类型的代码不受影响。
- 宏的 spec 检查只在已加载 `clojure.core.specs.alpha` 时做（cleat-166 的决定，cleat-193 复核后维持：spec 是独立的包 packages/spec.alpha，不在运行时里，
  JVM 那样首次宏展开时自动加载做不到；加载后编译约慢 24%）：test_clojure special / def / fn 的 32 个"did not conform to spec"断言因此失败，
  先 `(require 'clojure.core.specs.alpha)` 则这些断言通过。

随门面修的几处行为（与 JVM 对齐）：直接调 `macroexpand` / `macroexpand-1` 时宏的元数错误原样抛 `ArityException`（宏自己的元数去掉 `&form` / `&env`；
编译途中仍是编译错误）；`(quote 1 2 3)` 的原因是带 `{:form …}` 的 `ExceptionInfo`；反射调用找不到成员的消息开头是 `No matching member m taking n args`；
`case` 的各分支类型相同时它就是 `case` 的类型（`(BigDecimal/Create (case 1 1 1))` 不反射）；int 测试而被测值已知不是原始类型时
`*warn-on-reflection*` 下报 `Performance warning … case has int tests, but tested expression is not primitive.`（类型未知的不报）；
`let` 的初值是另一个局部时沿用它的类型提示。

## 5. core 库的组织（00 D15）

```
src/clojure/
  cleat/kernel.clj         ; 新写：原语 Var（def 到 cleat.kernel/… 的原语）、编译器与 nanopass 要用的最小函数集（first/next/cons/seq/vector/conj/assoc/get/nth/count/str/symbol/keyword/gensym/meta/with-meta/apply/concat/list*…）
  clojure/core.clj         ; ClojureCLR core.clj 改写：开头 (ns clojure.core) 后 refer cleat.kernel；去掉对 HostExpr/MaybeType 的调用（改原语）；CLR 差异保留；LLVM 差异用 #?(:cleat-llvm …)
  clojure/core_deftype.clj, core_proxy.clj(子集), core_print.clj, core/protocols.clj, gvec.clj, instant.clj, uuid.clj
  clojure/string.clj, set.clj, walk.clj, edn.clj, data.clj, template.clj, test.clj, stacktrace.clj, repl.clj, main.clj, pprint/…, zip.clj, clr/io.clj, clr/shell.clj, reflect.clj
```

- **加载顺序**（boot 镜像的构建顺序）：`cleat.kernel` → `clojure.core` →（core 末尾按 ClojureCLR 顺序）`core_clr` → `core_proxy` → `core_print` → `core_deftype` → `core/protocols` → `gvec` → `instant` → `uuid` → `cleat.nanopass` → `cleat.compiler`。`string`、`set`、`walk`、`edn`、`pprint` 等也编进 boot 镜像（REPL 与编译器驱动要用），其余按需从源码或 `.clj.dll` 加载。
- `core.clj` 中依赖编译器内部的地方（`HostExpr/MaybeType`、`Compiler/…`）改为 `cleat.kernel` 原语。
- 协议：`defprotocol` / `extend-type` / `extend-protocol` 按 ClojureCLR `core_deftype.clj`（`MethodImplCache` + 接口快速路径）。
- `defrecord` / `deftype` / `reify` 展开到 `deftype*` / `reify*`（与 Clojure 相同），由编译器生成类型。

## 6. 验证

- 展开器单测（C#）：每个特殊形式的 Lcore 快照；错误信息与 Clojure 一致（"Unable to resolve symbol"、"Can only recur from tail position"、"Mismatched argument count"…）。
- `core.clj` 全部顶层形式经展开器得到 Lcore（无未知形式），作为 04 的快照输入与 08 的种子来源。
