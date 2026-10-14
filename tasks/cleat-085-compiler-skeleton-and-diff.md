# cleat-085 编译器工程骨架、差分工具与 L0 普查

- 里程碑：M8（阶段 A）　依赖：cleat-082、cleat-084（第一层）　状态：未开始
- 设计依据：[19 D4 ~ D6、D9](../designs/19-clojure-self-hosting.md)

## 目标
Clojure 编译器有地方放、有办法逐 pass 和 Scheme 编译器对比，并且知道只编 Clojure 时哪些节点与 pass 真正需要。

## 工作内容
1. `cleat.compiler.dll`：由现役 Scheme 编译器从 `src/clojure/cleat/{nanopass,compiler}` 编出（构建目标与 `tools/build.cs`），随运行时发布，首次需要时加载。
2. 开关骨架：`CLEAT_COMPILER=clojure` 时运行时钩子改走 Clojure 编译器（此时各 pass 未实现，先接通"前端 → unparse"的路径）。
3. 导出：Scheme 编译器对每个顶层形式写出各阶段 unparse（`l0` / `la` / `l1` / `l2` / `l3` / `lcil`），输入集 = boot 镜像全部顶层形式 + 随附包 + 语料；Clojure 编译器同格式导出。
4. 规范化与差分工具（`tools/compiler-diff.clj` 或 `tools/build.cs compiler-diff`）：gensym / 临时变量 / 局部变量 / 常量序号规范化，按形式与阶段报告第一个差异。
5. 普查（D4）：编整个 boot 镜像与包时统计每种 L0 节点的出现次数、每个 pass 是否改变其输入；`cp0-inline`、推测循环、primdata 折叠关掉前后 Lcil 是否相同。结论写进本任务。
6. 双轨纪律（D9）写进 TASK.md 的工作约定。

## 验收标准
- 差分工具对 Scheme 自己（两次运行）报告零差异；故意改一个 pass 时能定位到形式与阶段。
- 普查表：Clojure 编译器要实现的节点与 pass 清单。
