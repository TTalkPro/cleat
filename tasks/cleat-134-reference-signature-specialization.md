# cleat-134 引用类型运行时签名特化

- 里程碑：M14　依赖：cleat-133　状态：完成
- 设计依据：[25](../designs/25-runtime-reference-signature-specialization.md)

## 问题

cleat-133 的运行时特化只接受 `Int64` / `Double`，`String`、`Int32[]` 等引用形参
始终回退通用路径。不能把这些类型降级成普通 `Object`，否则 `int[]` 的类型化
`aget` 仍无法决议；也不能预生成任意 CLR 类型的组合。

## 方案

签名继续用 `L/D/O` 描述存储形式；精确引用类型保存在运行时分发的 `Type[]`。
运行时证明成功后，把引用形参在 L3 特化体中标成 `Tagged`，复用宿主调用静态决议器。
`RuntimeSigFn` 继续做精确类型检查，未证明路径全部回退现有通用协议。

## 工作内容

1. 保守纯度检查接受只读的 `RT.aget` / `RT.alength` / `RT.count`。
2. 数值预生成候选为空时继续进入 `RuntimeSigEntry`，不生成空签名分发。
3. 运行时观察引用精确 `Type`，按数值 + `O` 组成签名；`spec-entry-try` 支持引用
   形参标注并重新推断。
4. 生成 `RuntimeSigFn` 时使用精确 `Type[]`；特化体里的引用形参携带 `Tagged` 元数据。
5. 补充 `Int32[]`、`String`、混合签名、错误类型回退和 `with-redefs` 语义测试；
   断言生成的特化调用包含 `RT.aget(Int32[], Int32)`。

## 非目标

- 不支持引用返回值。
- 不接受 `aset` 等可变副作用调用。
- 不预生成类型组合，也不扩展 `L/D/O` 字符串协议。

## 验收

- 默认、`CLEAT_NO_OPT=sigspec`、`CLEAT_NO_OPT=typed` 输出逐字相同。
- `Int32[]` / `String` 触发运行时生成；非精确类型和 `null` 回退。
- Release 构建通过；SignatureSpecialization 专项通过；Core 全量通过；种子不动点通过。

## 验证结果

- `sigspec_semantics.clj`：`Int32[]` 特化返回 `9`，`Int64[]` 回退返回 `11`，
  `String` 特化返回 `2`，混合签名特化返回 `6`，`Double[]` 回退返回 `2.5`。
- 默认、`CLEAT_NO_OPT=sigspec`、`CLEAT_NO_OPT=typed` 三种模式输出
  SHA-256 均为 `4468892e7f422b5a04ba0d46eaa2f2987527a56f40f59a7c8d3610650a674e37`。
- Release 构建、SignatureSpecialization、Core 118/118、
  `BuildFromTheSeedReproducesTheSeed`、`SelfhostReachesAFixedPoint` 全部通过。
- `clojure-seed` 重新生成后 stage2/stage3 固定点检查通过，
  seed 中的 boot 与 compiler 产物已同步更新。
