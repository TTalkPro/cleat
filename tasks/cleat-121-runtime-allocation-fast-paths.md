# cleat-121 运行时快路径的分配与内联预算

- 里程碑：M12　依赖：cleat-119　状态：未开始
- 设计依据：[22 §2 D5 ~ D7](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 复核 cleat-118 之后的类型化调用：实参 `long` / `double` 不装箱，返回值用 Num 或原始局部承接，`if` / `let` / loop 回边汇合不退回 object。
2. 剖析 `RhpNewFast`、`__tls_get_addr`、Numbers 慢分派与 `IFn.invoke` 仍占高的项，找出快路径上的实参数组、缓存 key、委托与包装分配。
3. 保持快路径小而可内联、慢路径不内联；大方法拆冷路径，避免 JIT 内联预算耗尽。
4. 量化 LongBox 缓存扩大与否；无明确分配收益与全量收益时不改默认范围。

## 验收标准
- browse、cpstak、peval、destruc 前后耗时与分配采样；明确哪些是调用开销优化、哪些实际降低 GC。
- double 装箱继续避免回归到内联 `box`。
- 语义开关开 / 关输出相同；基线、门禁、全量不变。
