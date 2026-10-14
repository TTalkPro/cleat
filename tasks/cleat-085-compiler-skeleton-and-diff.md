# cleat-085 编译器工程骨架、差分工具与 L0 普查

- 里程碑：M8（阶段 A）　依赖：cleat-082、cleat-084（第一层）　状态：完成
- 设计依据：[19 D4 ~ D6、D9](../designs/19-clojure-self-hosting.md)

## 目标
Clojure 编译器有地方放、有办法逐 pass 和 Scheme 编译器对比，并且知道只编 Clojure 时哪些节点与 pass 真正需要。

## 工作内容
1. `cleat.compiler.dll`：由现役 Scheme 编译器从 `src/clojure/cleat/{nanopass,compiler}` 编出（构建目标与 `tools/build.cs`），随运行时发布，首次需要时加载。
2. 开关骨架：`CLEAT_COMPILER=clojure` 时运行时钩子改走 Clojure 编译器（此时各 pass 未实现，先接通"前端 → unparse"的路径）。
3. 导出：Scheme 编译器对每个顶层形式写出各阶段 unparse（`l0` / `la` / `l1` / `l2` / `l3` / `lcil`），输入集 = boot 镜像全部顶层形式 + 随附包 + 语料；Clojure 编译器同格式导出。
4. 规范化与差分工具（`tools/compiler-diff.clj` 或 `tools/build.cs compiler-diff`）：gensym / 临时变量 / 局部变量 / 常量序号规范化，按形式与阶段报告第一个差异。
5. 普查（D4）：编整个 boot 镜像与包时统计每种 L0 节点的出现次数、每个 pass 是否改变其输入；`cp0-inline`、推测循环、primdata 折叠关掉前后 Lcil 是否相同。结论写进本任务。
6. 双轨纪律（D9）写进 TASK.md 的工作约定。

## 验收标准
- 差分工具对 Scheme 自己（两次运行）报告零差异；故意改一个 pass 时能定位到形式与阶段。
- 普查表：Clojure 编译器要实现的节点与 pass 清单。

## 结论（2026-10-14）

完成。

**编译器的包程序集**：`src/boot/cleat.compiler.dll`（包名 `cleat.compiler`，入口 `src/clojure/cleat/compiler/all.clj`，新的编译器命名空间要在那里登记），
由现役 Scheme 编译器以 `--compile-package` 编出：`Cleat.Host.csproj` 的 `GenerateCompilerPackage`（在 Clojure 镜像之后，按输入增量）或 `tools/build.cs compile-compiler`。
Clojure 镜像的输入排除了编译器源码（改编译器不重编镜像）。宿主启动时登记进包索引，第一次 `require` 才加载（比镜像旧、或开发布局下比编译器源码旧时不用，
改走源码）：从包加载 `cleat.compiler.all` 194 ms，从源码 10 s。打包（NuGet、dotnet tool、publish）暂不带它，cleat-090 加。

**开关**：`CLEAT_COMPILER` = `scheme`（缺省）/ `shadow`；其他值（`clojure`）在启动时报错"cleat-090 实现"。

**逐形式转储**（`CLEAT_DUMP_IR=目录`，`src/Cleat.Scheme/Clojure/IrDump.cs`，过渡期工具）：
- 每个编译单元一个 `目录/<一侧>/<单元>.ir`，记录 `;; 序号 阶段` + 文本。Scheme 编译器在前端每个阶段写一条：`p0`（parse-core）、`l0`（lower-loops）、`sl`（simplify-letrec）、
  `cf`（check-forward）、`c1`（cp0-lite）、`in`（cp0-inline + simplify-lets + cp0-lite）、`l1`（simplify-if）、`l2`、`l3`，每个单元的 `lcil`。eval 的单元统称 `eval`；
  `CLEAT_DUMP_IR_TAG` 给单元名加前缀（同一目录收集几个进程）。
- 规范文本：IR 的结构是列表，`symbol` 终结符是裸标识符（不能读回的写成 `#cleat/id "…"`），datum / name / flag 位置是 `PrintDatum`——Clojure 与 Scheme 的值带 `#cleat/…` 标签
  （`#cleat/var`、`#cleat/sym`、`#cleat/kw`、`#cleat/type`、`#cleat/vec`、`#cleat/map`、`#cleat/slist`、`#cleat/record` …），同名的 Clojure 符号与 Scheme 符号不相等。
  Scheme 一侧按 L0 ~ L3 的数据位置表打印 unparse 的结果；Clojure 一侧（`cleat.compiler.dump`）按语言描述打印节点，规则相同。
- 序号：驱动的 `eval-form` 进入时从两侧共用的计数器取，编译前压给前端（宏展开里嵌套的 eval 先编译，序号仍按进入顺序）；不经 `eval-form` 的编译从 1000000 起编号。

**影子模式**（`CLEAT_COMPILER=shadow` 且 `CLEAT_DUMP_IR`）：驱动对每个顶层形式在 Scheme 展开之前调用 `cleat.compiler.shadow/shadow-form`，异常记成该形式的 `error`
阶段、不影响 Scheme 编译。现在只记下源形式（`src`，差分报告用来定位）；cleat-086 起接上前端与之后的阶段（`cleat.compiler.pipeline/shadow-stages`）。
影子命名空间在宿主启动时加载：放到第一个形式时才 `require`，被它拉起的源码加载（构建镜像时 clojure.string 从源码重新求值）会插进形式序列。

**输入集**：`tools/build.cs ir-dump <目录> [--shadow] [--corpus]`：在现有镜像上从源码编 boot 镜像、随附库、tools.namespace / tools.build（产物写到 `<目录>/build`，不动 `src/boot`），
`CLEAT_NO_SIMPLE_EVAL=1`（简单形式也走编译器）；约 60 秒，Scheme 一侧约 190 MB，共 51258 条记录。

**差分**：`cleat -I src/clojure tools/compiler-diff.clj diff <A>/<一侧> <B>/<一侧> [--stage s]… [--max n]`。临时名（psyntax 的 `g$名字$N$作用域`、`g$genN$作用域`、内联副本的 `$inl`、
gensym 的 `名字__N` / `名字__N__auto__`、fresh 的 `名字%N`）按记录内首次出现重新编号后比较文本；每个形式报告流水线上第一个不同的阶段（附源形式、前后 80 个字符），
其余只计数；只在一侧出现的记录与单元单独计数。`... census <目录>/scheme [--unit u]…` 是普查。
验证：Scheme 自己两次运行（含一次影子模式）51258 条记录零差异；关掉 lower-loops 时报告定位到 `stage l0`（Host 测试 `CompilerDiffTests` 2/2）。

**普查**（boot 镜像、随附库、tools.build、tools.namespace 的 AOT 单元，2696 个形式）：

| L0 产生式 | p0 | l0（lower-loops 后） |
|---|---|---|
| quote | 42179 | 42179 |
| clj-var-ref | 18367 | 18367 |
| lambda / clause | 15961 / 16446 | 3263 / 3748 |
| call | 13108 | 0 |
| clj-call | 11150 | 11150 |
| clj-invoke | 9662 | 9662 |
| clj-static-call | 7935 | 7935 |
| if | 5221 | 5221 |
| letrec | 5113 | 263 |
| let / label / jump | 0 | 7848 / 4850 / 5260 |
| clj-def | 1780 | 1780 |
| begin | 1116 | 1116 |
| clj-keyword-invoke | 642 | 642 |
| throw / try-finally / try-catch | 311 / 204 / 47 | 同 |
| clj-deftype / clj-var-set! / set! / clj-case | 74 / 30 / 22 / 18 | 同 |
| gref、gset!、mref、mset!、prim、primcall、library、and、or、break、escape-frame、try-handle、全部 host-*、unsupported | 0 | 0 |

Clojure 输入的互操作全部是 `clj-call`（`ClojureOps` / 静态决议），不出现 host-* 节点；`primcall` 也不出现。

| pass | 改变的形式数 |
|---|---|
| lower-loops | 1900 |
| simplify-letrec | 0 |
| check-forward | 0 |
| cp0-lite | 105 |
| cp0-inline + simplify-lets（+ cp0-lite） | 567 |
| simplify-if | 0 |
| convert-assignments | 0 |
| uncover-free | 1875 |

关掉开关前后的 Lcil（全部 2703 个单元的 `lcil` 记录）：

| 开关 | 管什么 | Lcil 不同的单元 |
|---|---|---|
| `CLEAT_NO_069=1` | cp0-lite 的查表折叠与 discard（primdata、`$%b`） | 0 |
| `CLEAT_NO_070=1` | cp0-inline、simplify-lets 与下面三项 | 536 |
| `CLEAT_NO_070=letrec` / `list` / `call` | letrec 常量并组 / list → cons / letrec 局部过程直接调用 | 0 / 0 / 0 |
| `CLEAT_NO_071=spec` / `1` | 推测 fixnum 循环 / 连同收窄（var-facts） | 0 / 0 |
| `CLEAT_NO_073=if` / `commonize` | simplify-if / commonize | 0 / 9 |
| `CLEAT_SCHEME_NO_034=loops` | lower-loops | 1541（boot 构建失败，只比较到失败前） |

（070 的影响来自 simplify-lets：`(let ((G x)) …)` 这类变量绑定的传播、空 `let` 的删除；Clojure 的 fn* 不参与 cp0-inline。）

**对 Clojure 编译器的裁剪清单**（cleat-087 ~ 089 据此实现，cleat-092 据此删掉 L0 的产生式）：
- 前端产出的 L0 只有上表非零的产生式；`gref` 等 Scheme 全局、`primcall` / `prim`、`and` / `or`、`break`、`escape-frame` / `try-handle`、host-*、`unsupported`、`library` 不实现。
- pass：lower-loops、cp0-lite（不含查表折叠与 discard；Clojure 输入下实际起作用的部分在 087 用 G1 对出）、simplify-lets（cp0-inline 是否改变 Clojure 代码在 087 用 G1 确认）、
  uncover-free / attach。simplify-letrec 只需要"letrec 只绑 lambda"的形状变换（L0 → L1，Clojure 输入下全部已是 lambda）；check-forward、simplify-if、convert-assignments
  对 Clojure 输入不改变任何形式——convert-assignments 仍要（L1 → L2 的语言变换，deftype 字段的 set! 不装箱），其余两个不移植。
- codegen：不需要推测 fixnum 循环与收窄（071）、primdata 折叠；需要 commonize（9 个单元）。

**双轨纪律**写进 TASK.md 的工作约定。
