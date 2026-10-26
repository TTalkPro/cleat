# cleat-167 clojure.core.async 包

- 里程碑：M19　依赖：无　状态：未开始
- 设计依据：[29 §1 第 3 条 / §7](../designs/29-usability-and-engineering.md)

## 问题

`(require 'clojure.core.async)` 报 FileNotFoundException。core.async 的 `go` 宏依赖 IOC 状态机变换，
上游实现建在 tools.analyzer(.jvm) 上，不能直接搬过来。

## 工作内容

1. **spike（约一周）**：比较两条路线，选一条。
   - A：以 ClojureCLR 的 clr.core.async 为起点，移植 tools.analyzer 的最小子集；
   - B：在 cleat 自己的 analyzer（`cleat.compiler.analyzer` 的 L0）上实现 IOC 变换。
   比较内容：代码量、与 cleat 宏展开 / 局部变量模型的贴合度、对种子的影响（B 可能要从编译器暴露 API）。
2. 落地选定路线，建 `packages/core.async`：channel / buffer / `go` / `<!` / `>!` / `alts!` / `timeout` / `pipeline` / `mult` / `pub`。
   线程池与定时器用 .NET 原生设施，`thread` 走专用线程，可以复用 `src/Cleat.Core/Runtime/AsyncSupport.cs`。
3. 上游测试移植，失败项归类。

## 验收

- spike 结论（路线、理由、数据）写进任务书，再开始第 2 条。
- 上游测试通过率；`go` 块里 `try` / `loop` / `case` / 解构的组合用例可以跑通。
- 改到编译器时重走种子不动点与 29 §9 的门禁。
