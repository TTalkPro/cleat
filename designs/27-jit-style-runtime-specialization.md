# 27 JIT 式运行时重新编译：单一签名 key + 任意函数体

> 状态：**已采纳**（2026-10-19，M15）。取代 [24](24-runtime-signature-specialization.md) ～ [26](26-runtime-signature-tables.md)
> 中"编译期预生成 LL / DD"与"只特化纯函数体"两条限制。用户决定：删除 cleat-132 的 LL / DD 路线；
> 运行时仿照 JIT，一旦观察到参数是 `long` / `double` / `int` / `long[]` / `int[]` / `double[]` 等确定类型，
> 就按这些类型重新生成 CIL，并用每个参数类型组合成的**单一签名 key** 查表分发。
> 同日补充的要求：特化是对含原生类型形参的函数**整体**做类型推导后生成，不限于 LL / DD；
> 特化代码以默认的 `object` 版本兜底；入口对实参签名做快速 hash、查表找出最优路径；
> 进一步强化编译期类型推导——建立函数调用路径，记录各调用点传入 / 传出的类型，
> 能确认的快速路径在编译时就生成（§5）。

## §0 现状与问题

2026-10-19 在 vm-david 上对 a837e9f 实测：r7rs 56 项里运行时签名特化**一次都没有触发**
（`CLEAT_DUMP_RT_LCIL` 探针，sigspec 语义用例对照能触发）。原因是候选条件过窄：

1. 体必须"纯"（只有算术、自调用与 `aget` / `alength` / `count`），调用任何其他函数就出局；
2. 返回类型必须能证明是 `long` / `double`，因为特化体放弃时抛 `TypedBailout`，由通用体**重算**
   ——这正是纯度要求的来源，重算不能重复副作用；
3. 纯数值函数多数已在编译期被 cleat-104（类型化入口）或 cleat-132（LL / DD 预生成）接管。

另有两处协议缺陷：入口用 `null` 结果表示"未命中"，特化函数合法地返回 `nil` 时会再跑一遍通用体；
每个候选的 `invokeStatic` 都包着 `TypedBailout` 的 try / catch。

## §1 结论

1. **删除 cleat-132 LL / DD 预生成**（`SigEntry`、`ssKind`、`spec-signature-entries`）。
   cleat-104 的编译期类型化入口保留（它有编译期证据，不是猜签名的预生成）。
2. **单一签名 key**：每个形参按观察到的精确 CLR 类型取 4 位类型码，组合成一个 `long`。
   表项按 key 比较，热路径无分配、无 `Type[]` 逐项比较。
3. **两种特化体**（都以原 `object` 通用体兜底）：
   - **O 模式（新，默认）**：任意函数体，形参按类型码固定为原始局部或 `Tagged` 引用，返回 `object`；
     体内**没有放弃点**，因此不要求纯，任何副作用都只执行一次。
   - **R 模式（保留 cleat-133 路径）**：纯体且能证明返回 `long` / `double` 时，返回原始值，
     自调用不装箱；放弃仍抛 `TypedBailout`，由签名表转成"未命中"后执行通用体（纯，重算安全）。
4. 自调用按**身份守卫**直调：Var 当前值仍是本函数对象时 `call` 特化方法，否则经 `IFn.invoke`；
   O 模式的回退结果本来就是 `object`，不需要放弃。
5. 未命中用哨兵对象 `RuntimeSigTable.Miss` 表示；通用入口不再有 try / catch。

## §2 签名 key

### 2.1 类型码（4 位）

| 码 | 类型 | 特化体里的形参 |
|---:|---|---|
| 0 | 其他 / `null` / 未参与 key 的形参 | `object`，不守卫 |
| 1 | `Int64` | 原始 `long`（`:l64`） |
| 2 | `Double` | 原始 `double`（`:fl`） |
| 3 | `Int32` | 首版 `object` + key 区分（见 §6） |
| 4 | `Int64[]` | `Tagged Int64[]` |
| 5 | `Int32[]` | `Tagged Int32[]` |
| 6 | `Double[]` | `Tagged Double[]` |
| 7 | `String` | `Tagged String` |
| 8 | `Byte[]` | `Tagged Byte[]` |
| 9 | `Char[]` | `Tagged Char[]` |
| 10 | `Single[]` | `Tagged Single[]` |
| 11 | `Int16[]` | `Tagged Int16[]` |
| 12 | `Boolean[]` | `Tagged Boolean[]` |
| 13 | `Object[]` | `Tagged Object[]` |
| 14、15 | 保留 | — |

判定用 `x.GetType() == typeof(T)`（JIT 折叠成方法表比较），不用 `is`：CLR 数组协变会让
`long[]` 通过 `is ulong[]`。码 0 的形参不参与守卫，签名因此更宽、命中率更高。

### 2.2 key 与掩码

`key = Σ code(argᵢ) << 4i`，最多 16 个形参（64 位）。编译期为每个候选算一个**形参掩码**：
只有在体内作为宿主 / 原语调用（`clj-call`：`Numbers`、`RT.aget`、互操作等）的操作数出现过的形参
（含经 arity 标签复制后的副本）才进 key；只被转交给别的函数、存进集合或原样返回的形参恒为 0，
避免"同一函数、不同的无关类型"分裂出多个特化。

key 全 0 表示没有值得特化的类型：首次达到阈值时 key 为 0，入口永久回到通用体（JIT 的"不升层"）。

### 2.3 签名表 `RuntimeSigTable`

- 条目：`(long key, IFn fn, bool guarded, WeakReference assembly, hits, lastUse)` 的不可变数组；
  `Invoke(a1 … an)` 按掩码算 key，线性比较（每函数 ≤ 8 条），命中直调 `fn.invoke`。
- R 模式条目（`guarded`）在 try 里调用，`TypedBailout` → 返回 `Miss`。
- **最优路径**：先找 key 完全相同的条目；没有时找"兼容"条目——条目在某槽位的码是 0（object 通配）
  或与实参码相同，且所有非 0 槽位都相同；多个兼容条目取非 0 槽位最多的（最具体的）。
  选中结果按实参 key 缓存在表里（一条 key → 条目的小映射），下次同一 key 一次比较即可。
  都不兼容才回到 object 通用体。生成特化时同时生成"只固定部分形参"的版本没有意义，
  兼容匹配只服务于已有条目（例如掩码外的形参、或某形参在部分调用里是 `null`）。
- 未命中：周期计数达到阈值后按当前实参请编译器补生成；生成失败的 key 记入负表，不再重试。
- 每函数 8 条、进程 4096 个生成程序集、LRU 逐出空闲 1 秒以上的条目（沿用 cleat-135 / 136）。

## §3 生成代码

### 3.1 通用入口（函数类 C 的 `invokeStatic`）

```
ldsfld C::__rtSpec ; brfalse cold
ldsfld C::__rtSpec ; castclass RuntimeSigTable ; ldarg 1 … n
callvirt RuntimeSigTable::Invoke(object × n) ; stloc r
ldloc r ; ldsfld RuntimeSigTable::Miss ; beq generic
ldloc r ; ret
cold:   __rtCount++ ; < 阈值 → generic
        RuntimeSigSpec.Install(类名, 掩码, 实参[], this) → 表或 null
        null → __rtCount = int.MinValue（永久通用）；否则写 __rtSpec 后回到热路径
generic: 原通用体
```

### 3.2 特化类（`rtSpec$N`，独立的 collectible 内存程序集）

- `invokeStaticTyped(self, T₁ … Tₙ) → R`：R 模式 `R` 是 `long` / `double`，O 模式是 `object`。
- `invoke(object × n)`：按签名 `unbox.any` / `castclass` 后直调 `invokeStaticTyped`，R 模式装箱返回。
  key 已保证精确类型，这里不再 `isinst`。
- 自调用 `(clj-call "TypedSelf" …)`：守卫从"`isinst` 本类"改成"Var 当前值 `==` 通用函数对象"
  （通用函数对象作为 live 常量嵌入）；命中 `call invokeStaticTyped`，否则 `IFn.invoke` 装箱调用，
  R 模式按签名取回（不符抛 `TypedBailout`），O 模式直接得到 `object`。
- 实参类型与签名不符的自调用（例如 long 签名里以 double 递归）保留成普通调用，经 C 的入口查表。

### 3.3 候选

单 Var 的 `(def f (fn [形参…] 体))`：单固定子句、1～16 参、无闭包字段（也就没有自名局部）、
非 async、无原始签名提示、体不超过 4000 个节点、至少一个形参进掩码。**不再要求纯**。
cleat-104 已经证明类型化入口的函数不进运行时候选。

## §4 语义

| 情形 | 行为 |
|---|---|
| key 不在表中 / 表未建 | 通用体（与关闭优化相同） |
| O 模式体 | 只执行一次；没有放弃点，副作用不重复 |
| R 模式放弃 | `TypedBailout` → `Miss` → 通用体重算；R 模式只给纯体 |
| 特化函数返回 `nil` / `false` | 正常返回（哨兵不是 `null`） |
| `with-redefs` / `binding` / 重定义 | 自调用守卫失败 → 经 Var 调用新值；重定义得到新类，旧表不可达 |
| 长整数溢出、异常 | 与通用体相同的运算语义（同一份 L3，只是形参类型已知） |
| 生成异常 / 预算耗尽 | 返回 null，该 key 记负表；入口继续通用 |
| 编译期间重入 | `generate-unit` 进行中时 `Install` / 补生成直接返回 null |

阈值缺省 8 次通用调用（cleat-139：要赶在通用体分层升级前请求后台编译，晚切会让热循环重新预热；
生成在专用后台线程上，不占调用线程）；`CLEAT_RTSPEC_THRESHOLD=<n>` 覆盖，
`CLEAT_RTSPEC_THRESHOLD=1` 是验证用的**压力模式**：几乎所有候选在第一次调用就重新编译。
`CLEAT_NO_OPT=sigspec` 关闭整条路径。镜像 / 包构建时 `RuntimeSigSpec.Enabled = false` 不变。

## §5 编译期调用路径类型推导（cleat-140）

运行时特化解决"编译期不知道"的情况；能在编译期确认的快速路径应当直接生成，不必等到运行时。

1. **调用图与摘要**：每个编译单元（及已加载、登记了 L3 的函数）建立调用图：顶层 `def` 的函数是节点，
   经 Var 的调用是边。每条边记录调用点的实参推断类型；每个函数记录在给定形参类型下推断出的返回类型。
2. **签名的挑选**：一个函数各已知调用点的实参类型给出若干候选签名。Var 总能被外部（REPL、
   其他命名空间、高阶传递）以任意类型调用，所以调用点类型只用来挑选**要预生成的签名**，
   从不改变 object 通用体，也不省略入口的签名守卫。
3. **预生成与直连**：调用点的实参类型在编译期已确定为原生类型（`long` / `double` / 原生数组 / `String`）、
   且目标函数是运行时特化候选时，编译期就生成该签名的特化方法（与 §3.2 同形，返回 `object` 或可证明的
   原始类型），并登记进目标函数的签名表；调用点生成"Var 当前值 == 定义时的函数对象"守卫后直接 `call`
   特化方法，守卫失败走普通 `IFn.invoke`。
4. **返回类型传播**：特化方法能证明原始返回类型时，调用点直接拿原始值，继续参与调用方的类型推断；
   不动点迭代到摘要不再变化（节点数和迭代次数都有上限）。
5. 与运行时路径共用同一张签名表和同一套 key：编译期生成的条目就是"预先填好的表项"，运行时仍可补生成。

## §6 验证

1. 语义差分：默认 / `CLEAT_NO_OPT=sigspec` / 压力模式三种设置下 `sigspec_semantics.clj` 与新增
   的 `rtspec_jit_semantics.clj`（副作用只执行一次、返回 nil、异常、闭包捕获原始形参、`with-redefs`、
   `binding`、多签名、key 为 0 的函数、数组 / 字符串形参）输出逐字相同。
2. 压力模式下跑语料门禁、cts、test-clojure，基线不变——这是 O 模式"任意体"正确性的主要证据。
3. r7rs 逐项统计触发数（探针）并三方重跑；关注 fib / tak / ack（cleat-132 删除后不得回退）。
4. 种子按最终 codegen 重生成并验证不动点；随后删除只为旧种子保留的 `RuntimeSigFn` 兼容入口。

## §7 限制与后续

1. `Int32` 首版只区分 key，不做原始 `int` 形参：Clojure 对 `int` 的算术提升规则与 `long` 不同，
   需要单独的类型推断用例后再开放。
2. 多 arity / 变参 / 闭包函数不是候选；循环中的长时间单次调用不触发（没有 OSR）——
   循环内的类型由 cleat-103 / 127 的编译期推测负责。
3. 运行时生成的特化只对自调用直连；其他调用方仍经 C 的入口查表（一次虚调用 + key 计算）。
   编译期能确认类型的调用点由 §5（cleat-140）直连；运行时修补已生成调用点不做。

## §8 任务

| 任务 | 内容 | 依赖 |
|---|---|---|
| cleat-137 | 删除 cleat-132 LL / DD 预生成；≤ 3 参候选改走运行时路径 | cleat-136 |
| cleat-138 | 单一签名 key 与 `RuntimeSigTable`；哨兵未命中、入口去 try / catch、形参掩码 | cleat-137 |
| cleat-139 | O 模式：任意函数体重新生成、身份守卫的自调用、阈值与压力模式 | cleat-138 |
| cleat-140 | 编译期调用路径类型推导：调用图、实参 / 返回类型摘要、确认的快速路径编译期生成并直连 | cleat-139 |
| cleat-141 | M15 收口：压力模式跑全量基线、种子重生成、r7rs 三方重跑 | cleat-140 |
