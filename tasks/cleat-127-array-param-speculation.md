# cleat-127 数组形参推测：无提示数组形参的类型化入口

- 里程碑：M13　依赖：cleat-118　状态：未开始
- 设计依据：[23 §1](../designs/23-type-specialization.md)

## 工作内容
1. 候选：无提示的形参在真正的循环里被 `aget` / `aset` / `alength` 使用，体内不被 `set!`、循环里不被 recur 重新绑定成别的值。
2. 元素类型证据：`aset` 的值的原始类型、`aget` 结果进入的算术类型、同一单元里调用点的实参类型；冲突或无证据时不推测，最多两种。
3. 沿用 cleat-104 的类型化入口：签名扩展数组类型；通用入口 `isinst T[]` 后转调类型化体，体内 `ldelem` / `stelem` / `ldlen`，不需要 bailout。
4. 开关 `CLEAT_NO_OPT=arrayspec`。

## 验收标准
- 无提示数组循环微基准（long / double / object / int 数组）接近有提示的版本；r7rs 去掉 `^objects` 提示的变体前后对照。
- `data/array_semantics.clj` 开 / 关逐字相同；基线、门禁、全量不变；种子不动点。
