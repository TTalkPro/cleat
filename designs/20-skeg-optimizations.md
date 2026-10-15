# 20 M10 性能：从 skeg 借鉴的优化与 cleat 自己的差距

> 状态：**现行方向**（2026-10-15 起）。来源：skeg（~/workspace/skeg，同一作者的 Scheme 实现）M10 ～ M12 的性能工作 iron-060 ～ 087，
> 与 cleat 在 cleat-042 / cleat-099 之后的实测。任务 cleat-100 ～ 106。
> 只读 skeg 的 git 历史与源码，不改它的工作树。

## §0 事实（2026-10-15 实测，Release，本机）

5M 次迭代的循环：

| 场景 | 耗时 | 现状 |
|---|---|---|
| 有提示的 long / double 循环 | 30 / 23 ms | 已拆箱（cleat-042） |
| 循环变量初值来自无提示参数（long / double） | 17 / 42 ms | double 每轮装箱 |
| 嵌套算术，参数有 `^double` | 34 ms | |
| **嵌套算术，参数无提示** `(+ s (* (- a b) (+ a b)))` | **354 ms** | 每轮 3 次装箱 + Numbers 分派 |

2M 次：

| 场景 | 耗时 | 现状 |
|---|---|---|
| `(P. i i)` deftype 构造 | 160 ms（80 ns/次） | `NewOf` → `Reflector.New`，运行时反射 |
| `(->R i i)` record 工厂 | 227 ms | 同上 |
| `(System.Text.StringBuilder. 16)` | 146 ms | 同上 |
| `(.Length ^String s)` 属性读 | 156 ms（78 ns/次） | `ClojureStaticCalls.Resolve` 不认属性，走 Reflector |
| `(.-a p)` 字段读 | 46 ms | 静态决议 |
| `(conj v i)` 持久向量 | 329 ms（165 ns/次） | 另一个方向，不在本文 |

`bench/baseline.edn`（M10 起点，cleat-100，3 轮 min）：assoc_map 58、ctor 305、fib 9、keyword_invoke 21、lazy_chain 84、mbrot 52、nested_arith 311、
prim_fib 4、property 155、protocol 34、reduce_vec 36、sum_loop 13、untyped_loop 49 ms。分配采样（`tools/alloc.cs`）：nested_arith 的分配 94% 是
`System.Double` 装箱（5 轮 2.3 GB，gen0 GC 204 次）；ctor 的大头是 `Reflector.New` 每次新建的 `CallKey`（1.0 GB）、`System.Type[]`（0.55 GB）与实参数组。

cleat 已有的（cleat-042 与 M8 从 Scheme 层带入）：类型格与收窄（iron-071 的格）、`infer-var-types-into!`（iron-034c 工作表法）、
`:l64` / `:fl` 的原始发射（`cg-raw`）、`^long` / `^double` 原始签名（JVM 的 `IFn$LL`）、宿主调用静态决议（`ClojureStaticCalls.Resolve`）、
`.inl` 小函数内联（iron-070 的子集）、cp0-lite、局部变量清除、letrec 直接调用、`loop/recur` → `label/jump`（lower-loops D9）。

## §1 skeg 任务逐条对照

| skeg | 内容 | cleat | 说明 |
|---|---|---|---|
| iron-080 Num | 16 字节值类型装"是数但不知哪种"的局部与循环变量 | **借** | §3；语义按 Clojure 改 |
| iron-082 嵌套算术双局部 | 内层算术结果放 `(double d, object o)` 一对局部，纯 IL | **借** | Clojure 多参数 `+` 的 `:inline` 已是嵌套，一并覆盖 083(a) |
| iron-083 let 变量双表示 | let 右边是算术、只作算术操作数的变量用双局部 | **借** | |
| iron-071 类型格 + 循环推测 | 快慢两份循环，入口检查后走原始版 | **借推测部分** | 格已有，快慢循环没有 |
| iron-075 / 076 类型化入口 | 无提示的已知过程按签名推测生成 `$t`，调用点直调 | **借** | Clojure 溢出抛异常，退回协议比 skeg 简单（§5） |
| iron-085 封闭局部过程提升、闭包直接 Invoke | | 量化后定 | cleat 已有 letrec 直接调用 |
| iron-086 记录构造器 | | 被 §2 覆盖 | cleat 所有 `(new T …)` 都走反射 |
| iron-070 / 073 cp0 内联、simplify-if | | 已有子集 | 预算放宽量化后定 |
| iron-068 / 084 / 081 逃逸续延 → 跳转、尾逃逸、异常复用 | call/cc | **不适用** | Clojure 没有 call/cc：`reduced`、循环不 recur 都是值流；`try` 正常路径不抛 |
| iron-087 Invoke5 ～ 8 | | 不需要 | `IFn` 有 20 个 arity |
| iron-061 ～ 064 / 067 / 077 / 078 / 065 / 074 | Scheme 数值过程、字符端口、R7RS、处理器栈 | 不适用 | `clojure.lang.Numbers` 已有 long / double 重载 |
| iron-069 / 072 | 原语元数据、Chez mats | 已有 / 不适用 | |

**直接搬的代码**（C#）：`Skeg.Core/Numbers/Num.cs`（262 行，改语义后成 `Cleat.Core.Runtime.Num`）、`InlineArith.Result` 与 `Calls.Proc` 的
"快路径 AggressiveInlining + 慢路径 NoInlining 拆开"写法、`tools/alloc.cs`（分配采样器，104 行，改成跑 cleat 宿主）。
Scheme 写的 codegen（Num / 双局部约 300 行，类型化入口约 250 行，循环推测约 150 行）翻成 Clojure：cleat 的 codegen 与它出自同一份原版，
`infer-var-types-into!`、`cg-raw`、`expr-type`、`label` 的生成都在原位。

**预分配帧**：skeg 没有这个名字的任务。最接近的是 async-lambda 的 `AsyncFrame`（局部变量成帧对象的字段；cleat 用 .NET 运行时 async，不生成帧类）
与 iron-068 的 `escape-frame`。**待用户确认指的是哪一个**；确认前不列任务。

## §2 静态决议范围（cleat-101）

cleat-099 的结论记下了 `ClojureStaticCalls.Resolve` 相对 ClojureCLR 编译期决议的差距，它们都是 codegen 自己的限制，加提示解决不了：

1. **构造器**：`(new T a …)` 编成 `clj-call "NewOf"`，codegen 没有静态决议，一律 `Reflector.New`。做法与方法相同：按实参静态类型选唯一相容的公共构造器，
   `newobj`；选不出时照旧。deftype / defrecord 自己生成的类也受益（`->R`、`(P. …)`）。
2. **属性**：零实参的实例 / 静态成员找不到方法与字段时查属性，`call get_X`。索引器不做。
3. **值类型目标**：`(.ToString ^long n)` 一类：实参原始求值后 `box` 再 `call`（或地址 + `call`）；第一版只做已拆箱的 `:l64` / `:fl` 目标。
4. **非 core 的引用类型形参**：任何公共、可按名字引用的类型都 `castclass`（JVM 的 checkcast）；委托形参仍交给 Reflector（IFn → 委托转换）。
5. **泛型方法**：实参类型都已知且能唯一推断类型实参时 `call` 实例化后的方法；否则照旧。

开关 `CLEAT_NO_OPT=host` 关掉全部宿主调用的编译期决议（语义对照用）；语料门禁与两套基线不许变化（这些都是行为不变的优化）。

**cleat-101 结果（2026-10-15）**：1、2、4 完成，另加桩类型的构造器（defrecord 工厂）与 Int32 返回值按 l64；3（值类型目标）、5（泛型方法）等有基准证据再做。ctor 305 → 80 ms，property 155 → 20 ms。

**cleat-106（2026-10-15）**：§2 的延续——引用类型提示经 `Tagged` 标注带到决议器、一维数组形参可决议、类型化 `aget`、丢弃上下文不装箱。array1 的 long-array 版 18.6 s → 0.41 s（linux01，50 轮）。

## §3 Num 与双局部变量（cleat-102）

skeg 的设计：`readonly struct Num { long Bits; object Tag; }`——`Tag == null` 精确整数、`Tag == FlTag` double 的位、其余是装箱对象本身。
任何值都能放进去，**推断错了只是慢，不会错**，不需要去优化。只活在局部变量与求值栈，存进数据结构或传给未知函数时装箱。

skeg 的三条教训：
1. 过程级 Num 签名是负收益（compiler 基准慢 17%，skeg 已删）——不做。
2. 嵌套表达式不走 Num：大方法里 JIT 内联预算用完后 Num 方法变成真调用、16 字节按值传（simplex 慢 10%）。嵌套算术用纯 IL 的双局部（082）；
   Num 只给循环变量与 let 变量。
3. `FlTag` 用显式静态构造函数、镜像加载时先初始化，JIT 才把它当常量提到循环外。

Clojure 语义与 skeg 的差别：
- 精确整数：Clojure 的 `+ - *` 溢出**抛 ArithmeticException**（`+'` 才升 BigInt）。快路径 64 位有溢出检查，溢出走慢路径
  `Numbers.add(object, object)`，结果与异常完全相同；`*unchecked-math*` 为真时用 `unchecked_add` 系。
- `=`（`Util.equiv`）：`(= 1 1.0)` 是 false，不能像 skeg 的 `NumEqual` 按 double 比；`< <= > >=` 与 `==` 才做数值比较。
- long + double → double，与 Numbers 的传染一致。
- 装箱：long 走现有的装箱缓存（`Box`）；Tag 的"其余对象"覆盖 BigInt / Ratio / BigDecimal / 非数。
- 慢路径的"原过程"就是 `Numbers` 的静态方法，不需要像 skeg 那样把 boot 过程当参数传。

编译器：
- 推断：`infer-var-types-into!` 里定不成 `:l64` / `:fl` 的候选，有"不精确迹象"（double 常量、Numbers 的 double 重载结果、`:num` 变量、
  对它们的 `+ - * /`）时记 `:num`；只增不减的不动点；原始用途不少于装箱用途。没有不精确迹象的整数变量留给 cleat-103 的 long 推测。
- 生成：`cg-raw` 加 `:num`（常量 `FromInt64` / `FromDouble`，变量 `ldloc`，算术递归，其余 `FromObject`）；`numbers-arith-op` 有 `:num` 操作数时走
  `Num.Add` 等；`label` 形参可以是 Num。
- 嵌套算术（082）：操作数本身是类型未定的 Numbers 算术时写进 `(double, object)` 双局部；外层 double 路径看 `o == null`，否则照常。
  限制同 skeg：`quotient` / `remainder` 外层不用，内层是 `:num` 变量运算时仍走 Num。
- let 双表示（083b）：右边是双操作数算术、变量在体里只作内联算术 / 比较的操作数（不被 fn 捕获、不 set!、不作别的值）时就是那对局部。
- 开关 `CLEAT_NO_OPT` 含 `num` / `dual`。

**cleat-102 结果（2026-10-15）**：Num 完成（形参入口转换 + 副本别名 + let / label 变量 + 有类型子表达式包装），nested_arith 311 → 22 ms，untyped_loop 49 → 34；双局部（082 / 083）不做——cleat 不内联带类型检查的通用算术，Num 已经承担那对局部的角色。两处返工都是每轮多一次分配（形参副本的 Num.Box、double 子表达式的 box + FromObject），分配采样抓出。

验收：§0 的"嵌套算术无提示"降到 50 ms 以内；Num 语义用例（溢出、`+'`、混合、Ratio、BigInt、NaN、`=` 与 `==`、非数报错、unchecked）
在开、关两种设置下输出逐字相同；分配采样器确认热循环零分配。

## §4 循环推测（cleat-103）

skeg iron-071 + 080B：最内层 `label` 循环里类型未定的形参，回边实参在"这些形参都是 long（或 double）"的前提下一定是 long（double）时，
生成快慢两份循环：入口 `isinst Int64` / `isinst Double`（或 Num 标签检查）成立进快的一份（原始局部、回边不检查），否则进慢的一份
（object 或 Num）。Clojure 上 long 的快份不需要 skeg 的 int32 哨兵：溢出直接抛，与慢份一致。

针对：`(defn f [n x] (loop [i 0 s x] …))`——初值来自无提示参数的循环。验收：§0 的 param-double 降到与 typed-double 同级；
两份循环的 IL 快照；语义用例（入口是 long / double / Ratio / BigInt / 非数、NaN、两个形参一起推测）。

**cleat-103 结果（2026-10-15）**：long 推测完成（第一遍推断挑候选、试算推断确认、改写后重推）；double 项 cleat-102 的 Num 已与有提示的同级，Num 变量不再推测。
sum-long 13 → 2 ~ 3 ms（有提示 1 ms，余下是体外 Num 变量 `n` 的比较），untyped_loop 22 → 11 ms（linux01）；boot 镜像 8 处循环被改写。

## §5 无提示已知函数的类型化入口（cleat-104）

skeg iron-075 / 076：单元内已知过程（cleat：直接链接的顶层 `defn`，单固定子句，≤ 4 形参，体不大），每个形参与返回值独立推测
`{long, double, obj}`，组内乐观不动点；生成 `invokeStaticPrim`-形的 `$t` 方法，通用入口在实参恰好匹配时转调它，类型已知的调用点直调。
cleat 已有 `^long` / `^double` 的 `invokeStaticPrim` 与 `PrimInvoke` 调用点，这里是把"提示"换成"推测"：
- long 路径：Clojure 溢出抛异常，`$t` 里照常 `Numbers.add(long,long)`，不需要 skeg 的哨兵、`$g` 重算与溢出计数——退回协议只剩"实参不是 long 时走通用入口"。
- double 路径：同 skeg，无退回。
- 不做：过程级 Num 签名（skeg 已证明负收益）。
验收：`bench/fib`（无提示）接近 `prim_fib`；体有副作用 / 分配 / 组外调用的函数不特化（句法检查）。

**cleat-104 结果（2026-10-15）**：类型化入口完成（递归的纯数值 defn，签名 long / double 推测，通用入口 isinst 转调，自调用在 Var 未被重定义时直调，
重定义且结果类型不符时 TypedBailout 落回通用体重算）。fib(32) 82 → 13 ms（`^long` 提示 23 ms），fibfp(30.0) 41 → 5 ms（linux01）；boot 镜像不变。

## §6 不借的与待量化的

- 068 / 084 / 081：Clojure 没有 call/cc；`throw` 被同一方法的 `catch` 接住编成跳转这种事 JVM 也不做，实际代码罕见。
- 087：`IFn` 有 20 个 arity。
- 085（封闭局部 fn 提升、闭包直接 `Invoke`）与 070 预算放宽：cleat-100 的测量之后再决定（cleat-105）。

## §7 任务与顺序

cleat-100（测量基建）→ 101（静态决议范围）→ 102（Num + 双局部）→ 106（数组访问的静态决议，array1）→ 103（循环推测）→ 104（类型化入口）→ 105（量化后定）。
每个任务：Release 构建、`bench`（新增基准在内）、语义用例开 / 关对照、语料门禁、两套基线、Host / Core 全量；改了 `src/clojure` 的要重生成种子。
