# cleat-017 cleat.kernel 与 core.clj 分层适配

- 里程碑：M2　依赖：cleat-011、cleat-013　状态：未开始
- 设计依据：[03 §5](../designs/03-language-core.md)、[00 D15](../designs/00-overview.md)

## 目标
`src/clojure/` 的源码树：`cleat/kernel.clj` + 从 ClojureCLR 改写的 `clojure/*.clj`。

## 工作内容
1. `cleat/kernel.clj`：把原语 Var `def` 为可用函数；编译器与 nanopass 需要的最小函数集（列表见 03 §5）；`defmacro`/`defn`/`let`/`loop`/`fn`/`when`/`cond`/`and`/`or` 等基础宏若 `core.clj` 开头已定义则不重复。
2. 复制 ClojureCLR `Clojure.Source/clojure/` 的文件到 `src/clojure/clojure/`，保留 EPL 头；`core.clj` 开头 refer `cleat.kernel`；去掉对 `HostExpr/MaybeType`、`Compiler/…` 的调用（改原语）；`;;;` 注释的 CLR 差异保留；移除 `parse-eval`、`await` 相关；`*compiler-options*` 等 Var 的来源改为运行时。
3. 把 `#?(:cljr …)` 保持可用，新增 `:cleat-llvm` 分支先留空。
4. 记录每处改动（文件、行、原因）到本文件。

## 验收标准
- 03 §5 加载顺序中的每个文件都能在 stage0 上展开（无未知形式）。
