# cleat-127 数组形参推测：无提示数组形参的类型化入口

- 里程碑：M13　依赖：cleat-118　状态：完成（2026-10-16）
- 设计依据：[23 §1](../designs/23-type-specialization.md)

## 工作内容
1. 候选：无提示的形参在真正的循环里被 `aget` / `aset` / `alength` 使用，体内不被 `set!`、循环里不被 recur 重新绑定成别的值。
2. 元素类型证据：`aset` 的值的原始类型、`aget` 结果进入的算术类型、同一单元里调用点的实参类型；冲突或无证据时不推测，最多两种。
3. 沿用 cleat-104 的类型化入口：签名扩展数组类型；通用入口 `isinst T[]` 后转调类型化体，体内 `ldelem` / `stelem` / `ldlen`，不需要 bailout。
4. 开关 `CLEAT_NO_OPT=arrayspec`。

## 实现记录（2026-10-16）
- 载体改为**循环推测**（cleat-103）而不是类型化入口：不要求函数是纯的，闭包、嵌套循环里的循环也适用。对最内层循环
  `(label x (x* …) body0 body1)`：body1 里被 `aget` / `agetOnArray` / `aset` / `alength` 当数组用、在循环外绑定、不被赋值、
  推断类型不是 long / double 的变量 a（只推测一个），按证据猜元素类型：
  `aset` 的值的推断类型（l64 → `long[]`，fl → `double[]`，其余 → `object[]`）＞ `aget` 结果进入 Numbers 算术（`long[]`、`double[]` 两种）＞ 通用用途（`object[]`）。
- 生成 `(if (isinst T[] a) (let ([a' a]) 循环的副本（a 的用途换成 (Tagged T[] a')）) 原来的循环)`，两种推测时嵌套；副本里的数组访问由
  cleat-106 的静态决议选到类型化的 `RT.aget` / `aset`。原来的循环仍可做数值推测。开关 `CLEAT_NO_OPT=arrayspec`。
- 本机微基准（无提示，100 万元素 × 50 轮）：long[] 求和 841 → 54 ms（有提示 58 ms）、double[] 求和 963 → 88、long[] 填充 4781 → 67、object[] 计数 94 → 55。
- 语义：`data/arrayspec_semantics.clj`（各元素类型、推测不中退回、nil、非数组、越界、错误的值类型、long 溢出、闭包、嵌套循环）开 / 关逐字相同；
  测试 `ClojureCompilerTests.UnhintedArraysInLoopsAreSpeculated`。门禁 OK，种子不动点（2103 KiB）。

## 结果（2026-10-16，批量验证，david@192.168.3.111）
- 种子不动点（2103 KiB）；门禁 521 行 OK；cts、test-clojure 两套基线不变；Core 117 通过，Host 只有两项该机环境导致的失败（NuGet 包），
  新测试 `UnhintedArraysInLoopsAreSpeculated` 通过。
- r7rs 56 项 cleat 侧（各一次）与 118 的几何平均比值 1.001：r7rs 的数组都带 `^objects` / `^longs` 提示，不受影响；收益在无提示的数组循环（上面的微基准）。

## 验收标准
- 无提示数组循环微基准（long / double / object / int 数组）接近有提示的版本；r7rs 去掉 `^objects` 提示的变体前后对照。
- `data/array_semantics.clj` 开 / 关逐字相同；基线、门禁、全量不变；种子不动点。
