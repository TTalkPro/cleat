# cleat-116 eval 的小函数内联与运行时快路径

- 里程碑：M11　依赖：cleat-115　状态：完成（2026-10-16）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 背景
从源码加载（eval）的函数没有接口文件，调用点只有 cleat-110 的已知调用，小的访问函数（fft 的 `vector-ref` 一类、mcar / mcdr、比较函数）每次都是一次跨程序集调用；
JIT 不跨 eval 程序集内联。剖析另外找到几处与内联无关的热点：循环后装箱 double、`RT.first` 对 Cons 的类型检查、`rem` / `quot` 的通用分派。

## 工作内容
1. eval 的顶层 `(def v (fn* …))`（非动态、非宏、类在本单元里）求值后登记已知函数与可内联子句（`InterfaceFile.RegisterEntry`，与接口文件里的同样；
   重定义时覆盖，调用点有类型守卫）。只用于内联：直接链接（`AotPaths.DirectLinkTarget`）只认有接口文件的；构建镜像 / 包时不登记
   （产物里的调用点不能链接到 eval 程序集的类，seed 第一次就因此失败）。
2. 内联的限制（语义）：只在 fn 体里内联（顶层形式只运行一次，留给简单解释，调用帧照旧）；实参是 `##NaN` 字面量时不内联（形参本是对象，
   同一个常量对象 `(= a b)` 为真，内联后成了原始 double 的比较）；有原始签名（`^long` / `^double` 形参或返回）的子句不内联（子句体里没有入口与返回的转换）。
   clojure-test-suite 的 `not=` / `case` 里各有一个 NaN 断言因此转为通过（基线随之更新）。
3. double 装箱经不内联的 `LongBox.Double`：内联的 `box` 先调用分配再存值，值跨过调用存活，SysV 上 xmm 寄存器都由调用方保存，RyuJIT 于是把整个循环里的
   double 变量放在栈槽上（每次迭代一次存取）。C# 复现：同一循环 911 → 226 ms；long 不受影响（有被调用方保存的通用寄存器）。开关 `CLEAT_NO_OPT=boxdouble`。
4. `RT.first` / `next` / `more` 先判 sealed 的 `Cons`、精确类型的 `PersistentList`（一次方法表比较；ASeq 不是 sealed，isinst 要走 CastHelpers，nboyer 里 8.7%）；
   `PersistentList.first` / `next` 改为 sealed override。
5. 分层编译的标记只看 lambda 自己的体：-main 里嵌套一个带循环的闭包时，-main 本身不再被标为分层。
6. eval 单元里的受保护直接链接与已知调用一样，结果经局部变量汇合（cleat-114 的 tier-0 防护）。

## 结果（2026-10-16，linux01，cleat 侧各一次，JVM 用 cleat-107 的最小值）
- r7rs 56 项：**几何平均 2.64 → 2.29**。sumfp 1.13 → 0.24 s（JVM 0.22）、fft 4.65 → 1.45、ray 4.72 → 2.70、lattice 12.5 → 8.4（JVM 0.83 倍）、
  nqueens 18.9 → 15.0（1.05 倍）、nucleic -32%、sboyer -27%、simplex -23%、primes -22%、conform -21%、mazefun -19%、destruc -19%、matrix -17%；
  快于 JVM 的有 ack 0.27、cat 0.30、fib 0.56、lattice 0.83、mazefun 0.88、pi 0.93。
- 回退：array1 14.3 → 19.8 s（+38%）。my-try 内联进 go 后，go（只调用一次、500 次的重循环、分层编译）一直停在插桩的 tier-0：调用实参经过的 JIT 临时变量
  把上一轮的两个百万元素数组（与其中的装箱整数）多抓一轮，GC 次数更少但每次更贵（本地 100 轮 3.7 → 6.3 s，`DOTNET_TieredCompilation=0` 时没有回退）；
  go 的 OSR 补丁点在，但调低 `OSR_*` 阈值也不切换（JIT 没给原因）。另 peval +7%、tail +18%、slatex +9%（单次，I/O / 噪声级）。
- 实验（未采用）：Server GC（nboyer -28%，deriv 慢一倍、ray / destruc 变慢）；`GCgen0size`（array1 -25%，其余 ±5%）；放宽内联上限到 400 序对
  （JIT 的内联预算用尽，mbrotZ 不变）；AFn 虚调用代替 IFn 接口调用（C# 复现：接口调用反而快，3.3 vs 3.7 ns）。
  boot 镜像也分层（去掉 AggressiveOptimization）：14 项平均约快 3%，启动快约 1 s——要把 tier-0 防护推广到镜像，转入 cleat-117。
- 剖析结论（libcoreclr 的调试符号，perf 地址要加 0x1000）：分配密集的项里 GC（mark / plan / relocate）占 20% ~ 67%（array1 67%、nboyer 约 40%），
  分配快速路径 `RhpNewFast` 在 Linux 上经 `__tls_get_addr` 取线程分配上下文（cpstak 里两者约 15%）；string 是 UTF-16 字符串的搬运（JVM 有 Latin-1 紧凑字符串）。
- 测试：`ObjectArithmeticMatchesJvm`、`KnownCallsToUserFunctions` 加 `CLEAT_NO_INLINE=1` 对照；`BoxCacheAndNumericFastPaths`、`ClojureDriverEvalsLoadsAndCompiles`、
  `StackTracesHideCompilerFramesUnlessAskedFor`、`TieredEvalKeepsLazySeqsCollectable` 覆盖上面的限制。
- 验证（linux01）：种子不动点；Core 117 / Host 265 通过（1 个按环境跳过）；门禁 521；test-clojure 基线不变；cts 基线 +2 通过（上面的 NaN 断言）；
  bench/ 不变（关内联时 nested_arith 1 → 24 ms、untyped_loop 3 → 15 ms，与 cleat-110 时相同）。
