# cleat-189 `clojure.lang.Compiler` 门面（最小集）

- 里程碑：M23　依赖：无　状态：完成（2026-11-10）

## 问题

cleat 的编译器是 C#（`Cleat.Compiler`），没有 `clojure.lang.Compiler` 这个类。test_clojure 有 7 个命名空间因此整个加载不了
（compilation、control、errors、evaluation、ns-libs、param-tags、special 报 `Unable to resolve classname: clojure.lang.Compiler`；api、clearing 是别的类，见 190 / 192），
ClojureCLR 的库直接调编译器内部的也要改写（README"已知限制"）。

test_clojure 用到的成员：`Compiler$CompilerException`（32 处）、`Compiler/munge`、`demunge`、`CHAR_MAP`、`load`、`eval`。
上游库（clr.* 移植时看到的）还用：`Compiler/LOADER`、`SOURCE`、`SOURCE_PATH`、`METHOD`、`LINE_BEFORE` / `COLUMN_BEFORE` 等动态 Var，
以及 `Compiler.analyze` / `analyzeSeq` / `$LocalBinding` / `$ObjExpr` 等分析器内部（这一类不做，记账）。

## 工作内容

1. 在 Cleat.Core 加 `clojure.lang.Compiler`（静态类）：
   - `Compiler$CompilerException`：cleat 现有的编译错误（`ExpanderException` 等）与它的关系——是让 cleat 抛出的编译错误就是它（或其子类），
     还是 catch 时认两者；`ex-data` / 消息 / `:clojure.error/*` 与 JVM 一致（对照 errors 测试）。
   - `munge` / `demunge` / `CHAR_MAP`：转到 `clojure.lang.Munge`。
   - `eval` / `load` / `macroexpand1`：转到 cleat 的驱动（按需加载编译器）。
   - 动态 Var：`LOADER`、`SOURCE`、`SOURCE_PATH`、`LINE`、`COLUMN` 等映射到 cleat 已有的对应物（`*file*`、`*source-path*` …），没有对应物的不提供。
2. 分析器内部（`analyze`、`$LocalBinding`、`$ObjExpr`、`$FnExpr` …）不提供：在 designs 与 README 写明，并给移植时的替代写法。
3. test_clojure 里因 Compiler 加载不了的命名空间重跑，失败逐项分诊（修 / 记偏离表），基线更新。

## 验收

- 上述 7 个命名空间能加载；新出现的失败都有处置；Host / conformance / CTS / test_clojure 门禁通过；README"已知限制"同步。

## 结果

**门面**（`src/Cleat.Core/Runtime/Compiler.cs`，设计记在 [designs/03 §4.2](../designs/03-language-core.md)）：

- `Compiler+CompilerException`：原 `ExpanderException` 的内容（位置、phase、symbol、`getData`、消息格式）上移到它，`ExpanderException` 成为它的子类，
  分析器照旧抛 `ExpanderException`，`catch Compiler+CompilerException` 都接得住；诊断（`Diagnostics.Stamp` / `FormatError`）按基类认，用户自己 `new` 的也按语法错误报告。
  另有 JVM 的 `(source, line, column, cause)` 构造、`source` / `line` 属性与 `PHASE_*` 关键字。
- `munge` / `demunge` / `CHAR_MAP` → `Munge`；`eval`、`load`（1 ~ 4 参）、`loadFile`、`macroexpand1`、`macroexpand` → 运行时钩子 / `Loader`；`specials`；
  `SOURCE` / `SOURCE_PATH` / `SourceVar` / `SourcePathVar` / `COMPILE_FILES` / `COMPILE_PATH` / `COMPILER_OPTIONS` 就是对应的 Var。
- 不提供：分析器内部、`LOADER` / `METHOD` / `LINE` 等编译器状态；执行期异常不包装成 CompilerException。替代写法在 designs/03 §4.2 与 README"已知限制"。

**7 个命名空间**：control、errors、evaluation、ns-libs、special 加载并运行；compilation 还要 test.generative 的 `defspec`、param-tags 还要 ClojureCLR 测试支持程序集里的
`clojure.test.SwissArmy`，两个移入跳过表（原因写明，交 [cleat-191](cleat-191-test-libs-and-missing-nses.md)）。

**新出现的失败逐项处置**：

| 命名空间 | 失败 | 处置 |
|---|---|---|
| errors | `(macroexpand '(m0 1))` 等 4 个错误：cleat 把宏的元数错误包成编译错误 | 修：直接调 macroexpand / macroexpand-1（钩子）时原样抛 `ArityException`（宏自己的元数减 2，宏体里别的元数错误原样），编译途中不变 |
| errors | 3 个：消息要 `No matching member … taking n args` | 修：`Reflector` 两条找不到成员的消息改为 `No matching member m taking n args found for type T: …`（保留候选列表） |
| special | `(quote 1 2 3)` 的原因要有 `:form` | 修：原因是 `ExceptionInfo {:form …}`（JVM 的 ConstantExpr.Parser） |
| control | `(BigDecimal/Create (case 1 1 1))` 反射 | 修：分析器的形式类型认 `clojure.core/case`：各结果（与默认）静态类型相同即为它 |
| control | int 测试 `case` 的性能警告没有 | 修：`Local.InitType` 记 let 初值的静态类型（只在 `*warn-on-reflection*` 时算），case* 的被测局部初值已知不是原始类型时报（类型未知不报，避免误报）；顺带 let 初值是局部时沿用其类型提示 |
| special | 18 个 "did not conform to spec" | 偏离（cleat-166 的决定）：宏 spec 检查只在加载 `clojure.core.specs.alpha` 后做，不自动加载；先 require 则 special 39 个断言全过 |

**基线**：test_clojure 54 → 52 个运行（跳过 31 → 33）、加载不了 12 → 5（余 api → 190；array-symbols、clearing、compilation2、method-thunks → 192），
测试 384 → 439，断言通过 3877 → 4223、失败 25 → 43（+18 全是 special 的 spec）、错误 4 → 4。CTS 不变（6868 / 29 / 6）。

**测试**：`ClojureCompilerTests.CompilerFacade`（`tests/Cleat.Host.Tests/data/compiler_facade.clj`）；`ReflectorTests` 跟着新消息。

## 门禁（2026-11-10，13）

- Core 116 / Compiler 50 / Host 294 全过（Host 16 分 10 秒）
- image-set --twice --ilverify：全部通过
- conformance --gate：521 分类偏离 / 521 失败行，OK
- CTS：6868 / 29 / 6，基线 OK
- test_clojure：52 个命名空间（33 跳过、5 加载不了），439 测试，4223 / 43 / 4，基线（本任务重写）OK
