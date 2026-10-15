# cleat-114 eval 代码分层编译与动态 PGO

- 里程碑：M11　依赖：cleat-113　状态：完成（2026-10-16）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 背景
perf 与 JIT 反汇编：eval（从源码加载）的代码由 `AssemblyBuilder.RunAndCollect` 发射，可回收的动态程序集不参与分层编译——没有动态 PGO
（有守卫的去虚拟化、热路径布局，JVM 内联缓存的对应物），静态字段访问还多一层间接；生成的方法另带 `AggressiveOptimization`。
同一份基准编成包、从文件加载（能分层）时：nqueens 33.6 → 22.3 s、browse 13.4 → 8.4 s。

## 工作内容
1. eval 单元（eval$cN）发射成 PE 字节（PersistedAssemblyBuilder），装进专用的不可回收 AssemblyLoadContext（依赖先取进程里已装载的同名程序集）；
   活常量照旧经 ConstantPool。`CLEAT_NO_OPT=tiered` 回到 RunAndCollect。镜像与包照旧（字节不变：去掉 AggressiveOptimization 会改变镜像 id）。
2. tier-0 的 GC 报告：tier-0 把局部变量与 JIT 临时变量都报告为整个方法内活着，只调用一次的方法把惰性序列交给长时间运行的被调用者时抓住头
   （512 MB 堆下 `(defn once [] (walk (map inc (range 3e7))))` OutOfMemory）。调用实参在 tier-0 里必然经过 JIT 临时变量，IL 层面清不掉，所以：
   - 只有会被反复调用或长时间循环的函数类分层编译（`(attrs tiered)`）：体里有真正循环的 fn、引用自己 Var 的 defn、letfn 里引用同组绑定的 fn；
     其余（-main 一类只调用一次的）与模块入口（顶层形式）照旧直接按优化编译；有循环的经 OSR 进入优化代码；
   - eval 单元里值上下文的 if 与已知调用的两支经局部变量汇合（不让值留在栈上跨块），只写一次读一次的 object 临时变量读后清空。
3. 跟随的修正：类型名对从字节装载的程序集也带程序集名（重定义后同名的类在不同的程序集里）；静态决议的"Clojure 定义的类型"判断同样认这种程序集。

## 结果（2026-10-16）
- 实验：全部方法分层时 nqueens 38.5 → 27.6 s、puzzle 15.3 → 9.5、tak 6.2 → 3.8，但 512 MB 堆下只调用一次的函数抓住惰性序列（OutOfMemory）；
  关掉 quick JIT（`DOTNET_TC_QuickJit=0`）安全但收益全无（没有 tier-0 插桩就没有 PGO）；`JitMinOptsTrackGCrefs` 在正式版运行时不起作用；
  只给顶层 defn（不含 letfn）分层只拿回一部分；最终的规则（体里有循环的任何 fn、自递归的 defn、letfn 里的局部递归）几乎拿回全部收益。
- 镜像 id 随 dll 字节：去掉镜像与包里的 AggressiveOptimization 会让从种子发射的 stage1 镜像 id 改变，种子里编译器包的核对失败——镜像与包暂不改。
- 跟随的修正：从字节装载的程序集 `IsDynamic` 为假，类型名不带程序集名时重定义后同名的类会解析错（`HostSyntax.TypeName` 改为看没有文件位置）；
  "Clojure 定义的类型的方法不经 RetObject"的判断同样（否则 browse 回退 13%）。
- r7rs 56 项 cleat 侧（linux01，各一次）：**几何平均 3.26 → 2.64**；puzzle 14.1 → 6.7 s、quicksort 8.5 → 4.3、triangl 7.3 → 3.9、destruc 11.5 → 6.4、
  scheme 23.2 → 14.2、peval 20.7 → 14.7、nqueens 25.8 → 18.9、ack 3.6 → 2.0（JVM 0.26 倍）、cat 0.34、fib 0.64；只有 wc +14%、read1 +7%（I/O 类，噪声级）。
- 测试：`ClojureCompilerTests.TieredEvalKeepsLazySeqsCollectable`（512 MB 堆下 3000 万元素的惰性序列：顶层、只调用一次的四种写法、-main，
  输出与 `CLEAT_NO_OPT=tiered` 相同）。验证（linux01）：种子不动点；Core 117 / Host 264 通过（1 个按环境跳过）；门禁 521；两套基线不变。
