# cleat-028 编译驱动：compile-form / eval、compile、构建脚本

- 里程碑：M4　依赖：cleat-027、cleat-022　状态：未开始
- 设计依据：[04 §9](../designs/04-compiler.md)、[08 §2、§3](../designs/08-bootstrap.md)

## 工作内容
1. `compile-form`：Lcore → Lcil S 表达式 → `Cleat.Emit` 内存模式 → 无参 `IFn`；登记为 `RuntimeHooks.Compile`（新系统）。
2. `compile`/`compile-file`：逐形式展开—编译—执行；收集 Lcil → `.clj.dll` + `.ifc`；`*compile-files*`/`*compile-path*`。
3. `src/boot/build-boot.clj`：按 03 §5 顺序；写 `boot.lcil` 文本与 `cleat.boot.dll`、`cleat.boot.ifc`；`--deterministic`。
4. `--target` 参数（现在只有 `:clr`）。
5. 测试：编译 `clojure.string`、`clojure.set`、`clojure.walk` 为 `.clj.dll`，由 `Loader` 加载并通过各自的 `test_clojure` 测试。

## 验收标准
- 单命名空间编译 + 加载 + 测试通过。
