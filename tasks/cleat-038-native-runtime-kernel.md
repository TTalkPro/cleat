# cleat-038 原生运行时 cleat-rt 内核

- 里程碑：后续（LLVM）　依赖：cleat-004、cleat-037　状态：搁置（2026-10-10 决定先完成 CIL 能力）
- 设计依据：[06 §1 D2、D4](../designs/06-llvm-backend.md)

## 工作内容
1. `native/cleat-rt/`（C，CMake 或 Makefile；Boehm GC）：对象头与类型描述符、单例、装箱数（小整数缓存）、double、字符串（UTF-8）、符号 / 关键字驻留、Var（根、dynamic、TLS 绑定栈）、命名空间表、`cleat_fn` 与 `cleat_call0..10`/`calln`/`apply`、异常（按 cleat-004 的方案）、`monitor`、数值运算与比较（long 溢出、double、bigint 的升级策略按 cleat-004 后的决定）、哈希（Murmur3，与 CLR 侧一致）、原始数组、最小 IO、`cleat_fasl_read`、`cleat_rt_init`、模块登记。
2. 协议表：类型描述符的协议 id → 方法指针数组；`cleat_proto_lookup`、`cleat_extend_type`；原生类型各一个描述符。
3. C 单元测试（每个原语的最小用例）；与 CLR 侧的哈希一致性测试（同一组值两边哈希相同）。
4. 规模目标 5000 ~ 8000 行。

## 验收标准
- 手写 `.ll`（cleat-004 的用例扩展）链接 `libcleat-rt.a` 通过；哈希一致性测试通过。
