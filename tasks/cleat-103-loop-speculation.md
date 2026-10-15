# cleat-103 循环的 long / double 推测：快慢两份循环

- 里程碑：M10　依赖：cleat-102　状态：未开始
- 设计依据：[20 §4](../designs/20-skeg-optimizations.md)；skeg iron-071 / 080B

## 工作内容
1. 判定：最内层 `label` 循环里类型未定的形参，回边实参在"这些形参都是 long（double）"的前提下一定是 long（double）。
2. 生成：入口按实参检查（`isinst` 或 Num 标签），快份原始局部、回边不检查；慢份 object / Num；溢出直接抛（与慢份一致）。
3. 开关 `CLEAT_NO_OPT` 含 `loopspec`；语义用例（入口是 long / double / Ratio / BigInt / 非数、NaN、多形参一起推测）；IL 快照。

## 验收标准
- `bench/untyped_loop` 的 double 项降到与有提示的循环同级；基线、门禁、全量不变；种子不动点。
