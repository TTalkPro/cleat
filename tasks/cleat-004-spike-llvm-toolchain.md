# cleat-004 验证 LLVM 工具链与原生 ABI

- 里程碑：后续（LLVM）　依赖：cleat-001　状态：搁置（2026-10-10 决定先完成 CIL 能力）
- 设计依据：[06 §5](../designs/06-llvm-backend.md)

## 目标
在写任何 LLVM 后端代码之前，用手写 `.ll` + 一个几百行的 C 存根运行时确认 06 §1 D4 的对象模型、调用约定与异常方案。

## 工作内容
`spikes/cleat-004/`（Makefile；clang 21）：
1. 存根运行时 `rt.c`：`cleat_obj` 头、`nil`/`true`/`false`、装箱 long、`cleat_fn`（`fnptr[0..10]` + `ctx`）、`cleat_call0..10`、`cleat_throw`，Boehm GC（`-lgc`）。
2. **异常两方案**各一份 `.ll`：(a) `invoke`/`landingpad` + `__gxx_personality_v0`（`cleat_throw` 用 C++ `throw`），(b) `setjmp`/`longjmp` 栈。用例：try/catch/finally、跨两层函数抛出、finally 中再抛、`-O1` 下正确；记录生成复杂度。**决定采用哪种**。
3. 调用约定：`define ptr @f_arity2(ptr %self, ptr %a, ptr %b)`；未知函数经 `cleat_call2`；闭包 ctx 读取；`musttail` 同签名一千万层不溢出。
4. `alloca` + `opt -O1 -passes=mem2reg` 得到 SSA；`switch i64`。
5. 从 .NET（`Process`）调用 `clang`，捕获诊断。
6. 常量：`.ll` 中的全局字节串 + `cleat_fasl_read` 存根读回符号 / 关键字 / 列表。

## 验收标准
- 结论（含采用的异常方案、调用约定）写回本文件与 06 §1。
- `make test` 全部用例通过。
