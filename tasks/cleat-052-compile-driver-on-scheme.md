# cleat-052 编译驱动与宿主接入

- 里程碑：M4′　依赖：cleat-051　状态：未开始
- 设计依据：[04 §9](../designs/04-compiler.md)、[05 §6](../designs/05-cil-backend.md)、cleat-012 / 022 / 030

## 工作内容
1. `eval`：展开 → 简单形式解释（04 §9，借 stage0 的 `compile-core`）/ 其余编译到内存程序集；`macroexpand` / `macroexpand-1` 钩子。
2. `load` / `compile`：逐形式展开—编译—执行；`.clj.dll` + 接口文件（`InterfaceFile`）；`BootInterface.Check` 调用的生成。
3. 用 Scheme 实现 `RuntimeHooks` 的登记（`load-source` `compile` `eval` `macroexpand-1`）；`Cleat.Host` 接上，REPL 可用。

## 验收标准
- REPL 里 `(require 'clojure.string)`（源码加载 → 编译）成功；`clojure.string` 等三个命名空间编译为 `.clj.dll` 并通过各自的 test_clojure 测试。
