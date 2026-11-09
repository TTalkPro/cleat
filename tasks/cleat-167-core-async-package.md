# cleat-167 clojure.core.async 包

- 里程碑：M19　依赖：无　状态：完成（2026-11-05）
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

## spike 结论（2026-11-05）：路线 A

**路线 B 的前提已经变了**：任务书写的 B 是在 `cleat.compiler.analyzer`（Clojure 编译器的 L0）上做 IOC 变换，那个编译器已在 cleat-182 删除；
现在的 B 只能是把 C# 分析器的内部结构暴露给 Clojure 代码再写变换，侵入编译器、又是一套新代码。

**路线 A 实测可行**：直接用 ClojureCLR 的四个库——[clr.core.async](https://github.com/clojure/clr.core.async) 1.9.865（4652 行）、
[tools.analyzer.clr](https://github.com/clojure/tools.analyzer.clr) 1.4.0（3244 行）、上游可移植的 [tools.analyzer](https://github.com/clojure/tools.analyzer)
1.2.2（2236 行）、[clr.tools.reader](https://github.com/clojure/clr.tools.reader) 1.5.2（2993 行），都是 EPL、本来就面向 CLR。在 cleat 上直接加载，
逐个修掉挡路的地方，一共 **7 处库源码补丁 + 1 处 cleat 修正**：

| # | 位置 | 问题 | 处理 |
|---|---|---|---|
| 1 | core.async `dispatch.cljc` | `Boolean/TryParse` 的 `by-ref`（cleat 编译器不支持 by-ref 实参） | 改为字符串比较 |
| 2 | core.async `dispatch.cljc` | 线程池工作项里漏出的异常让 .NET 进程直接退出（JVM 是交给线程的未捕获异常处理器打印）；CLR 版的 `ex-handler` 是空操作 | 工作项捕获后交 `ex-handler`，它照 JVM 缺省处理器打印到 *err* |
| 3 | core.async `go.clj` | 宏的 `&env` 值是 ClojureCLR 的 `LocalBinding`（`.HasClrType` / `.ClrType`） | 改用 cleat 的 `Cleat.Core.Expand.LocalBinding` 的 `:tag` 提示 |
| 4 | tools.analyzer.clr `clr.clj` | 导入了 cleat 没有的 `clojure.lang.Compiler`（实际没用） | 去掉 |
| 5 | tools.reader `reader.clj` | `clojure.lang.Reflector/InvokeConstructor` / `InvokeStaticMethod` | 换成 cleat 的 `Cleat.Core.Runtime.Reflector/New` / `Call` |
| 6 | tools.reader `impl/commons.clj` | `.AsInt64` 的 `by-ref` | 改为范围判断 |
| 7 | tools.analyzer.clr 依赖 core.memoize（再带 core.cache、data.priority-map） | 只用了 `lru` 与 `memo-clear!` 缓存反射查到的成员 | 落地时改成分析器内部的小缓存，不引入（也不冒充）core.memoize |
| — | **cleat**：`HostSyntax.MaybeType` | `(:import [System.Collections.Generic |LinkedList`1[System.Object]|])` 之后写 `(|LinkedList`1|.)` 解析不到：带反引号的名字只走泛型语法解析、不查命名空间的导入 | 解析不到时再查导入（ClojureCLR 的 HostExpr.MaybeType 同样先查映射） |

**结果**：`go` 里 `loop` / `try`（含 finally）/ `case` / 解构、`alts!`、`timeout`、`pipeline`、`mult`、`pub`、`thread`、100 个并发 go 块都正常；
ClojureCLR 移植的上游测试（8 个文件：async、buffers、concurrent、flow、ioc-macros、lab、timers、pipeline）**51 个测试、342 个断言全过**，约 40 秒。
不需要改编译器、不影响镜像（没有种子了，cleat-182），比 B 少得多的新代码。

**落地方案**：三个包——`packages/tools.reader`（`Cleat.ToolsReader`）、`packages/tools.analyzer`（`Cleat.ToolsAnalyzer`，tools.analyzer + tools.analyzer.clr）、
`packages/core.async`（`Cleat.CoreAsync`，依赖前两个）。`by-ref` 实参的支持记为编译器的已知缺口（ClojureCLR 的库里常见，目前两处都绕开了）。

## 落地（2026-11-05）

### 1. 三个包

| 包 | 程序集 / 版本 | 来源 | 上游测试 |
|---|---|---|---|
| `packages/tools.reader` | `Cleat.ToolsReader` 1.5.2 | clr.tools.reader | 51 个测试、**889 个断言全过** |
| `packages/tools.analyzer` | `Cleat.ToolsAnalyzer` 1.4.0 | tools.analyzer 1.2.2（可移植部分）+ tools.analyzer.clr 1.4.0 | 43 个测试、**370 个断言全过**（`analyze-proxy` 不跑，见下） |
| `packages/core.async` | `Cleat.CoreAsync` 1.9.865，依赖前两个 | clr.core.async | 51 个测试、**342 个断言全过** |

每个包有 `cleat.edn`（core.async / tools.analyzer 写了 NuGet 依赖）、`build.clj`、`test/run.clj`、README（用法、差异清单、已知限制）、EPL 许可文件。
三套测试都跑了源码与编出的包程序集两遍（`PackageTests.CoreAsyncWithAnalyzerAndReader`）；`image-set` 也编这三个包，`--twice` 26 个文件逐字节相同、`--ilverify` 12 个程序集全过。

spike 之后又改的：
- core.memoize 的替代：`clojure.tools.analyzer.clr.utils` 里的 `lru` / `memo-clear!`（上限 2048），不引入也不冒充 core.memoize。
- tools.reader 的 `read-metadata` 测试起初 9 个断言失败，查出 **cleat 的缺陷**（见下第 2 条第 2 点），修后全过。
- tools.analyzer 的 `analyze-proxy`：cleat 的 `proxy` 展开成自己的特殊形式 `proxy-class*`（编译期生成子类，cleat-043），tools.analyzer 不认得——
  **已知限制**：go 块里直接写 `proxy` 不能分析。测试注释掉并写明原因（README 同样写了）。

### 2. 顺带修的 cleat 缺陷

1. **泛型类型的短名**（spike 时）：`HostSyntax.MaybeType` 对带反引号的名字只走泛型语法解析、不查命名空间的导入；改为解析不到再查导入。
2. **集合字面量的元数据被剥掉位置键**：`(meta ^{:line 9 :k 1} [1 2 3])` 在 cleat 上是 `{:k 1}`，JVM 是 `{:line 9, :k 1}`。原因是 `TransferSource`
   把宏调用的 `:line` / `:column` 也带给宏展开出的向量，于是 `AnalyzeDatum` 只好把所有位置键都剥掉，用户自己写的也一并没了。现在位置只带给列表
   （JVM 的 reader 也只给列表记位置；`:tag` 照旧带给任何 IObj，同 JVM 的 preserveTag），集合字面量的元数据原样保留；`fn*` / `reify` 仍按 JVM 的 FnExpr 去掉位置键。
3. **包里的泛型类型常量**：常量表按 `Type.FullName` 写类型，封闭泛型的实参会带上程序集名——编包时包里 deftype 的程序集是临时的 eval 单元（`eval$s104`），
   于是从包加载 core.async 时 `SortedSet<TimeoutQueueEntry>` 找不到（TypeInitializationException）。`ConstantEncoder.TypeName` 改为按 ClojureCLR 语法、
   实参递归、不带程序集名写（`SortedSet`1[clojure.core.async.impl.timers.TimeoutQueueEntry]`），fasl 读回时 TypeResolver 解析。新增单元测试
   `ConstantEncoderTests.GenericTypeConstantsOmitAssemblyNames`。

4. **浮点数的文本读不回来**（全量测试里 test.check 的 `edn-roundtrips` 属性测试随机撞到，缩小到 `#{2.980232238769531E-8}`）：.NET 的最短往返格式（"R"）
   在 2 的幂处不可靠——下方的间距只有上方的一半，它给出的 16 位文本解析回来是下方相邻的 double（2^-25 的位模式 …008 读回 …007）。
   `JvmText` 的浮点格式化在读回不等时退到 17 位有效数字，与 JVM 的 `Double.toString` 相同（`2.9802322387695312E-8`）。新增 `JvmTextTests`：
   全部 2 的幂与 20 万个随机位模式逐个往返。

### 3. 已知缺口（记账，不在本任务做）

- **by-ref 实参**（`(by-ref x)`，CLR 的 ref / out 形参）：C# 编译器报 "by-ref arguments are not supported yet"。ClojureCLR 的库里常见，这次两处都绕开了。
- go 块里的 `proxy`（上）。

### 4. 门禁

Release 构建 0 error；Core 116 / 116、Compiler 50 / 50；Host 全量 **289 / 289**（15 m 31 s；第一轮 288 / 289 的失败是上面第 2 节第 4 点的浮点往返，修后复跑全过）；`image-set --twice --ilverify` 通过（第 1 节）。
