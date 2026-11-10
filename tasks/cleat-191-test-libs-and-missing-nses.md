# cleat-191 测试用库与缺失的命名空间：data.generators、test.generative、clojure.xml、clojure.clr.process

- 里程碑：M23　依赖：无（与 189 / 190 并列）　状态：完成（2026-11-10）

## 问题

test_clojure 跳过的 31 个命名空间里，能补上的有：
- 7 个要 `clojure.test.generative` / `clojure.data.generators`（外部库，cleat-002 起一直跳过）：data-structures、edn、clr.added 等；
- 4 个要 `clojure.test.check`——cleat-166 已移植成包，跳过原因已失效；
- `clojure-xml`：`clojure.xml` 不在 cleat 的 src/clojure；
- `clr.process`：`clojure.clr.process` 不在 src/clojure。

## 工作内容

1. 把 ClojureCLR 的 data.generators、test.generative 移植成 packages/ 下的包（照 cleat-166 的做法：上游测试源码与包程序集各跑一遍）。
2. `clojure.xml`、`clojure.clr.process` 加进 src/clojure（随附库或镜像，按 boot-order 的规则定），对照 ClojureCLR 的实现。
3. test_clojure 的 cts 运行器能把这些包加进加载路径；原先因此跳过的 13 个命名空间改为运行，失败逐项分诊，基线更新。

## 验收

- 跳过表里只剩确实做不到的（helper 命名空间、ClojureCLR 编译好的测试支撑程序集）；新增包的上游测试全过；门禁通过。

## 补记（cleat-189 之后）

- compilation 的 `clojure.lang.Compiler` 已有（189），只差 test.generative 的 `defspec`：本任务移植 test.generative 后从跳过表拿出来。
- param-tags 同样只差 ClojureCLR 测试支持程序集里的 `clojure.test.SwissArmy`（attributes、clr.interop、try-catch 也要支持程序集）：
  可考虑把 ClojureCLR `Clojure.Tests.Support` 的 C# 源码移成 tests/ 下的一个测试支持项目，一并解开这 4 个。
- compilation2（cleat-192 移入跳过）除 test.generative 外还要 ClojureCLR 的 AOT 文件名（`assembly-load-from "<ns>.clj.dll"`），移植 test.generative 也解不开，维持跳过。
- api（cleat-190 移入跳过）：`clojure.clr.api.Clojure` 已有，只差 test.generative 与 `clojure.test-clojure.generators`。

## 结果

**包**（照 cleat-166 的做法，源码未改动，上游测试源码与包程序集各跑一遍：`PackageTests.DataGeneratorsAndTestGenerative`）：
- `packages/data.generators`（clr.data.generators 1.1.0，包程序集 `Cleat.DataGenerators`）：上游 3 个测试 / 100 个断言全过。
- `packages/test.generative`（clr.test.generative 1.1.2，`Cleat.TestGenerative`，依赖 data.generators 与 tools.namespace）：上游的测试是 defspec，
  `test/run.clj` 用上游 runner 的 `run-suite`（2 线程、按时间），22 个 spec 全过。
- 编包时发现 **M20 的 C# 常量编码器漏了 `#inst` / `#uuid`**（data.generators 有 `#inst` 字面量，编不成包程序集）：补上（Fasl 一直能读）。

**命名空间**：
- `clojure.clr.process`：取 ClojureCLR 1.12 的源码进 src/clojure（boot-order `:boot false`，随附库、按需 require，同 JVM 的 clojure.java.process）。
  修上游三处（`;;; CLEAT-191`）：实参原来用空格拼成 `Arguments`（`(exec "bash" "-c" "ls >&2")` 实际跑 `bash -c ls ">&2"`）→ 逐个进 `ArgumentList`；
  `:dir` 原来不生效 → `WorkingDirectory`；`exec` 缺省收 stdout（原来留着 JVM 的 `{:err :inherit}`，不加 `{:redirect-out true}` 就抛）。`ClojureCompilerTests.ClrProcess`。
- `clojure.xml`：**不加**。ClojureCLR 没有 clojure.xml（test_clojure 的 clojure_xml.clj 连 ns 形式都注释掉了，没有可跑的测试），没有对照实现；
  JVM 的版本基于 SAX。跳过原因改写为"上游无有效测试"。需要时另立任务（基于 System.Xml.XmlReader）。

**test_clojure 运行器**（tools/cts.clj）：加载路径加上 packages/ 的 test.generative、data.generators、tools.namespace、test.check、spec.alpha；
test.generative 的 defspec 不是 clojure.test 测试，`test-ns` 不跑——运行器对带 `:clojure.test.generative/arg-fns` 的 Var 按固定种子
（`data.generators/*rnd*` = Random(42)）各跑 100 次，每个 spec 记一个测试 / 断言（计数可重复，三次运行一致）。api、edn、data-structures 等的 26 个 spec 全过。

**从跳过表拿出的 14 个**：generators、data-structures、edn、numbers、reader、reducers、clr.added、api、sequences、data-structures-interop、parse、transducers、clr.process
（与 compilation——见下）。新出现的失败逐项处置：

| 命名空间 | 失败 | 处置 |
|---|---|---|
| data-structures | `(= (sorted-set-by compare #{}) #{#{}})` 为假：两个 `#{}` 不是同一对象，比较集合抛异常 | 修：空的向量 / 集合字面量（含嵌套常量）是共享的 `EMPTY`（JVM 的 EmptyExpr / emitValue）：分析器 `ConstValue` 与 Fasl 解码都按此 |
| clr.process | `test-stderr-redirect` | 修：见上（ArgumentList） |
| clr.process | `test-process-deref` 3 个错误：启动 `powershell.exe` | 偏离：测试只在 Windows 上成立 |
| numbers | `(+ "ab" "cd")` 期望 FormatException，cleat 抛 InvalidCastException | 偏离（designs/03 §4.1：非数值操作数照 JVM 的 ClassCastException） |
| numbers | `warn-on-boxed` 2 个：`*unchecked-math* :warn-on-boxed` 的装箱警告 | 未实现，交 cleat-193 |
| reader | 9 个：`:line` / `:column` 元数据期望 Int32 | 偏离（整数装箱为 Int64，偏离表 integer-box-model） |
| sequences | `test-iteration`：打开 Windows 路径 `clojure\edn.clj` | 偏离：测试自身的路径写法 |
| compilation | 加载不了：`test-fnexpr-type-hint` 引用 ClojureCLR 的 DLR 类型 `Microsoft.Scripting.ArgumentTypeException` | 跳过（test_clojure 保持原样，不改测试文件），原因写明 |

另外：`(inc UInt32/MaxValue)` 一类在 cleat-192 之后（静态字段有了类型）出现反射——Numbers.inc 的 long / ulong 重载之间选不出；按 C# 规范的"更好的转换目标"
（有符号整型优于无符号）补上（data.generators 的 `uint` / `ushort` 不再反射）。

**基线**：test_clojure 运行 49 → 62 个（跳过 36 → 23，全部能加载），测试 445 → 709，断言通过 4282 → 18190、失败 43 → 54、错误 4 → 9（新增的全部见上表）。
CTS、conformance 不变。跳过表只剩：辅助命名空间 / 驱动脚本（15）、ClojureCLR 测试支撑程序集（attributes、clr.interop、try-catch、param-tags）、
上游无有效测试（clojure-xml、clearing）、ClojureCLR 专有（compilation 的 DLR 类型、compilation2 的 AOT 文件名）、repl.deps（clojure.clr.basis）。

**测试**：`PackageTests.DataGeneratorsAndTestGenerative`、`ClojureCompilerTests.PackageConstantsInstUuidAndEmptyCollections`、`ClojureCompilerTests.ClrProcess`。

## 门禁（2026-11-10，13）

- Core 116 / Compiler 50 / Host 299 全过（Host 18 分 9 秒，含 DataGeneratorsAndTestGenerative 源码 + 包程序集两遍）
- image-set --twice --ilverify：全部通过（空集合常量改动后镜像仍两次逐字节相同）
- conformance --gate：521 / 521，OK
- CTS：6868 / 29 / 6，基线 OK
- test_clojure：62 个命名空间（23 跳过、0 加载不了），709 测试，18190 / 54 / 9，基线（本任务重写）OK
