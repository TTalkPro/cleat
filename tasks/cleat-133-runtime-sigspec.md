# cleat-133 运行时签名特化：观察 → 按需生成 → 查表分发

- 里程碑：M14　依赖：cleat-132　状态：完成
- 设计依据：[24 §1 ~ §3](../designs/24-runtime-signature-specialization.md)

## 问题

cleat-132 的编译期预生成 LL / DD 特化只能覆盖 ≤3 参的数值类型（long / double），
对 `String`、`Int32[]`、`PersistentVector` 等引用类型完全无效。且预生成会产生死代码：
实际只调用一种签名，却生成了全部候选。6+ 参的组合爆炸（64 种）使预生成不可扩展。

## 方案

编译期保留候选函数的 L3 IR 与类型分析结果（不做预生成）。运行时 `invoke` 入口
观察实参 CLR 类型；达到阈值后用内存程序集按观察到的精确 `Type[]` 重新编译后段，
生成类型化 IFn。首版 `RuntimeSigFn` 保存精确类型、特化函数与编译时通用函数：
类型一致且 `TypedBailout` 未放弃时直调特化，否则回退通用函数。

## 工作内容

1. 编译期：候选函数的 L3 子句保存到编译器登记表；≥4 参或 ≤3 参预生成失败包成
   `RuntimeSigEntry`，函数类增加 `__rtSpec` / `__rtCount`。
2. 运行时：通用调用计数达到阈值后携带函数类名、实参与通用函数回调编译器；先用
   `spec-entry-try` 证明观察到的数值签名，再 L3 → Lcil → 内存程序集 → 实例化，最后
   包成 `RuntimeSigFn` 写入 `__rtSpec`。
3. 引用类型形参留到 cleat-134：首版生成器只接受 `Int64` / `Double`，其他观察类型
   返回 null 并保留通用函数。
4. 退回：实参类型不符或 `TypedBailout` 由 `RuntimeSigFn` 转调通用函数；Var 重定义后
   新对象没有旧缓存。
5. 开关：`CLEAT_NO_OPT=sigspec` 同时关闭 cleat-132 预生成与本方案的运行时生成。

## 验收

- 首版语义用例覆盖数值混合、4 参递归、首签名后其他类型回退、`with-redefs`；
  `String` / `Int32[]` / `PersistentVector` 的生成进入 cleat-134；
  默认 / `CLEAT_NO_OPT=sigspec` / `CLEAT_NO_OPT=typed` 输出逐字相同。
- 生成的候选函数类包含 `__sigCache` 字段；非候选不变。
- Release 构建通过；Core 全量 + Host 专项通过；种子不动点通过。
- fib35 分配较通用路径显著减少；`array1` / `string` 基准不回退。
