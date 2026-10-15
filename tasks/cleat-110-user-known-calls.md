# cleat-110 用户函数的已知调用：经 Var 调用、分析时 Var 的值是有 invokeStatic 的函数类时，isinst 守卫后直接 call（cleat-107 原因 7 的一部分）

- 里程碑：M11　依赖：cleat-109　状态：完成（2026-10-16）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 依据：经 Var 调用每次 castclass IFn、接口分派、invoke 转发到 invokeStatic；对照实验直接 call 约省 6%（装箱另计）。boot 镜像里的核心函数已有这条路（接口文件的已知函数）。
2. 分析器：Var 的当前值是 AFunction 子类实例、有参数个数相符的静态 invokeStatic（非变长子句）、类型可按名字引用（已加载的程序集，含先前求值生成的）时，
   生成 `(if (IsInstanceOf 类 r) (静态调用 类 invokeStatic r 实参…) (clj-invoke r 实参…))`；重定义照常生效（守卫）。
3. 与 cleat-104 的类型化入口衔接：调用点实参类型已知时可直调 invokeStaticTyped（或留给 cleat-114）。

## 验收标准
- browse、takl、nqueens、deriv 等前后数字；语义开 / 关对照（含重定义、with-redefs）。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。

## 结果（2026-10-16）
- 分析器：非动态 Var、当前值是 AFunction 子类且有参数个数相符的公开静态 invokeStatic（没有闭包字段的固定子句）、类型可按名字引用时，生成
  `(clj-call "KnownCall" #f '类 Var读 实参…)`；codegen 在内存模式（或类来自文件加载的程序集）时生成 `isinst 类 → call 类.invokeStatic(f, 实参…)`，
  否则 IFn.invoke；临时变量按局部变量清除的规则读（长惰性序列作实参不被抓住）。编成包时不用（求值期间生成的类运行时不存在）。开关 `CLEAT_NO_OPT=known`。
  驱动的简单形式解释器把 KnownCall 当作 IFn 调用（否则 `(f 21)` 这类形式不再解释、改为编译）。
- `Util.identical`：先比引用，再对 long / double / bool / char 按类型比，最后才判值类型（原来先 `k1 is ValueType`，对引用类型要沿继承链走；
  中间一版用 GetType().IsValueType 更慢——两次真正的调用），强制内联。
- perf：browse 里 ChkCastInterface 24.7% → 8%（剩下的是 `mcar` 体里对 IMPair 接口的转换）；browse 12.8 → 10.3 s。
- 语义：`data/knowncall_semantics.clj`（重定义成别的函数 / 变长 / 多参数个数 / 关键字 / map、with-redefs、动态 Var、闭包、抛异常、求值顺序、
  300 万元素的惰性序列在 256 MB 堆下、跨命名空间与其重定义）开 / 关逐字相同（`ClojureCompilerTests.KnownCallsToUserFunctions`）。
- 验证（linux01，与 110 ~ 113 一起，一次完整流程）：种子不动点；Core 117 / Host 263 通过（1 个按环境跳过）；语料门禁 521 行；两套基线不变；
  bench 在噪声内（lazy_chain 48 → 16 ms）。r7rs 56 项 cleat 侧（各一次，对 JVM）：几何平均 3.74（109 后）→ 3.51（110 ~ 112 第一部分）→ **3.26**（含 112 第二部分与 113）。
- 这几个任务的改动在 codegen.clj、Util.cs 等文件里交织，按任务拆分要多跑几轮验证，所以一起验证、一个提交（与"每个任务一个提交"不同，有意为之）。
