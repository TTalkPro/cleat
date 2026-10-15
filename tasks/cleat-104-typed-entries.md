# cleat-104 无提示已知函数的类型化入口

- 里程碑：M10　依赖：cleat-103　状态：未开始
- 设计依据：[20 §5](../designs/20-skeg-optimizations.md)；skeg iron-075 / 076

## 工作内容
1. 签名推测：单元内直接链接的顶层 `defn`（单固定子句、≤ 4 形参、体不大、无副作用 / 分配 / 组外调用），形参与返回值各推 `{long, double, obj}`，组内乐观不动点。
2. 生成：`invokeStaticPrim` 形的 `$t`；通用入口实参匹配时转调；类型已知的调用点直调（复用 `PrimInvoke` 路径）。
3. long 溢出抛异常，不做哨兵 / 重算 / 计数；不做过程级 Num 签名。
4. 开关 `CLEAT_NO_OPT` 含 `typed`；语义用例；IL 快照。

## 验收标准
- `bench/fib`（无提示）接近 `prim_fib`；基线、门禁、全量不变；种子不动点。
