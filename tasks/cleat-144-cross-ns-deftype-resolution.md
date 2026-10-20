# cleat-144 跨 ns 的 deftype / definterface 编译期决议

- 里程碑：M16　依赖：无　状态：完成（2026-10-20，开关 `crossnstype`）
- 设计依据：[28 §2.3](../designs/28-hot-path-optimizations.md)

## 问题

实测兼容性缺口：`(:import (r7rs.util MPair))`、`^IMPair` 类型提示、`(MPair. 1 2)` 在 cleat 上
全部 "Unable to resolve classname"，JVM Clojure 正常。用户代码因此只能走工厂函数 + 接口访问器
（`mcons` / `mcar`），既多一层包装调用（+2 ns / 次，shapes3 实测），又拿不到字段直读，
还使 cleat-145 的访问器内联无从下手。

## 工作内容

1. 定位缺口：deftype / definterface 生成的 CLR 类型名在跨编译单元（其他 ns 的 AOT / 运行时编译产物）
   时如何登记；对照 cleat-042 的内联接口文件与 cleat-106 的类型决议机制。
2. TypeResolver / 命名空间映射补生成类决议：`ns` 里 deftype 定义的类可被其他 ns `:import`、
   用作类型提示与构造（`Foo.`），行为对齐 JVM（含未编译时的 require 触发加载）。
3. 接口文件（ifc）或程序集清单登记生成类，AOT 与运行时编译两条路都覆盖。
4. 补差分 / 符合性用例：跨 ns `:import` deftype、definterface、协议类型的构造与提示。

## 验收

- r7rs.util 的 `(:import (r7rs.util MPair))` + `^IMPair` + `(MPair. 1 2)` 在 cleat 上可用，
  行为与 JVM 一致。
- 语料门禁（gate 521 分类）与 CTS 的 cts 基线不变；**test-clojure 基线有意更新**（见结论 3）。
- 涉及 codegen / driver 时种子不动点（stage2 == stage3）。

## 结论（2026-10-20）

1. **决议缺口的三处出处与修法**（都由 `CLEAT_NO_OPT=crossnstype` 退回旧路径，开 / 关同一份二进制）：
   - `RT.classForName`（`src/Cleat.Core/clojure.lang/Lib/RT.cs:3002`，开关 `s_crossNsTypes` 在 2853）：
     旧路径只在**已载入**程序集里找类型，包构建时依赖包还没被加载就决议失败；新增按包索引
     `Cleat.Core.Runtime.Loader.LoadAssemblyDefining(p)` 把定义该类型的程序集载进来但不运行其入口
     （等价于 classpath 上的按名查找）。这是运行时 `(import ...)` / 反射用到的那条。
   - 分析器侧（`src/clojure/cleat/compiler/analyzer.clj`）：`class-for-name` 在 `RT/classForName` 失败后
     重试 `CompileTimeTypes/Get`（同进程刚分析过、还没求值的 deftype / definterface）；`tag-type` 对
     带点的全名先 `require-type-ns!` 再重试，使 `^crossns.gen.Box` 这类提示可用；`sp-import*` 走
     `import-class-for-name`，决议成功后用 `ClojureOps/ImportOf` 立刻在当前 ns 建别名。
   - `require-type-ns!`：编译期按需加载类型所在 ns。两条必要的边界：**不 require 当前 ns**
     （否则 core 加载自己的 `gvec` 类被记成循环依赖），以及 **require 的异常必须吞掉**
     （宿主包名不是 Clojure ns，require 抛的 `FileNotFoundException` 会顶掉调用方原本要报的
     `Unable to resolve classname`，见下面第 3 条）。决议成功与否只由返回值决定。
   - `gen-interface` 额外登记**编译期桩接口**（`HostSyntax.DefineInterfaceStub`）：包构建时接口本身
     还没求值，后续 ns 的 `:import` 与提示要能按方法签名做静态决议；真接口求值后按同名取最新定义
     （与 cleat-032 的 `import` 取最新定义一致）。
2. **验收第一条实测**：`bench/r7rs/src/r7rs/util.cljc` 的 `MPair` / `IMPair` 就是原缺口最直接的用例。
   main（4c8314f 的构建）上 `(ns t (:import (r7rs.util MPair)))` 直接
   `Syntax error (ArgumentException) compiling import* … Unable to resolve classname: r7rs.util.MPair`；
   本改动后同一个脚本（`:import` 的 `MPair` 与 `IMPair`、`^IMPair` 提示、`.getCar` 直读、`(MPair. 1 2)`
   直接构造、`class` 取回 `r7rs.util.MPair`）输出 `[true 3 5 7 r7rs.util.MPair]`，与 JVM 语义一致。
   Host 测试 `CrossNamespaceGeneratedTypesResolve` 另外覆盖**两个包程序集分开编译**这条路：
   `crossns.gen` 先编成 dll，`crossns.use` 只按 `-I <outDir>` 找到生成类型并编译，运行
   `(require 'crossns.use)` 后 `-main` 得 `[true 49 7]`；开关两侧都过。
3. **test-clojure 基线的变化是行为改善，逐条核对过**（这是本任务最需要诚实记录的地方）。
   开关关闭侧（`CLEAT_NO_OPT=crossnstype`）与 main 基线**逐字相同**
   （`tests 357 / pass 3688 / fail 18 / error 0`、14 not loaded、`baseline: OK`）→ 旧决议路径完整保留。
   开关打开侧 `tests 384 / pass 3877 / fail 25 / error 4`、12 not loaded，只有 3 个命名空间变：
   - `clojure.test-clojure.protocols`：从 load-error（`:import` 的 `ExampleInterface` 是 gen-interface
     产物，编译期不可见）变为可运行 → 23 tests / 184 pass / 1 fail / 4 error。
   - `clojure.test-clojure.def`：它 `(:use clojure.test-clojure.protocols)`，被上一条连带挡住；现在能运行
     → 4 tests / 5 pass / 6 fail。
   - `clojure.test-clojure.ns-libs`：仍是 load-error，但失败点从 `ns_libs.clj:71:27`
     （同 ns 的 deftype `clojure.test_clojure.ns_libs.String`，正是本任务修的那类）推到 `:100:3`
     的 `clojure.lang.Compiler+CompilerException`（嵌套类决议，`control` / `errors` / `special` 在基线里
     就卡在同一处）→ 本任务的决议确实生效，剩下的缺口与本任务无关。
   新暴露的 6 + 1 个 fail 与 4 个 error 逐条看过（`/tmp/inspect144.log`），**都不是跨 ns 类型决议问题**：
   `defn-error-messages` 6 条期望的是 `clojure.lang.ExceptionInfo "Call to clojure.core/defn did not conform
   to spec"`，cleat 走的是 `ArgumentException "Parameter declaration … should be a vector"`（语料里同一族的
   spec 文案条目已按 `:portability :jvm` 分类）；`protocols` 的 4 个 error 是 defrecord 的 `^long` 字段收到
   String 时抛 `InvalidCastException` 而非 `TargetInvocationException` / `FormatException`（互操作异常类型
   的既有缺口），1 个 fail 是协议重定义的异常文案不同（`_cache_protocol_fn` 的 miss 文案族）。
   → 结论：144 让两个此前**完全跑不到**的命名空间进入统计，读数变多是覆盖率上升，不是新增缺陷；
   这些发散按既有缺口记账，不由 144 修。
4. **种子**：改 codegen / 分析器后 `clojure-seed` 自举到不动点（stage2 与 stage3 的 boot 与 compiler
   产物逐字节相同，239 s），`src/boot/seed/*.gz` 相应更新（2190 KiB）。
5. **验证**（在 144 的 worktree 内串行跑；全套只在合并后的 main 上跑）：Release 构建 0 error / 0 warning、
   `conformance --run --gate` `gate: OK (521)`、`cts cts` 6868 / 29 / 6 与基线逐字相同、
   `cts test-clojure` 两侧各一遍（关侧对上旧基线、开侧对上写回后的新基线）、
   Cleat.Core.Tests 123/123、Host 的 `CrossNamespaceGeneratedTypesResolve` + `BuildFromTheSeedReproducesTheSeed`
   + `StringSeqDirectReads` 3 项通过。
6. **未做**：把某项 bench 程序改成"直接构造 + 字段直读"作端到端对照 —— 直接构造在 cleat 上是否比
   `mcons` / `mcar` 快属于 cleat-145（访问器内联）的问题面，144 只解决"能不能写"。已记为 145 的前置。
