# cleat-137 删除 cleat-132 的 LL / DD 编译期预生成

- 里程碑：M15　依赖：cleat-136　状态：完成
- 设计依据：[27 §1](../designs/27-jit-style-runtime-specialization.md)

## 问题

cleat-132 对 cleat-104 推不出类型化入口的 ≤3 参纯递归函数，在编译期按猜测的 `LL` / `DD` 等
组合预生成类型化重载，入口用 `ssKind` 静态缓存分发。它只覆盖猜中的数值签名，生成的多数候选从不执行；
用户决定（2026-10-19）删除这条路线，特化统一走运行时按观察到的类型生成（cleat-138 / 139）。

## 工作内容

1. codegen 删除 `sigspec-param-vectors`、`spec-signature-entry-tries` 的预生成分支、`SigEntry` 节点
   （类型、代码生成 `cg-signature-entry!` / `cg-signature-call!`）、`spec-signature-entries` 表、
   函数类的 `ssKind` 字段与重载方法、编译单元里对预生成体的二次推断。
2. 候选判定改名 `spec-runtime-candidate?`：cleat-104 不成立、形状合适的候选一律包成 `RuntimeSigEntry`。
3. cleat-104 编译期类型化入口不变（它有编译期证据）。

## 验收

- `sigspec_semantics.clj` 在默认与 `CLEAT_NO_OPT=sigspec` 下输出逐字相同。
- `SignatureSpecialization`：`sig_dd` 不再有 `ssKind` / `invokeStaticTyped`，改为 `__rtSpec` 运行时候选；
  `TypedEntries` 不变。
- Release 构建通过。完整基线与种子重生成在 cleat-141 批量验证。
