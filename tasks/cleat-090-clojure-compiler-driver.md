# cleat-090 Clojure 写的编译驱动与切换

- 里程碑：M8（阶段 B）　依赖：cleat-089　状态：完成
- 设计依据：[19 §1 阶段 B、D5 G3](../designs/19-clojure-self-hosting.md)

## 目标
`CLEAT_COMPILER=clojure` 时运行时的 eval / load / compile / `--compile-boot` / `--compile-package` 全部走 Clojure 编译器，全量测试通过。

## 工作内容
1. 驱动：`compile-forms->lcil`、eval（简单形式解释 → 发射到内存）、load 钩子、`*compile-files*` 与包程序集、`--compile-boot` 按 `boot-order.edn` 编 `cleat.boot.dll`。
2. 宿主接入：钩子登记不再经 `ClojureHost` 的 Scheme 程序；Scheme 镜像在此模式下不加载。
3. G3：全量测试、语料 / cts / test_clojure 基线、错误黄金文件、`SchemeInvisibilityTests`、包测试、示例、基准（`tools/build.cs bench`）；记录编 boot 镜像、REPL 单次 eval 的耗时并与 Scheme 编译器对比。

## 从 cleat-086 移来的
- 小函数内联（`.inl`）：Clojure 编译器写自己的 `.inl`（L0 的数据），调用点照 Scheme 版 `inline-call` 的形状展开（守卫 `IsInstanceOf`、子句改名、`$inl` 后缀）。
- 错误黄金文件 `tests/errors` 在 `CLEAT_COMPILER=clojure` 下通过。

## 从 cleat-089 移来的
- Clojure 单元的接口文件 `.ifc`（HasStatic 与 arity，现由 `Driver.WriteInterfaceFile` / `WritePackageInterface` 读 Lcore 与 Lcil 写出）随驱动改为 Clojure 编译器提供。
- 整单元的 G2：boot 镜像、随附包、tools 包按单元比较 Lcil（打包、镜像核对、预链接之后），cleat-089 只逐个形式比较了 lcil 与 aot。

## 验收标准
- 两种模式下全量测试都通过，基线不退化；编译耗时达到 19 D5 的目标或在结论里给出差距与计划。

## 结论（2026-10-14）

完成。

**驱动**（`src/clojure/cleat/compiler/driver.clj`，照 driver.sls / compile.sls / Driver.cs）：`eval-toplevel`（序列形式先完整宏展开并带上源位置、
顶层 do 拆开逐个求值）、简单形式的解释（L0 上的 `simple?` / `seval`，与 Scheme 驱动共用计数 `Driver.SimpleCount / CompiledCount` 与开关）、
其余编译成内存程序集执行（`DriverOps.RunModule` / `TakeResult` 以 IFn 交回值，不经宿主调用的 Int32 → long 转换；`DriverOps.RunForm` 照旧打位置戳）、
load 钩子（*ns* 绑定、单元后序收集）、compile 钩子与包程序集（`package-lcil` / 资源 / 镜像核对，照 compile.sls）、`compile-boot`（每个形式重置 RT 的 id，
加载结束补 `(in-ns …)`，构建时不内联）、接口文件 `.ifc`（读 L0 与 Lcil，`fn-arities` 同 Scheme 版的变长记法）与 `.inl`。
驱动状态放在线程局部变量（`DriverOps.State`）里，不用 Clojure 的动态绑定：驱动的 `binding` 弹出时会连带弹掉用户代码里不成对的 `pushThreadBindings`。
宿主：`CLEAT_COMPILER=clojure` 时加载镜像后 require `cleat.compiler.driver` 并 `install-hooks!`；`Driver.CompileBoot / CompilePackage`
（`--compile-boot`、`--compile-package`、tools.build 的编包脚本都经它们）在 Clojure 驱动登记了入口时交给它。

**小函数内联**：Clojure 编译器的 `.inl` 记 L0 子句的 S 表达式（fasl，`[:cleat/l0 子句]`），分析器在调用点照 Scheme 版 `inline-call` 的形状展开
（守卫 `IsInstanceOf`、子句改名、形参与实参临时变量带 `$inl`）；Scheme 编译器写的 Lcore 子句不认（反之 Scheme 驱动也不认 Clojure 的），
所以 Clojure 模式跑在 Scheme 编的镜像上时没有内联——cleat-091 把镜像换成 Clojure 编译器编的之后才有。

**顺带修正**
- 分析器的临时名序号改为进程内唯一（原来每个形式从 1 起）：一个单元里有多个形式时，codegen 按变量名记的类型表互相覆盖（镜像里 `root-directory`
  的局部变量被当成 double）。
- 宏调用的错误位置：顶层 macroexpand 时带上形式的位置（原来是 0:0）；`ArityException` 的属性是 `Name` / `Actual`。
- `simplify-letrec` 的前向变量：codegen 支持 `$uninitialized` / `$check-initialized` 两个 primcall（nrepl 里 `(fn this …)` 引用自己的 let 绑定），
  `check-forward` 引号里的名字照 Scheme 版是 Scheme 符号。
- 错误报告的过滤栈不显示 `cleat.compiler.*` / `cleat.nanopass.*` 与 `DriverOps` 的帧。

**验收**
- G3：`CLEAT_COMPILER=clojure` 下 Host 测试全过（语料门禁、test_clojure / cts 基线、错误黄金文件、SchemeInvisibilityTests、包与 NuGet、tools.build、
  nrepl / neorepl、示例）；默认（Scheme）模式下 Core / Scheme / Host 全过（5_2.ms 偶发同前）。语料：Clojure 编译器 4312 行通过，Scheme 4311
  （"a top-level do evaluates the forms ahead of a splice" 在 Clojure 驱动下通过，偏离表条目加 `:compiler :scheme`）。
- 镜像链：A（Scheme，--no-boot）→ B（Clojure，在 A 上）→ C（Clojure，在 B 上），B 与 C 的 Lcil、dll、`.inl` 逐字节相同；同在 A 上由 Scheme 编的
  BS 与 B 比较：Lcil 除 fn 类编号、eval 程序集编号与已知的 Int32 字面量外相同，接口文件 1238 条相同。
- G1 / G2（影子模式，`CLEAT_NO_INLINE=1`）：146905 条记录只有已知 Int32 字面量的 22 条不同。
- 耗时：编 boot 镜像 36 s（Scheme 18 s，2.0 倍，达到 19 D5 的目标）；REPL 单次 eval（300 个 defn）约 7.3 ms 对 5.3 ms；语料 52 s 对 48 s。
  基准（`tools/build.cs bench`）：Scheme 镜像上 Clojure 模式除 assoc_map 慢约三成（没有内联）外持平；Clojure 编的镜像上 assoc_map 40 ~ 49 ms，
  比 Scheme 模式的 53 ~ 55 ms 还快。
- Host 测试 `CompilerDiffTests.ClojureDriverEvalsLoadsAndCompiles`。

**已知 / 遗留**
- Scheme 镜像在 Clojure 模式下仍然加载：生成的代码引用 Cleat.Scheme 的运行时类型（RT.Box、Procedure 等），去掉在 cleat-092 / 093。
- 编译器不在加载路径上（发布布局还不带 cleat.compiler、`--boot` 指向别处的镜像）时静默回退 Scheme 驱动；随 cleat-094 的发布带上编译器。
