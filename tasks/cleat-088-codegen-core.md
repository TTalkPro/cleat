# cleat-088 codegen 一：骨架、调用、控制流与 fn*

- 里程碑：M8（阶段 A）　依赖：cleat-087　状态：完成
- 设计依据：[19 D5](../designs/19-clojure-self-hosting.md)、[05](../designs/05-cil-backend.md)、`src/Cleat.Emit/LCIL.md`

## 目标
不含类型推断的 codegen：L3 → Lcil S 表达式（Clojure 数据），交给 `Cleat.Emit`。

## 工作内容
1. 单元与方法上下文（`unit` / `mctx`：指令、标签、局部变量、try 深度、`operands!`）、Lcil 输出（发射器接受 Clojure 数据，`ClojureData.cs`）。
2. 常量编码（Clojure 值与普查需要的其余标签）、常量表。
3. Var 读写与 def、`clj-invoke` / `clj-keyword-invoke` / `clj-call`、`if` / `do` / `let` / `letrec` / `label` / `jump`、try / catch / finally（照现行：处理器闭包）。
4. fn* 类（AFunction / RestFn、`invokeStatic`、子句方法、闭包）、局部变量清除。
5. 接口文件 `.ifc` 的写出与读入（boot 与包）。

## 验收标准
- G2：不经类型推断的单元（普查给出名单，或以开关关掉推断后两边比较）Lcil 规范化后相同。
- 用 Clojure 编译器编出的小程序集能加载运行（Core / Host 测试里挑一组）。

## 结论（2026-10-14）

完成（范围与原计划有调整，见下）。

**移植**（`src/clojure/cleat/compiler/codegen.clj`，照 `codegen.sls` 逐条，Scheme 式的写法保留到 cleat-092）：
- 单元与方法上下文（`make-unit` / `make-method-ctx`，原子与 Hashtable）、常量表（`constant-index!` 的去重规则同 Scheme 版：按 Equals 查表，
  只有 Scheme 的 number? / char / string / symbol / boolean / 表 / 向量按值合并，其余按引用）、`load-constant!`、Lcil 的 S 表达式与文本写出
  （`lcil->string`，flonum 照 `boot/numbers.sls` 的 number->string）。
- Var 的读写与 def、`clj-invoke` / `clj-keyword-invoke` / `clj-call`（宿主调用的静态决议、Numbers 运算、PrimInvoke）、`clj-static-call`、`clj-case`、
  `if` / `begin` / `let` / `letrec` / `label` / `jump` / `set!` / box、try / catch / finally（处理器照现行是 Scheme 过程）、局部变量清除。
- fn* 类（AFunction / RestFn、`invokeStatic`、原始签名 `invokePrim`、async、子句方法、闭包字段）、Scheme 过程类与共享持有类（`$SP`、`$P<k>`）、commonize。
- **类型推断从 089 提前到这里**：类型格、`expr-type`、var-facts、`infer-walk` / `infer-var-types-into!`（不动点与候选顺序无关）、拆箱局部变量与 `cg-raw`。
  普查（cleat-085）表明推测 fixnum 循环与收窄（071）、查表折叠（069）在 Clojure 输入下不改变 Lcil，不移植；Scheme 专有的节点
  （gref / mref / prim / primcall / library / and / or / escape-frame / try-handle / host-*）报错。
- `cleat.compiler.pipeline/compile-unit`（推断的形式 = lower-loops 生成了 label 或有 ^long / ^double 形参的形式，同 Scheme 驱动）、
  `eval-form`（Clojure 编译器编译并在内存里运行一个顶层形式，程序集名 `ceval$N`）；影子模式在 l3 之后接着跑 codegen，转储 `lcil` 阶段。

**顺带修正**
- `clojure.lang.RestFn`：10 个实参、必需 9 个时第 10 个实参没有包成剩余序列；20 个实参、必需 9 个时漏掉第 13 ~ 15 个实参（cleat-006 转写的错误）。
  Clojure 编译器自己的 `clause-method`（9 个固定参数加 `& [shared]`）因此拿错了参数。Core 测试 `RestFnPacksEveryArityCombination` 覆盖全部组合。
- 宿主调用返回的 Int32 会被 `RetObject` 换成 long，经 `first` / `nth` 取出的值也一样：Scheme 数据里要的 fixnum（过程的 arity）经
  `Cleat.Core.Compiler.Fixnum`（IFn）取得；`identical?` 对布尔与数按值比较，Scheme 版的 `eq?` 改用 `Object/ReferenceEquals`。
- IR 转储：quote 的数据本身是 long 以外的整数时带类型名（`#cleat/Int32 48`），G1 能看出 fixnum 常量与 long 常量的区别（codegen 对两者生成的代码不同）；
  分析器的临时名改为 `g$名字$N$c`（子句的源名字照 Scheme 版从临时名还原）。

**验收（G2，eval 单元）**：`CLEAT_NO_INLINE=1 tools/build.cs ir-dump <目录> --shadow --corpus` 后 `compiler-diff.clj diff … --stage lcil`：
13080 条 Lcil 记录 13078 条规范化后相同；p0 ~ l3 共 119970 条记录除下面 Int32 字面量的 18 条外相同。没有比较的：deftype* / reify* / proxy-class* /
gen-class*（250 个形式，cleat-089 报错 "come with cleat-089"）。Host 测试 `CompilerDiffTests.ShadowCodegenMatchesSchemeLcil`（12 个形式的 Lcil 与 Scheme 版相同）、
`ClojureCompiledFormsRun`（Clojure 编译器编出的程序集加载运行：递归的原始签名函数、多 arity / 变长、解构与 loop、try、letfn、case、宿主互操作、宏）。

**已知差异 / 遗留**
- 源形式里的 Int32 字面量：读入器给 syntax-quote 里带元数据的形式生成 `(with-meta … {:line 30 …})`，行号是 Int32（JVM / ClojureCLR 同样把整张元数据表
  syntax-quote 进去）。Scheme 编译器照原样生成 fixnum 常量（`ldc.i4` + `RT.Box`），Clojure 编译器取出形式元素时宿主调用已把它换成 long（`ldconst`）。
  影响两个宏（`gen-delegate`、Libs 的 `thread`）的 18 条 p0 ~ l3 记录与 2 条 Lcil 记录，运行结果只差元数据里行号的装箱类型；按 Clojure 编译器的行为为准，
  不改 Scheme 版（双轨期只修错误）。
- 原有的错误（与本任务无关，Scheme 流水线同样）：`(defn ^long fib [^long n] …)`（返回类型标在名字上）之后 `(fib 10)` 抛 InvalidCastException
  `user$fib` → `primifs.LO`：Var 的 :tag 是求值后的函数对象 `clojure.core/long`，调用点按 LO 签名，fn* 按 :rettag 实现 LL。返回类型标在参数向量上的写法正常。
- 移到 cleat-089：deftype* 一族的类生成；AOT 单元的常量编码（`(constants …)`）与接口文件 `.ifc` 的写出（`unit-interface`）；直接链接的 HasStatic 登记。
