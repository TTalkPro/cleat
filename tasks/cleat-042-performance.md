# cleat-042 性能：拆箱、协议内联缓存、跨单元直接链接

- 里程碑：后续　依赖：cleat-032　状态：完成
- 设计依据：[04 §10](../designs/04-compiler.md)、[06 §4](../designs/06-llvm-backend.md)

## 工作内容
1. 基准集（`bench/`：语料中的算术 / 集合密集用例 + ClojureCLR 常用基准），记录 M6 基线。
2. 类型推断与拆箱：`^long`/`^double` 局部与 `label` 形参用 `int64`/`double` 局部变量（IronScheme iron-034c 的工作表法）；原始类型签名 `IFn.LL` 等。
3. 协议调用内联缓存（站点类或 `MethodImplCache` 快速路径内联）；`keyword-invoke` 站点缓存。
4. 跨单元直接链接到用户 `.clj.dll`；`:inline` 之外的小函数内联。
5. 局部变量清除（`locals-clearing`）。
6. LLVM：NaN-boxing / 小整数立即数、`musttail`、`hoist-var-derefs`。

## 验收标准
- 每项有基准前后数字；不动点与符合性基线不退化。

## 进度（2026-10-11）

1. **基准集与基线** —— 完成。`bench/` 七个基准（sum_loop、fib、reduce_vec、lazy_chain、assoc_map、protocol、keyword_invoke），
   `tools/build.cs bench [-r n] [--write] [--only re]` 聚合最小/中位。基线（Release，5 次，`bench/baseline.edn`）：
   sum_loop 1006 ms（500 万次装箱算术）、fib 49 ms（fib 25）、reduce_vec 119 ms（100 万 reduce）、
   lazy_chain 349 ms（30 万惰性链）、assoc_map 219 ms（20 万 assoc）、protocol 517 ms（40 万分派）、
   keyword_invoke 631 ms（50 万 `(:k m)`）。之后的每项优化对照此表。
4. **跨单元直接链接到用户 `.clj.dll`** —— 已在 cleat-033 完成（受保护的 invokeStatic 直调，接口文件登记 + with-redefs 回退；
   本基准的数字即含此优化的状态）。
6. **LLVM** —— 整体搁置（TASK.md 2026-10-10 决定），不在本任务内。

待做：2（类型推断与拆箱，iron-034c 工作表法 + `IFn.LL` 原始签名）、3（协议与 keyword-invoke 的内联缓存）、
4 剩余（`:inline` 之外的小函数内联）、5（locals-clearing）。每项完成后在此记基准前后数字。

### 第 2 项的先期分析（2026-10-11，实验记录）

- `+` / `<` / `inc` 的 `:inline` 已生效：`(clojure.core/+ acc i)` 与手写
  `(. clojure.lang.Numbers (add acc i))` 同速（1051 vs 1049 ms / 500 万次）——内联不是瓶颈。
- 实验加了 Int64 快速路径（`Numbers.add` 等五处 `is long` 直连原语，省去 `ops(x)` 的
  `Type.GetTypeCode` 双重分派）：sum_loop 103%–105%，无改善——分派也不是瓶颈。
- 装箱实验：同样的循环去掉 `acc` 累加（每迭代 1 次分配而非 2 次）1126 → 724 ms——**成本在
  `Numbers.add(object,object)` 的结果装箱与 GC 压力**（每迭代 ~80 ns/次分配）。
- 结论：第 2 项的正确形态是完整拆箱——`^long`/`^double` 局部与 loop 形参用 int64/double
  局部变量、`+`/`<`/`inc` 在类型已知时发原始 `add.ovf`/`clt`（checked 溢出语义必须保留，
  不能用无检查的 `add`）；已移植的 `infer-var-types!` 只认 Scheme 的 `$fx…`/int32 语义，
  对 Clojure 的 Int64 返回未知，需要为 `$clj-call` 节点补 Int64 类型的推断与原始发射。
  预期 sum_loop 1006 → ~150 ms。尚无对外的 C# 改动（实验已回退）。

### 第 2 项（整数部分）完成（2026-10-11）

根因：之前几轮的推断与发射设施都在，但形状识别从未命中。`IRONSCHEME_DUMP_PASSES=前缀` 直接给出 L3：
`(+ acc i)` 展开为 `($clj-call "CallStatic" 3 '<System.Type clojure.lang.Numbers> '"add" '() acc i)`——
**类型实参是 Type 对象而非字符串**（`string?` 恒假），泛型实参表是 `'()`（先前"gen 是 #f"的判断不对）。
此外 Int64 字面量的 `real?`/`exact?` 判定不成立、`MaybeTyped` 不认 clj-call、发射后未装箱、
手写的溢出 IL 混用 int32/int64 操作数（非法 IL）。

改法（codegen.sls）：
- `numbers-arith-op` 识别 `add/minus/multiply/inc/dec`（→ long）与 `lt/lte/gt/gte/equiv`（→ bool），类型实参接受 Type 或字符串。
- 推断：Int64 字面量 → l64；clj-call 的 long 运算参与 `MaybeTyped`、`deps` 与暂定 top 收敛。
- 发射：实参以原始 int64 求值后**直接调用 Numbers 的 `(long …)` 重载**（溢出检查与 object 重载完全一致，JIT 内联），
  不再手写溢出 IL；value 上下文装箱 Int64，比较走 `finish-bool!`；Int64 常量发 `ldc.i8`。
- 清理了上一轮遗留的 `/tmp/opencode` 探针。

语义核对：溢出仍抛 ArithmeticException "integer overflow"；double 累加、`*'` 自动提升、`dec`/`-`、`dotimes` 结果不变。
583 测试全绿（含两个不动点与符合性门禁）。

| 基准 | 基线 min | 现在 min | 比例 |
|---|---|---|---|
| sum_loop | 1006 | 13 | 1% |
| assoc_map | 219 | 173 | 79% |
| keyword_invoke | 631 | 541 | 86% |
| protocol | 517 | 471 | 91% |
| reduce_vec | 119 | 107 | 90% |
| fib | 49 | 45 | 92% |
| lazy_chain | 349 | 328 | 94% |

（非 sum_loop 的改善来自 core 内部的循环——`dotimes` 等——也被拆箱。）

第 2 项剩余：`^double` / 浮点的 Clojure 路径（`Numbers` 的 double 重载）、`^long`/`^double` 提示的形参与返回、
`IFn.LL` 等原始签名（fib 这类跨调用的算术）。

数学语义方向（2026-10-11 用户决定）：数学层面不必逐条对照 JVM Clojure，优先贴合 CLR 自身特性；
具体取舍（溢出异常类型、checked 指令、整数类型族等）在后续项中逐个定。

### 第 3 项（第一步）：宿主调用的编译期静态决议（2026-10-11）

先测后做：分项计时显示瓶颈不是分派缓存，而是**所有 `(. T (m …))` 互操作调用都在运行时走 Reflector**
（`ClojureOps.CallStatic/CallInstance/Get…Field` → 每次分配实参数组与缓存键、查表、委托调用，~100 ns）。
core 的 `:inline`（`nth`、`rem`、`long`、`get` …）、协议函数里的 `.fnFor` / 接口方法调用、record 的 `valAt` 全在这条路上。
真正的内联缓存在它面前是次要的。

改法：
- `ClojureStaticCalls.Resolve` / `ResolveField`（C#，编译期由 codegen 调用）：按 JVM Clojure 的
  `paramArgTypeMatch` / 最具体规则选重载；实参静态类型来自数字 / 字符串字面量与推断为 long 的变量，
  未知实参对 clojure.lang 的方法只匹配引用类型形参（`Numbers.add(object, object)`、字面量 `(/ 1.0 0.0)` → `divide(double, double)`）。
  **其余类型（BCL、用户类型）只在唯一相容重载时静态决议**，形参限 object / 原始类型 / String / Core 类型、不含委托
  （`String.IndexOf(char|string)` 这类按运行时类型选的重载、IFn → 委托的转换仍交给 Reflector）。
- codegen：`call`/`callvirt`/`ldfld` 直发；实参转换 `ArgInt32/…`（= Reflector.Convert）、引用形参 `castclass`（JVM 的 checkcast），
  返回值同 Reflector 的 ToClojure；后面操作数含 try 时先全部存 object 临时变量再转换。无实参且无方法时读公共字段（含 volatile.）。
- deftype / defrecord 方法体里 `this` 是桩类型：桩登记 deftype 的接口表（`HostSyntax.DefineStub` 新重载），
  `(.valAt this …)` 按接口 callvirt。
- 协议方法构造器给 `cache#` 加 `^clojure.lang.MethodImplCache` 提示（JVM 靠局部类型推断得到）。
- 发射器：`"类型, eval$N"` 指向本单元重定义的同名类型时，方法 / 字段也在本单元的定义里找（AOT 镜像里的协议接口）。
- 调试：`CLEAT_DUMP_LCIL=1` 时 AOT 单元旁写 `.clj.dll.lcil`。

符合性：5 条语料分歧消失（"Reflector 重载未命中而不是 ClassCastException"——现在是 castclass 给出 CCE），从分歧表删除；
cts `not-eq` +1 通过（基线已重写）。583 测试全绿。

| 基准 | 基线 | 拆箱后 | 现在 |
|---|---|---|---|
| sum_loop | 1006 | 13 | 12 |
| protocol | 517 | 471 | 112 |
| keyword_invoke | 631 | 541 | 163 |
| lazy_chain | 349 | 328 | 147 |
| assoc_map | 219 | 173 | 83 |
| reduce_vec | 119 | 107 | 54 |
| fib | 49 | 45 | 19 |

第 3 项剩余：extend-protocol 路径的 `MethodImplCache.fnFor`（~50 ns）可做调用点单态缓存；
`(m k)`（map 作为函数）与 `(:k m)` 的站点缓存；局部变量的类型推断（let 绑定取初值的静态类型，省去 `^Type` 提示）。

### 第 3 项（第二步）：剖析驱动的两处修正（2026-10-11）

`dotnet-trace`（~/.dotnet/tools/dotnet-trace，speedscope 格式自己汇总）剖析 protocol 基准：
- **Reflector 仍占 30%**：`(nth v (mod i n))` 的 `RT.nth(object, int)`——实参静态类型未知、形参是 int，JVM 规则下不匹配。
  JVM 的 Compiler 在**该参数个数只有一个方法时直接用它**（实参按形参拆箱转换），再才看类型匹配；补上（clojure.lang 的类型）。
  已知 l64 的实参传 long / int 形参时原始求值（int 经 `RT.intCast(long)`，与 Reflector 的转换一致），`ArgInt32` 加 long 快速路径。
- **每个 defn 的函数都被包成 MetaWrapper**：defn 在 fn 形式上放 `{:rettag …}`，`fn*` 把非空形式元数据一律
  `WithMetaOf` → `AFunction.MetaWrapper`（RestFn）。后果：直接链接的守卫（isinst 函数类）恒失配，每次调用走
  `doInvoke → applyTo → ApplyToHelper`。JVM 的 FnExpr 去掉 `:rettag`（与 line/column/file）后才决定是否带元数据；照做。

语料又一条"Reflector 重载未命中"分歧消失（repeat 的非数字计数），从分歧表删除。583 测试全绿。

| 基准 | 基线 | 第一步后 | 现在 |
|---|---|---|---|
| protocol | 517 | 112 | 63 |
| keyword_invoke | 631 | 163 | 69 |
| lazy_chain | 349 | 147 | 103 |
| assoc_map | 219 | 83 | 75 |
| reduce_vec | 119 | 54 | 19 |
| fib | 49 | 19 | 17 |
| sum_loop | 1006 | 12 | 13 |

剖析中剩余的大头：`mod` 等小 defn 的装箱泛型算术（第 2 项的 `IFn.LL` 原始签名 / 第 4 项的小函数内联）、
extend-protocol 路径上 `MethodImplCache` 的 MRU 在两种类型交替时失效（`FindFnFor` ~8%）。

### 第 3 项收尾 + 第 2 项阶段 A：原始形参与宿主调用的原始返回值（2026-10-11）

- `MethodImplCache` 记最近两项（多态调用点两种类型交替时不再回表查找）：protocol 63 → 57 ms。
- **^long / ^double 形参**（fn*）：形参的循环初值经 `RT.longCast` / `RT.doubleCast`（JVM 原始形参的入口转换：
  `(f 2.7)` → 2，`(f nil)` → NRE，NaN → 0），编译器把它推断为 int64 / double 局部；parse 把这种转换计入有类型的子句，
  没有 recur 的 fn 也做推断。
- **静态决议的宿主调用参与类型推断**：返回 long / double 的调用（`RT.longCast`、`Numbers` 的 double / long 重载 …）
  是 l64 / fl 表达式，`cg-raw` 直接留原始值；实参静态类型来自推断（double 运算经 `Numbers.add(double, double)` 等，
  long / double 实参原始传入）。**正确性**：推断不动点过程中类型是乐观的，按未收敛的类型选重载可能导致错误转换——
  推断中实参类型未定时结果暂为 top、不缓存；收敛后全部候选的类型记入 `sound-types`，代码生成按它选重载，与推断给出的
  返回类型一致（`var-types` 只是其中决定拆箱的子集）。C# 侧 `Resolve` 按（类型, 名字, 实参类型, 静态?）缓存。
- `Numbers` 的混合重载 `(long, Object)` / `(Object, long)` 在另一侧也是 long 时直接走 `(long, long)`（原实现先装箱再分派）；
  `quotient` / `remainder` 的 `(long, long)` 除零与 object 重载一样报 ArithmeticException "Divide by zero"
  （类型已知时编译器直调它，结果不应随推断而变）。

语料 3 条原始提示相关分歧消失（`^double` 拒绝 nil、`^long` 把 NaN 当 0、reduce 的 `^double` 形参），已从分歧表删除。
583 测试全绿（cts 有一次计时相关的偶发差异，复跑一致）。

新基准 `prim_fib`（`(defn fib ^long [^long n] …)`，fib 27）：27 ms——跨调用仍装箱（invokeStatic 是 object 签名），
留给阶段 B（原始签名的 invokeStatic 与调用点）。

| 基准 | 基线 | 现在 |
|---|---|---|
| sum_loop | 1006 | 13 |
| protocol | 517 | 53 |
| keyword_invoke | 631 | 57 |
| lazy_chain | 349 | 86 |
| assoc_map | 219 | 68 |
| reduce_vec | 119 | 37（19 ~ 38 双峰） |
| fib | 49 | 17 |
| prim_fib | — | 27 |

### 第 2 项阶段 B：原始签名（JVM 的 IFn$LL）（2026-10-11）

按 JVM 的做法，不靠接口文件：
- **fn 一侧**：固定子句的形参 / 返回带 `^long` / `^double`（返回取参数向量的提示，没有时取 fn 形式的 `:rettag`——defn 名字上的
  `^long`）、不超过 4 个形参、`clojure.lang.primifs` 有对应接口（`LL`、`OL`、`DD` …）时，`$clj-fn` 带签名
  （`($clj-fn '类名 '变长 '((参数个数 . "LL") …) (case-lambda …))`，parse 与接口文件的读取同步改为 5 元素）。codegen：
  - 体编成有类型的方法：无字段时静态 `invokeStaticPrim(self, long …) : long`，L / D 形参是原始参数（新的位置 `targ`），
    推断把它们当固定类型；返回在尾位置——叶子表达式已是该原始类型时直接返回（`cg` 外包一层），否则按 JVM 的返回转换；
    这种方法不发 `tail.`。有字段时体是实例 `invokePrim`。
  - 转发：object 签名的 `invokeStatic`（直接链接照旧可用）、实现接口的 `invokePrim`、有字段时的 `invoke` 都转换后转发。
- **调用点**：Var 的 `:arglists` 里该参数个数的参数表带原始提示（返回提示缺省取 Var 的 `:tag`）时生成
  `($clj-call "PrimInvoke" #f '签名 Var读 实参 …)`：根 castclass 成 `primifs.<签名>`，已知原始类型的实参原始求值，
  callvirt `invokePrim`，结果是 l64 / fl 表达式（推断与 `cg-raw` 都认）。`def` 在展开初值前就把 `:arglists` 放上 Var，自递归也走这条路。
  根被换成没有该签名的函数时 InvalidCast（JVM 相同）。
- **转换语义（JVM）**：实参 `RT.longCast` / `doubleCast` 但只收数字（`"42"`、`\a` 是 ClassCastException，nil 是 NPE）；
  返回值 `Number.longValue()` / `doubleValue()`（大整数与比值取低 64 位、浮点饱和、NaN 为 0）——`ClojureStaticCalls.PrimArg… / PrimRet…`。

符合性：语料 13 行由失败转为通过（"primitive hint coercion" 一组、`#()` 的返回提示、非数字实参的 CCE），
逐行对比前一提交的结果没有任何回退；分歧表里标"target cleat-042"的条目全部删除（共 13 条）。583 测试全绿。

prim_fib（`(defn fib ^long [^long n] …)`，fib 27）：27 → **4 ms**。

| 基准 | 基线 | 现在 |
|---|---|---|
| sum_loop | 1006 | 12 |
| protocol | 517 | 53 |
| keyword_invoke | 631 | 56 |
| lazy_chain | 349 | 87 |
| assoc_map | 219 | 64 |
| reduce_vec | 119 | 33 |
| fib | 49 | 17 |
| prim_fib | — | 4 |

剩余：第 4 项的小函数内联（无提示的 `mod`、`fib` 这类装箱泛型算术）、第 5 项 locals-clearing；
局部变量按初值静态类型推断（省去 `^Type` 提示）。

### 第 4 项：小函数内联（2026-10-11）

先剖析（fib）：两处与内联无关的固定开销先去掉——
- 静态决议调用后的 `RetObject` 走 `Reflector.ConvertFrom` 的类型分派，占 fib 的 35%：long / double / bool / 引用类型直接返回。
- 直接链接调用点每次 `newobj` 一个实例作 self：守卫已确认 Var 的根就是该函数类的实例，直接用根（普通 invoke 的 this 也是它）。
  fib 17 → 9 ms、protocol 53 → 43 ms。

**内联**（JVM 没有的；按 CLR 的做法在编译期做）：
- AOT 单元（clojure.core 等）的顶层 `(def v (fn* …))`，非宏、无自引用、固定子句小（≤160 个序对）且只含 let / loop / if / 调用
  （不含 set!、fn*、deftype、def、try 等）时，接口文件旁写 `.inl`（二进制：镜像 id + 各子句 Lcore 的 fasl）；`.ifc` 不变
  （启动时要解析；把内联体放进 .ifc 会让它 134 KB → 1.1 MB、启动多 ~40 ms）。`.inl` 第一次要用时才读，镜像 id 不符的旧文件忽略。
- 调用点（展开器 var-call）：`((lambda (r a …) (if (instance? 函数类 r) ((case-lambda 子句) a …) (r a …))) Var读 实参 …)`——
  守卫保证 with-redefs / 重定义照常；实参只求值一次；子句的约束变量逐一 α 改名（前端按符号建表，同一体内联多次名字不能重复）。
  构建 Clojure 镜像时关闭（`inline-calls?`）：镜像里的子句不含内联结果，A / B / C 各级 `.inl` 相同，不动点成立。
- 变长子句的形参在 Lcore 里也是真列表（必需参数 + 剩余 seq），必须跳过（否则 `(commute r f x)` 把 x 当剩余 seq）。
- **语义边界**：内联后函数边界上的 Object 不再存在，实参的静态类型会流进体里改变重载选择。long 的两组重载行为相同；
  double 不同（`Util.equiv` 的同一性捷径使 JVM 上 `(not= ##NaN ##NaN)` 为 false；double 除零 object 重载报错、double 重载得 ##Inf）。
  内联的形参与实参临时变量带 `$inl` 后缀，推断不把它们定为 double。
- 配套：`(instance? T x)` 的 T 是能按名字引用的常量时直接 `isinst`（守卫与用户代码都受益）；
  宿主调用的实参在推断里算原始用途（通常选到原始类型的重载）；
  Clojure `if` 的测试 `(let ((t X)) (if (null? t) #f t))` 中 X 是返回 CLR bool 的调用时直接在 test 上下文求值（不装箱再判断）。

内联的合流点：守卫两支（内联体 long、退回的 invoke 是 object）合流后结果装箱——不做投机类型（没有去优化就不安全）。

符合性：语料 "eduction reduces" 由抛 CCE 转为正确的 9（分歧表那条的"jolt 值"说明本就不对），删除；cts 与基线一致；583 测试全绿。

**基准的预热**：现在生成代码很快，前几轮的时间主要是框架（CoreLib 等 ReadyToRun 代码）还没升到 tier-1——调用计数的 100 ms 窗口在
启动期不断被新 JIT 推迟。`DOTNET_TieredCompilation=0` 稳态反而更慢（protocol 72 vs 33 ms，失去动态 PGO 的接口去虚化），所以不改运行时设置；
min 是稳态指标，中位数呈双峰（keyword_invoke 21 / 69、protocol 33 / 71）。

| 基准 | 基线 | 第 4 项前 | 现在（min） |
|---|---|---|---|
| sum_loop | 1006 | 12 | 11 |
| protocol | 517 | 53 | 33 |
| keyword_invoke | 631 | 56 | 21 |
| lazy_chain | 349 | 87 | 82 |
| assoc_map | 219 | 64 | 61 |
| reduce_vec | 119 | 33 | 33 |
| fib | 49 | 17 | 10 |
| prim_fib | — | 4 | 4 |

剩余：第 5 项 locals-clearing；内联只覆盖 AOT 单元（REPL / 源码加载的 defn 还没有内联体）；局部变量按初值类型推断。

### 第 5 项：局部变量清除（2026-10-12）

目标同 JVM 的 locals clearing：惰性序列的头不被调用者的局部变量 / 参数 / 临时变量钉住，`(reduce + (map inc (range 3e7)))` 这类在小堆上能跑完。
（分支 `wip/cleat-042-locals-clearing` 的半成品合入后继续。）

**codegen（只对 Clojure fn / deftype 方法；Scheme 代码不变，种子不动）**：
- 方法入口按生成顺序数每个局部 / 参数的引用（`init-clearing!` / `walk-var-refs`）；最后一次引用取值即清：
  `ldloca`/`ldarga` + `Cleat.Scheme.Locals.Take(ref object)`（读出并置 null，AggressiveInlining）。
  deftype 方法的形参有类型，不能按 `ref object` 取地址，退回读后 `starg null`（`clear-args-by-ref`）。
- `if` 分支感知：两支各自数引用，少的一支在入口补清；循环体引用的外层变量钉住不清（`loop-outer-refs`）。
- 转发方法（`invoke` → `invokeStatic`）、桥方法的实参同样经 Take 传出；静态调用、`jump`、变长打包的临时变量用完即清。
- reify* 的构造参数是方法闭包捕获的外层变量（`deftype-caps`），计数要算它们——第一次全量测试 aot.sps
  `(let* [k (:k {:k v})] (reify* … [k v]))` 的 v 被提前清成 nil、cts add-watch 两处断言失败都是这个。

**tier-0 的问题与 `AggressiveOptimization`**：只清 IL 层的局部变量不够。tier-0 JIT 把所有局部与求值栈溢出的临时槽在整个方法内都报告为活的
（不做活跃性分析），`ldloc; ldnull; stloc` 本身还会生成新的溢出槽。用 `DOTNET_DbgEnableMiniDump` + FailFast 取堆转储看根：
钉住序列头的是转发 `invoke` 的 tier-0 帧与 C# `Var.invoke` 的帧。所以：
- 所有 Clojure fn 的方法（子句方法、invokeStatic(Prim)、桥、转发 `invoke`）标 `optimize` → `MethodImplAttributes.AggressiveOptimization`
  （Lcil 方法属性；Emitter 设置实现标志）；`Var.invoke` / `applyTo` 的 7 个重载同样标注。
- 代价：这些方法跳过分层编译，直接以完全优化 JIT，但**没有动态 PGO**（tier-0 插桩 → tier-1 的接口 / 委托去虚化、热冷布局）。
- 场景（20 个保留头用例：N = 3e7、512 MB 堆上限 `DOTNET_GCHeapHardLimit`）：let / 参数 / 转发 / doseq / dorun / reduce / internal-reduce / 嵌套 fn 全部跑完。

调试开关：`CLEAT_NO_INLINE` 环境变量关闭第 4 项的内联（对比剖析用）。

| 基准（min，ms） | 第 5 项前 | 第一次测 | 修正后再测 |
|---|---|---|---|
| sum_loop | 11 | — | 12 |
| protocol | 32 | 34 | 34 |
| keyword_invoke | 21 | — | 18 |
| lazy_chain | 80 | 91 | 79 |
| assoc_map | 50 | 56 | 51 |
| reduce_vec | 18 | 22 | 20 |
| fib | 10 | — | 9 |
| prim_fib | 4 | — | 4 |

启动（`-e "(+ 1 2)"`）：第 5 项前 ~1.13 s，第一次测 ~1.24 s，再测 1.15–1.19 s（clojure.core 的方法都改为直接完全优化 JIT）。
第一次测与再测之间 codegen 只改了 reify 计数，差别主要是机器噪声；稳定的代价是 protocol / reduce_vec 的 ~5–10% 与启动的数十毫秒。
收窄方案（只给转发 `invoke` 与 `Var.invoke` 标 `optimize`，子句方法留在分层编译）未做：子句方法自身的 tier-0 帧同样会钉住参数，
需要逐例验证；需要时再做。

符合性：cts 与基线一致、语料与分歧表一致、ILVerify 通过、591 测试全绿。

## 结论（2026-10-12）

第 1 ~ 5 项完成，第 6 项（LLVM）随 LLVM 后端整体搁置，任务关闭。未纳入本任务、留待以后的：
REPL / 源码加载的 defn 的内联体、局部变量按初值类型推断、`optimize` 收窄到转发 `invoke` 与 `Var.invoke`。
