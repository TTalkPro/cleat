# cleat-037 机器描述表与原语表的 llvm 列

- 里程碑：后续（LLVM）　依赖：cleat-004、cleat-028　状态：搁置（2026-10-10 决定先完成 CIL 能力）
- 设计依据：[04 §4](../designs/04-compiler.md)、[06 §1 D5、D6](../designs/06-llvm-backend.md)

## 工作内容
1. `targets.edn` 加 `:llvm` 项（符号前缀、`max-positional-arity 10`、triple、datalayout、异常方案开关、`musttail` 开关）；codegen 入口按 `*compile-target*` 分派。
2. `native/cleat-rt/primitives.h` 的标注格式（`// @primitive clojure.core/first arity=1`）；PrimGen 读它并与 `:clr` 列合并，名字 / arity 不一致时构建失败。
3. `host-*` 节点在 `:llvm` 目标的编译错误信息。
4. reader 条件 `:cleat-llvm` 随 `*compile-target*` 生效。

## 验收标准
- `primitives.edn` 含两列；PrimGen 的一致性检查有正反测试。
