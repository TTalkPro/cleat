# cleat-121 运行时快路径的分配与内联预算

- 里程碑：M12　依赖：cleat-119　状态：进行中（第一轮测量）
- 设计依据：[22 §2 D5 ~ D7](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 复核 cleat-118 之后的类型化调用：实参 `long` / `double` 不装箱，返回值用 Num 或原始局部承接，`if` / `let` / loop 回边汇合不退回 object。
2. 剖析 `RhpNewFast`、`__tls_get_addr`、Numbers 慢分派与 `IFn.invoke` 仍占高的项，找出快路径上的实参数组、缓存 key、委托与包装分配。
3. 保持快路径小而可内联、慢路径不内联；大方法拆冷路径，避免 JIT 内联预算耗尽。
4. 量化 LongBox 缓存扩大与否；无明确分配收益与全量收益时不改默认范围。

## 第一轮测量（2026-10-16，本机）
- **分配本身的代价**（C# 微基准）：24 字节对象约 5 ~ 6 ns / 次，装箱一个 double 相近；JVM 的 TLAB 分配约 1 ~ 2 ns。.NET 10 在 Linux 上的
  分配快速路径 `RhpNewFast` 每次经 `__tls_get_addr` 取线程的分配上下文；JIT 不内联分配。cpstak 里两者合计约 28%（CPS 每步必然分配的闭包）。
- **`RetObject` 的 `is not ValueType`**（ray、peval 里的 `IsInstanceOfClass` 约 1.8% / 5.5%）：试过缓存上次见到的引用类型方法表，
  多态时更慢（409 → 535 ms / 1 亿次），放弃。
- **`Locals.Take` 未内联**（browse 8.5%、peval 5.3%）：大方法里 JIT 的内联预算用尽，只出现在分层方法（tier-0 需要它）；
  `DOTNET_JitInlineBudget` 在正式版运行时不起作用。没有不破坏 tier-0 防护的替代写法，暂不动。
- **接口转换**（peval 里 mcar / mcdr 的 `ChkCastInterface` 约 3%）：用户代码经接口 `IMPair` 调用，C# 复现里接口调用本身不慢（见 cleat-116），不动。
- 结论：剩下的大头是分配次数（GC 与分配快速路径），通用的减法在标量替换（cleat-123）与按类型特化（M13）；本项保留，等这两项之后再看。

## 验收标准
- browse、cpstak、peval、destruc 前后耗时与分配采样；明确哪些是调用开销优化、哪些实际降低 GC。
- double 装箱继续避免回归到内联 `box`。
- 语义开关开 / 关输出相同；基线、门禁、全量不变。
