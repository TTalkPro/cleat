# cleat-135 运行时签名表与高元数特化

- 里程碑：M14　依赖：cleat-134　状态：完成
- 设计依据：[26](../designs/26-runtime-signature-tables.md)

## 问题

cleat-134 的 `RuntimeSigFn` 只有一个精确签名；第二个热签名永远 miss，只能在通用体执行。
运行时候选也停在 4 参，无法覆盖 5+ 参纯函数；按需生成数量没有进程级上限。

## 方案

1. `RuntimeSigFn` 内部保存精确 `Type[]` → 特化 IFn 的不可变条目数组；热路径无分配。
2. 新签名 miss 后先回通用体；连续 miss 达到 16 次再请编译器补生成，成功后复制替换表。
3. 每函数最多 8 个签名，进程最多 4096 个运行时生成程序集；失败后暂停该函数补生成。
4. 运行时候选上限放宽到 20 参；9～20 参经 `InvokeArray`。
5. 5+ 参暂不接 cleat-128 活动剖面，因为其运行时 `Profile` 重载只到 4 参。

## 非目标

- 不做主动逐出、LRU 或批次 `Unload`；拆到 cleat-136。
- 不扩展 `L/D/O` 静态签名协议。
- 不支持引用返回值或 `aset` 副作用分段。

## 验收

- 6 参和 9 参纯递归函数正确执行；6 参生成类型化 CIL。
- `Int32[]` 后再观察 `Int64[]`，第二个签名进入同一表且旧签名仍命中。
- 默认、`CLEAT_NO_OPT=sigspec`、`CLEAT_NO_OPT=typed` 输出逐字相同。
- Release 构建、SignatureSpecialization、Core 全量、种子不动点通过。

## 验证结果

- `sigspec_semantics.clj` 新增 6 参输出 `4`、9 参输出 `4`、
  `Int32[]` / `Int64[]` 多签名输出 `7` / `13` / `17`。
- 三种优化开关的完整语义输出 SHA-256 均为
  `4aeaa249ad918f69c5a277927d0b1de959ea628d70c2bf747a6728f9dc372525`。
- 6 参生成的 LCIL 含 `invokeStaticTyped(Object, Int64 × 5) → Int64`。
- RuntimeSigFn 单元测试证明第 16 次 miss 后补生成，且旧 `Int32[]` 签名仍直接命中。
- Release 构建通过；Core 119/119；SignatureSpecialization 与
  `BuildFromTheSeedReproducesTheSeed` 通过；`clojure-seed` stage2/stage3 固定点通过。
