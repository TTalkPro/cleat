# cleat-118 类型化调用点：窄类型数组的类型化访问与原始值贯通

- 里程碑：M11　依赖：cleat-117　状态：完成（2026-10-16）
- 设计依据：[21](../designs/21-r7rs-gaps.md)、[22 §2 D5](../designs/22-gc-and-performance-followups.md)、[23 §0](../designs/23-type-specialization.md)

## 工作内容（原计划）
1. 非递归的纯数值函数也有类型化入口；调用点实参类型已知时直调 invokeStaticTyped，结果用 Num 承接。
2. let 绑定、不被赋值的已知 lambda：直调其静态方法。

## 实现记录（2026-10-16）

### 量化后放弃的
- **第 1 项**：非递归的小数值函数在调用点已经被 cleat-116 内联；没被内联的大函数在 r7rs 的热路径上大多不是纯数值（返回 false / 向量），
  收益上界很小。不做；更通用的"按实际类型特化"转到 designs/23（cleat-127 ~ 129）。
- **第 2 项**：letrec / let 绑定、不被赋值的已知 lambda 在同一方法里直调——没有捕获变量时 `call 类::invokeStatic`，否则 castclass 到
  sealed 类后调它的 `invoke`（含 letfn 展开 `(let ([f (letrec ([f' …]) f')]) …)` 的别名）。实现了、语义对，但本机交替复测
  earley、dynamic、conform、peval、graphs 都在 ±10% 噪声内、方向不一致（Tier1 的 PGO 已把这些接口调用去虚化）。撤回。

### 做了的：窄类型数组的类型化访问（[23 §0](../designs/23-type-specialization.md) 的第 1 层缺口）
- `^ints` / `^shorts` / `^bytes` / `^floats` 与局部初值（`(int-array n)` 一类）的 `aget` 此前走通用的 `RT.agetOnArray`（`Array.GetValue` + 装箱）：
  cleat-106 以为类型化结果会改变类型，其实通用路径的结果经 `RetObject` 规范化后本来就是 Int64 / Double。现在 int / short / byte / sbyte /
  ushort / float 都决议到类型化的 `RT.aget(T[], int)`。uint / ulong / decimal 不改（返回值规则与运行时不同）。
- 宿主调用结果的原始用途：Int16 / SByte → `conv.i8`、Byte / UInt16 → `conv.u8`、Single → `conv.r8`（与装箱路径的转换相同），
  类型推断把它们当作 l64 / fl，于是 `(+ s (aget a i))` 里的累加变量不再退回 object。
- 决议器（Cleat.Core 的方法）：接受精确的加宽——较窄的整数（含 Int32）→ Int64 形参、Single → Double 形参；多个候选并列时取在已知实参位置上
  形参更具体的那个（值类型优于 object，JVM 的 subsumes）——`(+ 1.0 (aget ^floats f i))` 选 `add(double, double)` 而不是 `add(double, object)`。

本机微基准（100 万元素 × 200 轮求和，有提示）：

| 元素类型 | 改前 | 改后 |
|---|---|---|
| int | 4046 ms | 225 ms |
| byte | 5913 ms | 192 ms |
| short | —（同 byte） | 190 ms |
| float | 1431 ms（只做前两处时） | 388 ms |
| long（对照） | 292 ms | 201 ms |

- 语义：`data/array_semantics.clj` 追加窄类型的读、算术、越界、范围检查（`aset` 写 3000000000 到 int[] 照常抛异常）、有符号 / 无符号与循环累加，
  `CLEAT_NO_OPT=host` 开 / 关逐字相同。
- 顺带删掉 cleat-117 提交里误带的一行调试输出（driver.clj 的 `TMP_DUMP_EVAL`）。

## 结果（2026-10-16，david@192.168.3.111）
- 种子不动点（2086 KiB，与本机逐字节相同）；门禁 521 行 OK；cts、test-clojure 两套基线不变；Core 117 / Host 264 通过（1 个按环境跳过，
  `ConsoleExampleQueriesCsvWithSqlite`、`ToolsBuild` 只在该机因缺 NuGet 包失败）。
- r7rs 56 项 cleat 侧（各一次）与 117 的几何平均比值 1.000（对 OpenJDK C2 仍为 1.85x、对 GraalVM 1.99x）：r7rs 的数组是 `^objects` / long 数组，
  不受窄类型改动影响；parsing -11%、cat -9% … sum +27%、tail +12% 属单次波动。收益在窄类型数组的代码上（上面的微基准）。

## 验收标准
- browse、destruc、lattice、puzzle、takl 前后数字；语义开 / 关对照。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
