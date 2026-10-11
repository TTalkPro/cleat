# cleat-042 性能：拆箱、协议内联缓存、跨单元直接链接

- 里程碑：后续　依赖：cleat-032　状态：进行中
- 设计依据：[04 §10](../designs/04-compiler.md)、[06 §4](../designs/06-llvm-backend.md)

## 工作内容
1. 基准集（`bench/`：语料中的算术 / 集合密集用例 + ClojureCLR 常用基准），记录 M6 基线。
2. 类型推断与拆箱：`^long`/`^double` 局部与 `label` 形参用 `int64`/`double` 局部变量（IronScheme iron-034c 的工作表法）；原始类型签名 `IFn.LL` 等。
3. 协议调用内联缓存（站点类或 `MethodImplCache` 快速路径内联）；`keyword-invoke` 站点缓存。
4. 跨单元直接链接到用户 `.clj.dll`；`:inline` 之外的小函数内联。
5. 局部变量清除（`locals-clearing`）。
6. LLVM：NaN-boxing / 小整数立即数、`musttail`、`hoist-var-derefs`。

## 验收标准
- 每项有基准前后数字；不动点与符合性基线不退化。

## 进度（2026-10-11）

1. **基准集与基线** —— 完成。`bench/` 七个基准（sum_loop、fib、reduce_vec、lazy_chain、assoc_map、protocol、keyword_invoke），
   `tools/build.cs bench [-r n] [--write] [--only re]` 聚合最小/中位。基线（Release，5 次，`bench/baseline.edn`）：
   sum_loop 1006 ms（500 万次装箱算术）、fib 49 ms（fib 25）、reduce_vec 119 ms（100 万 reduce）、
   lazy_chain 349 ms（30 万惰性链）、assoc_map 219 ms（20 万 assoc）、protocol 517 ms（40 万分派）、
   keyword_invoke 631 ms（50 万 `(:k m)`）。之后的每项优化对照此表。
4. **跨单元直接链接到用户 `.clj.dll`** —— 已在 cleat-033 完成（受保护的 invokeStatic 直调，接口文件登记 + with-redefs 回退；
   本基准的数字即含此优化的状态）。
6. **LLVM** —— 整体搁置（TASK.md 2026-10-10 决定），不在本任务内。

待做：2（类型推断与拆箱，iron-034c 工作表法 + `IFn.LL` 原始签名）、3（协议与 keyword-invoke 的内联缓存）、
4 剩余（`:inline` 之外的小函数内联）、5（locals-clearing）。每项完成后在此记基准前后数字。
