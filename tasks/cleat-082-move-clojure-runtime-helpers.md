# cleat-082 Clojure 运行时辅助类搬进 Cleat.Core

- 里程碑：M8（阶段 A）　依赖：cleat-081　状态：完成
- 设计依据：[19 D8](../designs/19-clojure-self-hosting.md)

## 目标
编出的 Clojure 代码只依赖 Cleat.Scheme 里"与 Scheme 无关"的辅助类，这些类先搬到 Cleat.Core，让 Clojure 编译器从第一天起就面向最终的运行时名字。

## 工作内容
1. 搬：`Locals`（`Take`）、`Clojure/ClojureOps`、`Clojure/ClojureStaticCalls`、`Clojure/HostSyntax`、`Clojure/HostSyntaxClasses`、`RT.IsTrue` / `RT.Bool`、
   `Driver.cs` 中与 Scheme 无关的部分（简单形式解释 `TryEvalSimple`、错误格式化与栈过滤、接口文件 / 内联文件读写、直接链接目标）。新命名空间在本任务里定（建议 `Cleat.Core.Compiler`、`Cleat.Core.Interop`）。
   依赖 Scheme 类型的方法（如 `SeqToList`、`HoldResult` / `TakeResult` 的 Int32 处理）留在 Cleat.Scheme 的薄垫片里。
2. Scheme 编译器（codegen、syntax.sls、compile.sls、driver.sls）改引用新名字；`Cleat.Emit` 按名字决议的类型表同步。
3. 重生成 Scheme 镜像、种子（不动点）、Clojure 镜像与包程序集。

## 验收标准
- `cleat.boot.lcil` 里不再出现 `Cleat.Scheme.Locals` / `Cleat.Scheme.Clojure.*` / `Cleat.Scheme.RT::IsTrue`；剩下的 Scheme 引用只有 D8 第二条所列（Procedure、ContinuationException、EmptyList、StaticProcedure、Fasl、RT.Box）。
- 种子不动点；全量测试通过；Clojure 基准不退化。

## 结论（2026-10-14）

完成。

**搬到 Cleat.Core 的东西**

| 新位置 | 来源 | 说明 |
|---|---|---|
| `Cleat.Core.Runtime.Locals` | `Cleat.Scheme.Locals` | 原样 |
| `Cleat.Core.Runtime.Truth`（`IsTrue` / `Bool` / `True` / `False`） | `Cleat.Scheme.RT` 的同名成员 | Scheme 的 `RT.True` / `False` 指向同一对对象（`eq?` 比较 `#t` / `#f` 靠对象标识）；`RT.Box(int)` 留在 Scheme（D8 第二条再去掉） |
| `Cleat.Core.Runtime.ClojureOps` | `Cleat.Scheme.Clojure.ClojureOps` 里编译后代码调用的静态方法 | `HoldResult` 的值改由 `TakeHeldResult` 取出；`DefineType` 改登记 `CompileTimeTypes` |
| `Cleat.Core.Runtime.ClojureStaticCalls`、`AsyncSupport` | `Cleat.Scheme.Clojure` | 原样（`clojure.clr.async.task.alpha` 与测试里的类名同步改） |
| `Cleat.Core.Compiler.HostSyntax`、`HostSyntaxClasses` | `Cleat.Scheme.Clojure` | 原来经 Scheme 的 `TypeResolver` 查桩与本进程生成的类型，改查新的 `CompileTimeTypes` |
| `Cleat.Core.Compiler.CompileTimeTypes` | Scheme `TypeResolver` 的编译期类型表 | 两边共用一张表：Scheme 的 `AddCompileTimeType` / `GetTypeFast` 转到它 |
| `Cleat.Core.Compiler.Diagnostics` | `Driver.cs` 的读形式、源位置记录、错误文本、栈过滤 | Scheme 条件对象的文本经 `MessageHook` 由 Scheme 侧提供；栈过滤按程序集名去掉 `Cleat.Scheme` |
| `Cleat.Core.Compiler.AotPaths` | `Driver.cs` 的输出路径、加载键、镜像 id、直接链接目标 | 原样 |

留在 Cleat.Scheme 的：`ExpandOps`（原 `ClojureOps` 里展开期用的 Scheme 过程）、`Driver.cs` 里与 Lcore / Scheme 数据绑在一起的部分（简单形式解释、接口文件与 `.inl`、钩子包装、`CompileBoot` / `CompilePackage` 的 Scheme 列表）——这些由 Clojure 编译器重写（cleat-086、088、090），不搬。

**编译器与工具**：targets.sls 加 `truth`、`clojure-ops` 指向 Cleat.Core；codegen 的 `Locals`、`ClojureStaticCalls`、`AsyncSupport` 改名；`(clojure …)` 各库改用 `ExpandOps` / `AotPaths` / `HostSyntax` 的新名字；
`tools/build.cs compile-tools`（单独重编 tools.namespace / tools.build 的包程序集；原来只有 pack 里做）。

**验证**

- `cleat.boot.lcil` 中剩下的 Scheme 引用：`EmptyList` 1126、`Procedure` 109、`ContinuationException` 35、`RT`（`Box`）28、`StaticProcedure` 19，与 D8 第二条一致；`Cleat.Core.Runtime.Locals` 25668、`Truth` 2496、`ClojureOps` 3260、`ClojureStaticCalls` 3087。
- 种子重生成后不变；Scheme 不动点（Bootstrap 6/6）、Clojure 镜像不动点 B == C（Host 的 Bootstrap 11/11）。
- Scheme 快照 13 个 `.lcil` 的差异只有 `Cleat.Scheme.RT` → `Cleat.Core.Runtime.Truth`，已更新；`tests/scheme/clojure/driver.sps` 改用 `Diagnostics.FormatError`。
- 全量：Core 129/129、Host 248/248、Scheme 255 中 252 + 上述快照 / driver.sps 重跑 19/19；`5_2.ms` 在与 Host 测试并行时 remq:7 超时、remv:6 的工作进程 OOM（循环表用例），单独运行 `fail 0`，与本任务无关（负载下的已有现象）。
- 基准（同一机器、旧构建与新构建各 `bench -r 3`）：8 项最小值都在噪声内（如 assoc_map 57 / 54、lazy_chain 90 / 82、reduce_vec 27 / 21、protocol 33 / 34 ms）。
