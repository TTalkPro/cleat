# cleat-051 编译器的 Clojure 节点

- 里程碑：M4′　依赖：cleat-050　状态：完成
- 设计依据：[15 §3](../designs/15-scheme-core-vendoring.md)、[05](../designs/05-cil-backend.md)、[02 §4](../designs/02-runtime.md)

## 工作内容
1. L0 ~ L3 加节点：`var-ref` / `def` / `set!` 到 Var、`keyword-invoke`、`instance-of`、`deftype` / `reify`、`switch`（`case*`）、多类型 `$try/catch`、集合字面量；`targets.sls` 加 Clojure 运行时类名。
2. 调用约定：`ClojureFn : Procedure, IFn`（`invoke(k)` 0 ~ 20、`invokeStatic`、`RestFn` 变长）；Clojure 调用点发 `IFn.invoke(k)`；已知调用直接 `call`；`loop` / `recur` 复用 `label` / `jump`。
3. codegen：`deftype` → Lcil `type` + `implements` + 可变 / volatile 字段；`switch`；`try/catch` 多子句；`tail.` 对 Clojure 代码按机器描述表关闭。
4. `compile-small` 的 Clojure 版本（几十个小程序经编译 → CIL → 运行，与预期值比较）。

## 验收标准
- `compile-small` 通过；产物过 ILVerify。

## 结论（2026-10-10）

完成。Clojure 代码经 `(clojure syntax)` 展开为 Scheme 核心形式 + 编译器的 Clojure 节点（`$clj-…`），Scheme 编译器直接生成调用 `clojure.lang` 的 CIL：不再经 Scheme 过程常量与 Scheme 互操作的值转换；`fn*` 是真正的 `AFunction` / `RestFn` 子类，`deftype*` / `reify*` 是真正的 CLR 类型；Clojure 命名空间可以**保存为 dll**，在新进程里加载运行，ILVerify 通过。展开器（psyntax）没有改动，种子不需要重生成。

### 新节点：Lcore → L0 → Lcil

| Lcore（`(clojure syntax)` / `(clojure namespaces)` 产生） | L0 | Lcil |
|---|---|---|
| `($clj-var-ref 'v 'dyn?)` | `(clj-var-ref d st)` | `ldconst v; castclass Var; callvirt getRawRoot`（`^:dynamic`：`get`，与 Clojure 的 VarExpr 相同） |
| `($clj-var-set! 'v e)` | `(clj-var-set! d e)` | `callvirt Var::set(object)`，值为新值 |
| `($clj-def 'v 'init? 'dyn? init meta)` | `(clj-def d st0 st1 e0 e1)` | `setDynamic`（运行时也设，保存的 dll 加载后成立）、`bindRoot`、`setMeta`；值为 Var |
| `($clj-invoke f a …)` | `(clj-invoke e e* …)` | `castclass IFn; callvirt IFn::invoke(object × k)`（k > 20：`invoke(20 + object[])`）；不发 `tail.`（目标表 `clojure-tail-calls?` 为假） |
| `($clj-keyword-invoke 'k x [nf])` | `(clj-keyword-invoke d e e* …)` | `isinst ILookup` → `valAt(k [, nf])`，否则 `RT.get` |
| `($clj-call '方法 'n|#f a …)` | `(clj-call d0 d1 e* …)` | `call Cleat.Scheme.Clojure.ClojureOps::方法(object …[, object[]])`：集合字面量、`withMeta`、Reflector 互操作、`new`、`instance?`、monitor、import |
| `($clj-case '(shift mask 种类 条目) x default then …)` | `(clj-case d e0 e1 e* …)` | 键（`:int`：`IsNonCharNumeric` / `ConvertToInt`；否则 `Util.hash`；`shr` / `and`）→ 稠密时 IL `switch`，否则比较链 → `Util.equiv` / `ceq` 校验 → 分支；分支与 default 在尾位置（`check-refs` / `jumpify` 认得，`recur` 在分支里仍降为循环） |
| `($clj-fn '类名 '变长必需个数 (case-lambda …))` | `(lambda #<clj-fn-name> cl …)` | `(type "ns$name" (base AFunction|RestFn) …)`：固定子句 = `invoke(k)` 覆盖，变长子句 = `doInvoke` + `getRequiredArity`，`HasArity`；自由变量为字段 `c<i>`，无自由变量为单例；letrec 自引用为 `this` |
| `($clj-deftype '描述 (lambda (字段 …) ($clj-methods 方法 …)))` | `(clj-deftype d (x* …) le* …)` | `(type 类名 (interfaces 全集) (field … [volatile]) .ctor 方法 (implements I m) 桩 getBasis)`；reify：自由变量 + `__meta` 作构造参数，生成 `meta` / `withMeta`；值：deftype 为 Type（`ClojureOps.DefineType` 登记，`classForName` 与展开期解析都取最新），reify 为新实例 |

- fn* 在前中端就是普通 lambda（名字位置是 `clj-fn-name` 记录），自由变量、装箱、letrec、recur 的命名 let → `label` / `jump` 全部照旧；只有 codegen 分支生成函数类。类名即 Clojure 的 `ns$name`（`AFn.WrongArityException` 用类型全名，ArityException 文本与 050 相同），单元内重名加 `__N`。`ClojureFn` 删除。
- deftype 字段是节点绑定的变量：分析表 `fields` 使它们被 `set!` 也不装箱，方法里读写 `this` 的字段（`dfield` 位置）。方法签名、接口全集、未直接实现的接口方法（同名同参的转调 + 返回值转换，否则抛 `NotImplementedException`——CLR 要求实现全部接口方法）在展开期由 `HostSyntax.TypeMethod` / `InterfaceLayout` 算好写进描述，编译器不反射。原始类型参数入口按 Clojure 规则装箱（int → long），返回值按 `Reflector.Convert<T>` 转换。方法体可以引用正在定义的类型：展开前登记同名桩类型，运行时 `ClojureOps` 把桩换成真正的类型。
- `try`：050 的展开（`$try/catch` 多类型 catch + `$try/finally`）原样编译；try 区域内没有 `tail.`（测试检查 fn 类的 Lcil）。
- 常量：Lcore 里不再有过程常量（测试检查）；保存时经常量编码器的"外来常量"挂钩（`set-foreign-constant-encoder!`，`(clojure compile)` 登记）：Var → `(var ns name)`（加载时 intern）、long、关键字、符号 / 列表 / 向量 / 字符串（发射器新增无歧义的 `csym` `clist` `cvec` `cstr`，LCIL.md §8）、map / set / sorted、带元数据、BigInt / BigDecimal / Ratio、正则、Type。

### `(clojure syntax)` 的变化

运行时操作全部从 `((quote 过程) …)` 改为节点：过程式形式直接返回节点，模板（转换器式形式）经隐藏形式 `%node` 嵌入（`(%node 头 (数据 …) 子形式 …)`）。Var 读写在 `(clojure namespaces)`；调用、关键字调用、集合、互操作、`def`、`fn*`、`case*`、`deftype*` / `reify*` 在 `(clojure syntax)`。新库 `(clojure compile)`：`clojure-compile-forms` 逐形式展开、求值、收集 Lcore，编成一个持久化 dll（cleat-052 正式驱动之前的最小版本）。C#：`ClojureOps` 只剩展开期过程 + `$clj-call` 用的静态方法；`HostSyntax` 加 `InterfaceLayout` `IsValueType` `DefineStub` / `Real`；`Cleat.Core.Runtime.Loader.RegisterType` 与 `TypeResolver.Forget`。

### 度量（同一测量脚本，预热后；旧镜像在临时 worktree 构建）

| | 改动前 | 改动后 |
|---|---|---|
| 展开一个 Var 引用 | 6.8 ~ 7.5 µs | 6.3 ~ 6.8 µs（不变：049 的 38 µs 在 050 已降到这里，剩下的是语法对象开销） |
| 展开 + 编译 + 运行，每个 Var 引用（100 个的 `do`） | 72 ~ 74 µs | 28 ~ 29 µs |
| `loop` 一百万次、每次 3 个 Clojure 调用 | 209 ms | 152 ms（其余主要是 `Numbers` 的反射互操作） |
| `case*`（8 个 int 分支）每次分派 | 约 166 ns | 约 19 ns |

### 测试

- 新增 `tests/scheme/clojure/compile-small.sps`（79 项）：闭包、多 arity、变长（infinite `applyTo`、> 20 实参、20 个必需参数）、命名 fn 递归、loop / recur 一百万次、letfn 互递归、Var 重定义对已编译调用者可见、动态绑定与 `set!`、关键字调用（含 ILookup deftype）、集合 / Var 作函数、互操作、deftype* / reify*（C# 调用 Counted / Indexed / IComparer、Equals 覆盖、volatile 字段、桩与桥接、方法内 recur、引用自身类型、重定义）、case*（int 稠密 / 稀疏、hash-equiv、skip-check、identity、分支里 recur）、try / catch / finally 顺序与操作数位置的 try、形状断言（节点、IFn.invoke、AFunction 子类、IL switch、try 区域无 `tail.`、Lcore 无过程常量）；Scheme 过程作 Clojure 函数调用抛 InvalidCastException。
- 新增 `tests/scheme/clojure/aot.sps`：样例命名空间编成 dll → ILVerify 通过 → 删除本进程的命名空间 → **子进程**只加载 dll，18 项调用全部正确（Var 由常量表 intern、deftype 类型来自该程序集）。
- `syntax.sps` 237 项（deftype* / reify* 由形状断言改为行为断言，letfn 的类名）、`namespaces.sps` 57 项；`RuntimeClauseTests` 加无歧义编码（2 项）。
- `dotnet test Cleat.slnx`：Cleat.Core.Tests 129 通过；Cleat.Scheme.Tests **224 通过**（含自举不动点、ILVerify、44 个快照——快照没有变化，Scheme 代码的生成不受影响），该程序集 5 分 33 秒。安装的 stage3 镜像 sha256 前缀 `54b20f21f5d4f1be`（dll）、`65f2bc0c3e0e493f`（lcil）。

### 未做 / 留给后续

- 已知函数的直接链接（`invokeStatic`、跨单元 `call`）与接口文件：cleat-052 / 053（designs/04 §6）。
- 互操作的静态决议（有类型提示时直接 `call`，不经 `Reflector`）、`catch` 处理器内联（现在每次执行 try 分配一个处理器闭包）、原始类型局部变量：cleat-042。
- deftype 字段一律 object（类型提示只用于方法签名）；`defrecord` 需要的 `IRecord` / `IPersistentMap` 方法由宏生成（cleat-053）；deftype* 方法不能捕获外层局部（报错，与 Clojure 相同）。
- 顶层 `(do …)` 拆分、`compile` / `compile-file`、`.clj.dll` 的接口文件与加载器接入：cleat-052（`(clojure compile)` 是它的雏形）。

## 进度
- 2026-10-10 基线（改动前，镜像 + `(clojure syntax)`，`tests` 之外的测量脚本）：展开一个 Var 引用 7.5 µs（100 个引用的 `do` 中每个 6.8 µs）；展开 + 编译 + 运行，100 个引用的 `do` 中每个 57.8 µs，单个引用的形式 888 µs（以发射为主）。Lcore 形状 `('#<procedure clojure-var-root> '#<Var>)`。
- 2026-10-10 设计定稿：
  - **Lcore 新核心形式**（`$clj-…`，只由 `(clojure syntax)` / `(clojure namespaces)` 的过程式形式与隐藏形式产生，数据操作数一律 `(quote …)`，展开器的 `rewrite` 原样通过，**展开器不改、种子不重生成**）→ parse-core 识别 → L0 节点 → codegen 直接发 IL，不经 Scheme 互操作的转换：`($clj-var-ref 'v 'dyn?)`、`($clj-var-set! 'v e)`、`($clj-def 'v 'init? 'dyn? init meta)`、`($clj-invoke f a …)`、`($clj-keyword-invoke 'k x [默认])`、`($clj-call '方法 '固定个数|#f a …)`（`ClojureOps` 的静态方法，参数与返回都是 object，可把尾部实参打包成 object[]）、`($clj-fn '类名 '变长必需个数|#f (case-lambda …))`、`($clj-deftype '描述 (case-lambda ((字段 …) ($clj-methods 方法lambda …))))`。
  - **fn\***：L0 仍是 `lambda`，名字位置放 `clj-fn-name` 记录（类名、变长子句的必需参数个数），前中端 pass 不变（自由变量、letrec 自引用、recur 的命名 let → label 都照旧）；codegen 对它生成 `AFunction` / `RestFn` 子类：固定子句 = `invoke(k)` 覆盖，变长子句（参数为"必需 + 剩余 seq"，与 050 的变长过程同形）= `doInvoke` 覆盖 + `getRequiredArity`；自由变量为实例字段，无自由变量的为单例。类名即 Clojure 的 `ns$name`（`AFn.WrongArityException` 用 `GetType().FullName`，所以 ArityException 文本与 050 相同），单元内重名加 `__N`。`ClojureFn` 退役。
  - **调用**：`callvirt IFn::invoke(object × k)`（k ≤ 20，以上 `invoke(20 + params)`），被调方先 `castclass IFn`：Scheme 过程不会作为 Clojure 函数值出现（`fn*` 是函数类、集合 / 关键字 / Var 本身是 IFn），万一出现则与 Clojure 调用非函数一样抛 InvalidCastException（有测试）；Clojure 节点不发 `tail.`（目标表 `clojure-tail-calls?` 为假）。
  - **常量**：保存到 dll 时编码。Fasl 的二进制标签本来就区分 Scheme / Clojure 的符号、列表、向量、字符串，只有文本名 `sym` `list` `vec` `str` 按程序集 runtime 解释——发射器加无歧义的 `csym` `clist` `cvec` `cstr`；常量编码器加"外来常量"挂钩（最先查），`(clojure syntax)` 登记 Clojure 值的编码（Var → `var`、long → `long`、关键字、符号、集合、带元数据、Type、BigInt …）。于是 Clojure 单元的 Lcore 里不能再有 `(clojure syntax)` 库变量的引用与过程常量：全部运行时操作改为上面的节点。
  - **deftype\* / reify\***：字段是 `$clj-deftype` 绑定的变量（不装箱，`set!` 写字段，`volatile` 字段），方法是 lambda（`this` + 参数），codegen 生成类型：`interfaces`、按方法签名的显式实现（`implements`，同名同参的其他接口方法生成桥接）、构造函数、静态 `getBasis`；reify 的自由变量为构造参数 + `__meta`，自动实现 `IObj`。值：deftype 为 Type（运行时登记到 `Loader`，`classForName` 取最新的），reify 为新实例。
- 2026-10-10 实现：编译器 L0 加 `clj-var-ref` `clj-var-set!` `clj-def` `clj-invoke` `clj-keyword-invoke` `clj-call` `clj-deftype`，fn* 为带 `clj-fn-name` 的 lambda（`languages` `parse` `loops` `front` `closures` `codegen` `targets` `constants`）；`(clojure syntax)` / `(clojure namespaces)` 的运行时操作全部改为节点（模板经隐藏形式 `%node`）；C#：`ClojureOps` 加静态方法（`VectorOf` `CallInstance` … `DefineType` `BoxArg` `ConvertReturn`），`HostSyntax.InterfaceLayout` / `IsValueType`，`Loader.RegisterType`（`TypeResolver.Forget`），发射器加 `csym` `clist` `cvec` `cstr`。镜像照常重建（展开器未改，种子不动）。`syntax.sps` 237 项（deftype* / reify* 的形状断言改为行为断言，letfn 的类名改为 `expander$g`）、`namespaces.sps` 57 项通过。新库 `(clojure compile)`：`clojure-compile-forms` 逐形式展开 + 求值 + 收集 Lcore → 一个持久化 dll，登记 Clojure 常量编码器。手工验证：样例命名空间（def、^:dynamic、fn、变长、deftype 可变字段、try/catch、常量集合）存为 dll，**另一个进程**加载后调用全部正确，ILVerify 通过。
- 2026-10-10 测试：新增 `tests/scheme/clojure/compile-small.sps`（71 项：闭包、多 arity、变长与 >20 实参、命名 fn 递归、loop / recur 一百万次、letfn 互递归、Var 重定义、动态绑定与 set!、关键字调用、互操作、deftype* / reify*（C# 调用 Counted / Indexed / IComparer、Equals 覆盖、volatile 字段、桩与桥接、方法内 recur、重定义取最新）、try / catch / finally 顺序与提升、Lcore / Lcil 的形状：节点、IFn.invoke、fn 类、try 区域无 tail.、Lcore 里没有过程常量；Scheme 过程作 Clojure 函数调用抛 InvalidCastException）；`tests/scheme/clojure/aot.sps`（样例命名空间存 dll → ILVerify 通过 → 子进程只加载 dll、18 项调用全部正确）。调试中修正：deftype* 登记类型时同时登记到 Scheme 的 TypeResolver（`HostSyntax.MaybeType` 经它解析，不清缓存则重定义后仍解析到旧类型）。公平对比（同一测量脚本、预热后，旧镜像在临时 worktree 构建）：展开一个 Var 引用 6.8 ~ 7.5 → 6.3 ~ 6.8 µs（不变：主要是语法对象的开销）；展开 + 编译 + 运行，每个引用 72 ~ 74 → 28 ~ 29 µs。运行时：`loop` 一百万次每次 3 个 Clojure 调用 209 → 152 ms（其余是 `Numbers` 的反射互操作）。
- 2026-10-10 deftype* 方法体引用自身类型（桩类型）、case* 改为 `$clj-case` 节点（IL switch，分派 166 → 19 ns），`ClojureOps.CaseIndex` 删除；compile-small 79 项；全量测试通过（Core 129、Scheme 224）；结论已写，TASK.md 改为完成。
