# 24 运行时签名特化：观察 → 按需生成 → 查表分发

> 状态：**历史；运行时路径已由 cleat-170 删除**（2026-10-18 采纳首版）。问题来源：cleat-132 的编译期预生成 LL / DD 特化只能覆盖数值类型（≤3 参），
> 对 `String`、`Int32[]`、`PersistentVector` 等引用类型完全无效，且组合爆炸不可扩展。
> 本文提出运行时按需生成方案：编译期保留 IR 与类型元数据，`invoke` 入口做签名观察与查表，
> 命中新签名时用 `PersistedAssemblyBuilder` 生成类型化 CIL 并装载。

## §0 现状与动机

### 已有机制覆盖范围

| 层次 | 机制 | 覆盖类型 | 参数上限 | 状态 |
|---|---|---|---|---|
| 编译期已知类型 | cleat-104 类型化入口 | long / double（有提示时） | 4 | ✅ 完成 |
| 编译期推测 | cleat-132 签名特化 LL / DD | 仅 long / double | 3 | ✅ 完成（fib 3.8×） |
| 运行时类型反馈重编译 | cleat-129 | 任意 | 4 | ❌ 关闭（复杂度 / 收益不匹配） |
| 数组元素推测 | cleat-127 | 窄类型数组 | — | ✅ 完成 |

### 缺口

1. `String` / `Int32[]` / `PersistentVector` 等引用类型形参没有原始运算路径
2. 4+ 参函数既无法推测（组合爆炸）也无法用 cleat-104（编译期推不出）
3. 预生成浪费：实际只调用一种签名，但生成了 LD / DL 等从未触发的候选

### 解决方案概述

编译期做类型分析但不生成特化代码，只保留函数的 L3 IR。运行时 `invoke` 入口观察实参
的 CLR 类型形成签名 key，签名稳定后用 `PersistedAssemblyBuilder` 以观察到的类型为已知
类型重新编译后段，生成类型化 IFn，装入首版签名表项。后续同签名调用先做精确类型检查；
首版每个函数缓存一个签名，其他签名回退通用函数。

## §1 编译期：类型分析与 IR 保留

### 1.1 候选选择

沿用 cleat-132 的形状约束：无 rest、无闭包逃逸、无 async、无原始签名提示的固定子句，
但**取消参数个数上限**（≥1 即可），把 `spec-pure?` 分析结果存入函数类元数据。

### 1.2 IR 保存

每个候选函数的 L3 子句（`clause` 节点）序列化为 Clojure 数据结构，存在函数类的
静态 readonly 字段 `__sigIR` 中。同时保存：

- 形参符号表（参数名 → L3 绑定名映射）
- 自递归标记（`spec-typed-selves` 分析结果）
- 副作用检查结果（纯 / 有副作用）

序列化格式：复用 eval 内联登记已保存的 L0 / L3 clause 格式（cleat-116），不需新协议。

### 1.3 通用入口生成

`invokeStatic` 方法照常发射通用体（装箱版）。在方法入口增加签名分发指令：

```cil
// invokeStatic(self, arg1, arg2, ...)
ldsfld    class __sigCache   // Dictionary<Type[], Func<…>>
brfalse   generic            // 空表 → 通用体
// 检查实参类型，查表
ldloc.1; callvirt object.GetType(); ldloc.2; callvirt object.GetType(); ...
ldsfld   class __sigCache
ldloc    typeArray
callvirt Dictionary<Type[],object>::get_Item
// 命中 → 直接调 delegate；miss → 通用体 + 类型观察
```

热路径开销：一次字段读取 + `TryGetValue` + `isinst`×N ≈ **10 ~ 20 ns**。

## §2 运行时：签名观察与按需生成

### 2.1 签名观察

函数类增加一个静态字段 `__sigCounts`（`Dictionary<Type[], int>`）。通用路径每次调用
`TryGetValue` 递增对应签名的计数。计数达到阈值（如 16）时触发后台编译。

线程安全：`ConcurrentDictionary.GetOrAdd` + `Interlocked.Increment`。

### 2.2 运行时编译管线

触发后从 `__sigIR` 取出 L3 子句，以观察到的 CLR 类型作为形参已知类型，调用编译器后段：

1. 把 `Type[]` 映射回 L3 类型（`Int64` → `:l64`，`Double` → `:fl`，其余 → object）
2. 调用 `spec-entry-try`（已有函数）验证类型一致性
3. 通过验证 → `spec-typed-copy` 生成 L3 类型化体
4. L3 → Lcil → `Emitter.EmitLoaded`（`PersistedAssemblyBuilder` → PE → `LoadFromStream`）
5. 反射取出 `invokeStaticSpec` 方法 → 创建 delegate
6. 写入 `__sigCache[Type[]] = delegate`（volatile 写）

单个程序集生成耗时 ~53 μs（实测）。首版在触发线程同步执行，避免引入后台队列生命周期；
生成失败只禁用该函数候选。

### 2.4 首版表项与退回

`RuntimeSigFn` 保存精确 `Type[]`、类型化 IFn、编译时的通用 IFn。命中要求实参个数和每个
实参的 `GetType()` 精确一致；不命中直接调用通用函数。类型化体内的 `TypedBailout`（例如
自调用时 Var 改变）也被表项捕获并转调通用函数。通用函数按对象捕获，旧函数对象在
`with-redefs` 后保持旧语义；新函数对象没有旧静态字段。

### 2.3 特化体结构

生成的 `invokeStaticSpec` 是一个 **static 方法**，签名为
`(self, T1, T2, …) → object`，内部：

```cil
// self 类型化的自调用：直接 call self 类的 invokeStaticSpec（或已缓存的重载）
// 纯运算：int64 / float64 / 引用类型的 CLR 指令
// 边界返回：一次 box
ret
```

引用类型形参（如 `Int32[]`）不拆箱，直接用原始类型调用 `ldelem.i4` 等指令，
消除 `aget` / `aset` 的装箱开销（复用 cleat-118 的类型化数组访问）。

### 2.4 退回与失效

| 事件 | 行为 |
|------|------|
| 实参类型不符签名 key | `RuntimeSigFn` 调用捕获的通用函数 |
| 特化体内抛 `TypedBailout` | `RuntimeSigFn` 调用捕获的通用函数 |
| Var 被 `with-redefs` 重定义 | 新函数对象没有旧 `__sigCache`，自动不命中 |
| 特化编译失败（IR 不兼容） | 标记该签名不可特化，不再重试 |
| 特化数量达上限（4096） | 停止新增特化，已有特化继续使用 |

## §3 内存管理

### 3.1 数量与内存估算

典型工作负载：200 个热点函数 × 平均 2 种实际签名 = 400 个特化程序集
× 13 KB ≈ **5.2 MB**。上限 4096 个 ≈ **53 MB**，完全可接受。

### 3.2 可回收 ALC 批次

第一批用不可回收 ALC（复用 `EvalLoadContext`，保证分层编译 + PGO）。
超过阈值（如 2048 个）时切换到可回收 ALC 批次（每批 512 个），装满后 `Unload()`
整批回收，特化表清空该批条目。`CLEAT_NO_OPT=sigspec` 完全关闭整个机制。

## §4 与已有机制的关系

| 机制 | 关系 |
|------|------|
| cleat-104 类型化入口 | 编译期已证明的候选仍优先走 104；132 / 本方案只处理 104 推不出的情况 |
| cleat-132 编译期预生成 | 替代（更泛用：任意类型 + 任意参数个数）；开关 `CLEAT_NO_OPT=sigspec` 同时控制两者 |
| cleat-127 数组推测 | 正交：127 处理循环内数组变量的元素类型，本方案处理函数形参的整体类型 |
| cleat-128 类型反馈剖面 | 被 2.1 的签名观察替代（更轻：只记录 Type[] key，不记录每个槽位的类型） |
| cleat-129 运行时重编译 | 复用其核心思想，但简化：无独立剖面记录、无后台编译线程、直接在触发线程编译 |
| eval 分层编译（cleat-114） | 复用 `EmitLoaded` 路径，生成的特化代码参与 JIT 分层 + PGO |

## §5 验收标准

1. 语义差分：默认 / `CLEAT_NO_OPT=sigspec` / `CLEAT_NO_OPT=typed` 输出逐字相同
2. 特化表命中后的调用不经过通用体；`isinst` 失败回退通用体
3. `with-redefs` 后特化失效，结果仍正确
4. `String` / `Int32[]` / `PersistentVector` 形参的函数有特化生成且分配减少
5. 4+ 参函数有特化生成
6. Release 构建通过；Core + Host 专项通过；种子不动点通过
7. fib35 与 r7rs 关键项（array1 / string / quicksort）性能不回退

## §6 任务

| 任务 | 内容 | 依赖 |
|---|---|---|
| cleat-133 | 编译期 IR 保留 + invoke 入口签名分发 + 运行时数值管线原型 | cleat-132 |
| cleat-134 | 引用类型形参的特化（`Int32[]` / `String` / `PersistentVector`） | cleat-133 |
| cleat-135 | 4+ 参特化与上限 / 可回收 ALC 批次 | cleat-133 |
