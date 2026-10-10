# cleat-039 codegen-llvm

- 里程碑：后续（LLVM）　依赖：cleat-037、cleat-038、cleat-025　状态：搁置（2026-10-10 决定先完成 CIL 能力）
- 设计依据：[06 §1 D4、§2](../designs/06-llvm-backend.md)

## 工作内容
1. `codegen_llvm.clj` + `llvm_ir.clj`（IR 文本写出：模块头、类型、全局、函数、基本块、指令）。
2. 节点映射：`fn` → 每 arity 一个 `define` + ctx 类型 + 创建代码；已知调用直接 `call`；未知经 `cleat_callK`；`if` 内联真值判断；`let`/`label`/`jump` 用 `alloca`（依赖 `opt` 的 mem2reg）；`box`；Var 全局与 `cleat_load_<ns>` 内的 intern；常量 blob 与 `@ns.constants`；`deftype` 结构体、描述符、方法；协议调用经 `cleat_proto_lookup`；`case` → `switch`；`try`/`throw` 按 cleat-004 方案；`monitor-*`；集合节点经原语；顶层形式 → `@ns.init_k`；`main` 生成。
3. `compile-small` 的 LLVM 版本：每个小程序 → `.ll` → clang → 运行 → 与 .NET 结果比较。

## 验收标准
- `compile-small`（不含互操作用例）在原生目标全部通过；`-O1` 与 `-O0` 结果相同。
