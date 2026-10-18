# 26 运行时签名表与高元数特化

> 状态：**已采纳，保守首版**（2026-10-18）。本文是 [24](24-runtime-signature-specialization.md)
> 和 [25](25-runtime-reference-signature-specialization.md) 的后续。
> cleat-134 已支持精确引用类型；cleat-135 把运行时签名候选扩展到 20 参，并把每函数单签名
> `RuntimeSigFn` 升级为可增长的精确签名表。

## §1 结论

1. `RuntimeSigEntry` 的静态协议不变：仍是 `(RuntimeSigEntry n generic-body)`，
   函数类字段仍是 `__rtSpec` / `__rtCount`。
2. `RuntimeSigFn` 变成一个小型不可变条目表。热路径先线性比较 `Type[]`，不分配；
   miss 时抛 `TypedBailout`，由入口 catch 后执行通用体。
3. 带类名的 `RuntimeSigFn` 在同一签名连续 miss 达到阈值后，调用 `RuntimeSigSpec.Promote`
   补生成新签名；成功条目复制替换表数组，原子发布。
4. 每函数最多 8 个签名；进程最多生成 4096 个运行时特化程序集。达到上限后既有特化继续用，
   新签名只回通用体。
5. 运行时生成的程序集本身就是 `AssemblyBuilderAccess.RunAndCollect`；但只要 `__rtSpec`
   引用特化 IFn，就不会回收。主动逐出 / 批次 `Unload` 留给 cleat-136。

## §2 候选与参数上限

1. 固定子句、单 Var、非变参、非 async、无原始签名提示的形状不变。
2. 运行时候选上限从 4 参放宽到 20 参，与 `invokeStatic` 的常驻重载上限一致。
3. 9～20 参的 `RuntimeSigFn` 调用经 `InvokeArray`，本次调用会分配一个实参数组；
   1～8 参保持直接重载，热路径无数组分配。
4. 活动剖面运行时重载仍只支持 4 参，所以 5+ 参候选不生成 `ProfileArgs` 字段；
   这是避免扩大 cleat-128 协议的保守决定。

## §3 语义与失败路径

1. 精确类型完全一致才命中；`null`、派生类型、值类型和不同 arity 一律回通用体。
2. 补生成失败、预算耗尽或每函数表满时暂停该包装器后续补生成，当前调用回通用体。
3. 已有条目不受失败影响；`with-redefs` 后新函数对象没有旧 `__rtSpec`，语义不变。
4. `TypedBailout` 仍由入口 catch 后执行通用体；纯度检查继续排除可能重复副作用的调用。
5. `CLEAT_NO_OPT=sigspec` 继续关闭整条运行时签名特化路径。

## §4 验收

1. 6 参 `Int64` 函数生成 `invokeStaticTyped`，递归自调用无对象边界。
2. 9 参函数经 `RuntimeSigFn.InvokeArray` 正确执行。
3. `Int32[]` 首签名后，`Int64[]` 连续触发补生成并进入同一表；回到 `Int32[]` 仍命中。
4. 默认、`CLEAT_NO_OPT=sigspec`、`CLEAT_NO_OPT=typed` 输出逐字相同。
5. Release 构建、SignatureSpecialization、Core 全量与种子不动点通过。

## §5 后续 cleat-136

1. 给签名表加入频率 / 时间戳和逐出标记。
2. 批次装载生成的特化程序集；整批失效后清除引用并 `AssemblyLoadContext.Unload`。
3. 用 GC 引用到达性测试确认已逐出程序集可回收。
4. 补多态站点压力测试，量化表扫描和 miss 后通用体成本。
