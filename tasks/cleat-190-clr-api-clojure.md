# cleat-190 `clojure.clr.api.Clojure`（C# 嵌入 API，与 ClojureCLR 一致）

- 里程碑：M23　依赖：无　状态：完成（2026-11-10）

## 问题

ClojureCLR 给 C# 宿主的入口是 `clojure.clr.api.Clojure`（`var(ns, name)` / `var(qualifiedName)` / `read(s)`，对应 JVM 的 `clojure.java.api.Clojure`）。
cleat 的嵌入 API 是自己的 `Cleat.Core.Hosting.CleatRuntime`（Init / Eval / Require / Var）。test_clojure 的 `api` 命名空间因此加载不了，
按 ClojureCLR 文档写的 C# 代码也要改。

## 工作内容

1. 在 Cleat.Core（或 Cleat.Hosting）加 `clojure.clr.api.Clojure`：`var(object ns, object name)`、`var(object qualifiedName)`、`read(string)`，
   惰性启动语义与 `CleatRuntime` 相同（首次调用时启动运行时）。
2. NuGet 冷加载测试里加一个只用 `clojure.clr.api.Clojure` 的 C# 项目。
3. test_clojure 的 `api` 命名空间加载并通过（或分诊）。

## 验收

- C# 按 ClojureCLR 的写法能用；`api` 命名空间有处置；门禁通过；README 的嵌入一节同时介绍两种入口。

## 结果

- `clojure.clr.api.Clojure`（`src/Cleat.Core/Runtime/ClojureApi.cs`）：`var(ns, name)`、`var(qualifiedName)`（字符串或符号）返回 **Var 本身**（IFn，同 JVM / ClojureCLR 的
  `Var.intern`：不存在时 intern 未绑定的 Var，调用时取当前值）；`read(s)` 用 `clojure.edn/read-string`（第一次用时在 serialized-require 的锁里 require clojure.edn）。
  放在 Cleat.Core 而不是 Cleat.Hosting：宿主里的 Clojure 代码（test_clojure 的 api）也要能 import 它。惰性启动经 `RuntimeBootstrap.EnsureStarted`（没启动时按名字载入
  Cleat.Hosting 调 `CleatRuntime.Init`，与包程序集的类型相同）。调用在调用方线程上，不排到 CleatRuntime 的工作线程（源码加载仍经登记的加载线程）。
- NuGet 冷加载：`NuGetColdLoadTests.CSharpAndFSharpProjectsColdLoadAPackageFromALocalFeed` 加第三个 C# 项目，只 `using clojure.clr.api`、不调初始化：
  require 包里的命名空间、调它的函数、`read` + `pr-str` 往返、`+`。
- test_clojure `api`：Clojure 类有了，剩 `clojure.test.generative` 的 `defspec`（与 `clojure.test-clojure.generators`），移入跳过表交 [cleat-191](cleat-191-test-libs-and-missing-nses.md)。
  加载不了的命名空间 1 → 0。三个 defspec 测的性质（`read` 往返、`var` 往返）由 `ClojureCompilerTests.ClrApiClojure`（`data/clr_api.clj`）在宿主里覆盖。
- README 新增"从 C# / F# 调用"一节，两种入口并列；designs/17 记一笔。

## 门禁（2026-11-10，13）

- Core 116 / Compiler 50 / Host 296 全过（Host 17 分 19 秒，含 NuGet 冷加载的 clojure.clr.api 项目与 ClrApiClojure）
- image-set --twice --ilverify：全部通过
- conformance --gate：521 / 521，OK
- CTS：6868 / 29 / 6，基线 OK
- test_clojure：49 个命名空间（36 跳过、0 加载不了），445 测试，4282 / 43 / 4，基线（本任务重写）OK
