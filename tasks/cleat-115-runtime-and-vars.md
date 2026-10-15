# cleat-115 运行时与 Var 访问：Numbers 的 object 快路径、$Vars 的 static readonly 字段、Symbol 密封

- 里程碑：M11　依赖：cleat-114　状态：完成（2026-10-16）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. `Numbers` 的 object 版本（add / minus / multiply / inc / dec / lt / lte / gt / gte / equiv）先判 long / double（cpstak 的 perf：Numbers.ops 6.4%、ConvertToLong 4.7%），
   结果与原路径相同（long 溢出照常抛，混合按 double；double 减 long 按 JVM 的 x + negate(y)，(- -0.0 0) 为 0.0）。
2. eval 单元里 Var 的读写经 `$Vars` 的 static readonly 字段（类型初始化器从常量表取；JVM 的 fn 类同样把 Var 放在 static final 字段里）：
   没有常量表的数组读、边界检查与 castclass，Tier1 的 JIT 在类型初始化之后当常量。镜像与包照旧（字节不变）。
3. `clojure.lang.Symbol` 改为 sealed（没有子类）：`symbol?` 的类型检查内联成一次方法表比较（scheme 的 perf：eq? 里 IsInstanceOfClass）。

## 验收标准
- 语义：`tests/Cleat.Host.Tests/data/arith_semantics.cljc` 的输出与 JVM Clojure 逐行相同；全量、门禁、基线不变；种子不动点。
- r7rs 受影响项前后数字。

## 结果（2026-10-16）
- 语义：`ClojureCompilerTests.ObjectArithmeticMatchesJvm`（data/arith_semantics.cljc，132 行：long / double / 混合 / BigInt / Ratio / 溢出 / NaN / ±0.0 的
  + - * inc dec < <= > >= ==）输出与 JVM Clojure 1.12 逐行相同。
- 与 cleat-116 一起提交（同一批文件交错修改，在 linux01 上一起验证）；r7rs 的数字见 cleat-116。
