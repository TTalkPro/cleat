# cleat-041 交叉编译驱动与原生语料验收

- 里程碑：后续（LLVM）　依赖：cleat-040　状态：搁置（2026-10-10 决定先完成 CIL 能力）
- 设计依据：[06 §1 D1、§2](../designs/06-llvm-backend.md)、[09 §2](../designs/09-testing.md)

## 工作内容
1. `cleat --target llvm compile ns… -o app`：展开（.NET 上）→ `.ll` → clang → 链接 `libcleat-rt.a` + `libcleat-core.a` → 可执行；`--emit-ll`、`--shared`。
2. 语料 runner 的原生模式：把每条 `:common` 用例编成一个程序（或批量成一个程序，每条一个函数）运行，与 .NET 结果比较。
3. 偏离表加 `:llvm` 分类（06 D7 的不支持项）。
4. 文档：`docs/llvm-backend.md` 用户说明。

## 验收标准
- 语料 `:common` 子集在原生目标的通过率记入本文件与 09 §3（目标 ≥ 90%，未通过的全部分类）。
