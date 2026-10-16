# cleat-117 镜像与包分层编译、tier-0 生命周期

- 里程碑：M11　依赖：cleat-116　状态：完成（2026-10-16）
- 设计依据：[21](../designs/21-r7rs-gaps.md)、[22 §1](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 镜像与包里的函数类按 cleat-114 的规则分层（`(attrs tiered)`），获得动态 PGO；入口、转发与桥方法保持优化编译。
   改在 codegen 的 attrs 上做（旧种子的 Lcil 没有 tiered，stage1 字节不变，一步重建种子）；开关 `CLEAT_NO_OPT=imagetier`。
2. tier-0 的 GC 报告防护推广到镜像 / 包：值汇合走局部变量、只写一次读一次的 object 临时变量读后清空、循环实参与 `^:once` 闭包字段按最后使用释放。
3. `RT.nth`、序列遍历一类长运行 runtime 路径用 `ref` 交出调用方槽位，避免遍历期间抓着惰性序列头。
4. 补核心函数（`dorun`、`last`、`nth`、`count`、`reduce` 一类）只调用一次时不抓惰性序列头的测试。
5. 本项只修生命周期与分层，不解决 `array1/go` 的 OSR 未触发问题；该诊断转 cleat-120。

## 实现记录（2026-10-16）
- 分层规则收紧（否则核心库只调用一次的函数在 tier-0 里抓住实参）：
  - 自递归 = 某个子句经自己的 Var **以同样的参数个数调用**自己（可在嵌套 lambda 里，如 map 的 lazy-seq）；只引用 Var 不算
    （defprotocol 的分派函数读自己 Var 上的缓存），arity 之间的委托不算（transduce 的 3 参调 4 参）；
  - letfn / 命名 fn 要**调用**同组绑定才算（分派函数 `G__` 把自己传给缓存函数只是引用）；
  - 转发方法与桥方法不加 tiered（tier-0 把转发实参的 JIT 临时变量报告为活，被调用的归约运行期间抓着实参）。
- 局部变量清除按方法区分（`*tiered-method*`）：分层方法经 `ldloca` + `Locals.Take`、值汇合经局部变量；其余方法 `ldloc; ldnull; stloc`
  （Take 让局部变量地址暴露，大方法里 JIT 内联预算用尽时它还是一次真正的调用）。
- 分层方法里尾位置的循环：入口 jump 读完实参后，循环体不再引用的 object 形参（含 Num 形参的副本，Num 的 Tag 也持有对象）置空。
- `(fn* ^:once [] …)`（lazy-seq、delay 的展开，JVM 的 FnExpr.onceOnly）：闭包字段在最后一次使用处清空。cleat 此前没有实现，
  `(first (drop n (map …)))` 抓头。
- 没有回跳的标签（每个子句都有的 arity 标签，没有 recur）不再"钉住"体外变量，它们照常按最后使用清除。
- 运行时：`NthFrom` 先让序列（LazySeq / ISeq 也实现 IList，IList 的索引器抓着头）走 Sequential 分支；`RT.nth` 先判精确类型
  `PersistentVector`、内联，非 Indexed 时经 `ref` 交给 `AggressiveOptimization` 的慢路径。
- 简单解释只接受原子实参（常量、局部、Var、以及实参都是原子的运行时操作）：解释器把实参值放在向量里直到调用返回，
  顶层 `(println (nth (map inc (range 3e7)) n))` 会抓头；嵌套的用户函数调用交给编译。
- 运行时快路径：`rem` / `quot` 的两个 long、`RT.longCast` / `intCast` 的 long 快路径内联（慢路径拆出，puzzle 约 -6%）。
- `clojure.core/not` 加 `:inline`（展开成 `if`）：守卫式内联让函数体不"纯"，数值递归函数拿不到类型化入口；本地 tak 3.7 → 1.9 s。
- 试过并撤回：顶层 defn 的自调用按类守卫直调（Tier1 的 PGO 已把接口调用去虚化，tak 无收益；种子多出约 3400 处守卫）。
- 测试：`ClojureCompilerTests.CoreFunctionsKeepLazySeqsCollectable`（data/lazy_head_core.clj，18 种写法，512 MB 堆）；
  `ClojureDriverEvalsLoadsAndCompiles`、`SimpleFormsAreNotCompiled` 的计数不变。

## 中途数字（linux01，2026-10-16，不含 `not` 内联与后续改动，cleat 各一次，JVM 为 GraalVM CE）
几何平均 2.29 → 2.27（triangl -25%、primes -20%、bv2string -15%、puzzle -14%；ack +30%、slatex +16%、paraffins +11%、cpstak +7%）；
Host 两项计数测试失败（已修）；门禁 521、两套基线不变。linux01 之后不可用，后续验证在 david@192.168.3.111 上做。

## 结果（2026-10-16，david@192.168.3.111：4 vCPU / 15 GB，.NET 运行时 10.0.0）
- 种子不动点（2086 KiB，与本机生成的种子逐字节相同）；门禁 521 行 OK；cts（补上子模块内容后）与 test-clojure 两套基线不变；bench/ 正常。
- Core 117 通过；Host 263 通过 / 1 按环境跳过 / 3 失败：`ClojureTestSuiteMatchesItsBaseline`（该机缺 clojure-test-suite 子模块内容，补上后通过）、
  `ConsoleExampleQueriesCsvWithSqlite` 与 `ToolsBuild`（测试的隔离 NuGet 源只有 local-feed，该机的 NuGet 缓存里没有 Microsoft.Data.Sqlite 等包）；
  后两项在本机通过。新增 `CoreFunctionsKeepLazySeqsCollectable` 通过。
- r7rs 56 项（各一次，见 [notes/r7rs-performance.md](../notes/r7rs-performance.md) 末节）：对 OpenJDK 25.0.4（HotSpot C2）几何平均 **1.85x**，
  对 GraalVM CE 25.0.2 **1.99x**（GraalVM 对 C2 0.93）。对 C2 比 JVM 快的：sum 0.11、sumfp 0.38、ack 0.38、ntakl 0.45、lattice 0.55、fibfp 0.69、
  pi 0.85、fib 0.95；tak 1.05x（`not` 内联后有类型化入口）。与 linux01 的 2.27x 不可直接比较（机器与 JVM 都不同）。

## 验收标准
- 512 MB 堆下新增惰性序列用例不 OOM，输出与关掉分层时逐字相同。
- boot 镜像与包的启动、r7rs 14 项对照；镜像字节变化后种子达到不动点。
- 基线、门禁、全量不变。
