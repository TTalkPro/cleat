# 19 Clojure 自寄宿：Clojure 版 nanopass 与编译器、去掉 Scheme

> 状态：**现行方向**（2026-10-14 起，取代 [15](15-scheme-core-vendoring.md) 的"编译器用 Scheme 写、nanopass 不改"）。
> 用户决定（2026-10-14）：现行两段自举已经成立（Scheme 镜像 → Clojure 镜像），下一步**用 Clojure 实现 nanopass 与编译器，由它自举，然后把 Scheme 全部移除**。
> 三个前提问题的答复：移除范围 = 全部（src/scheme、Cleat.Scheme、Cleat.Stage0、Scheme 镜像，生成代码不再引用任何 Scheme 类型）；
> 信任根 = 入库的 Lcil 文本种子；门控 = 分级门控（只有自编译不动点要求逐字节）。
> 背景与旧评估：[07](07-nanopass-port.md)（表面设计 D1 ~ D6 仍有效）、[评估笔记](../notes/clojure-nanopass-plan-review.md)、[自寄宿路径笔记](../notes/bootstrap-stage0-and-selfhosting-path.md)（"第三段"）。

## §0 事实（2026-10-14 调研）

**要替换的东西**

| 部分 | 现状 | 规模 |
|---|---|---|
| nanopass 框架 | `src/scheme/nanopass`（原版快照） | 约 7600 行；编译器只用到一小部分（§2 D2） |
| 编译器 | `src/scheme/lib/cleat/scheme/compiler/`：parse 403、loops 292、front 543、inline 377、closures 84、constants 261、primitives 107、primdata 411、targets 84、lcil 43、**codegen 4060**；驱动 `compiler.sls` 182 | 约 6800 行 |
| Clojure 前端 | `src/scheme/clojure/`：syntax 1378、namespaces 596、syntax-kinds 126、compile 207、driver 205；依赖 psyntax（`boot/expander/core.sls` 4648 行）的语法对象、rib、注解与 cleat-048 的扩展表 | 约 2500 行 + psyntax |
| C# 元层 | `Cleat.Scheme`（14.1k 行）、`Cleat.Stage0`（1.1k 行）、`Cleat.PrimGen` 的 Scheme 原语表 | — |

**编出的 Clojure 代码对 Cleat.Scheme 的运行时依赖**（`cleat.boot.lcil` 的引用次数）：`Locals.Take` 25668（局部变量清除）、`Clojure.ClojureOps` 3260（反射互操作、字面量构造）、
`Clojure.ClojureStaticCalls` 3087（静态决议的宿主调用的参数 / 返回转换）、`RT` 2524（`IsTrue` / `Bool` / `Box(int)`）、`EmptyList` 1126（几乎都是"无泛型参数"占位的 `'()`）、
`Procedure` 109（`$try/catch` 的处理器编成 Scheme 闭包）、`ContinuationException` 35（每个 catch 要重抛）、`StaticProcedure` 19。
另外：保存的程序集经 `Cleat.Scheme.Fasl.ReadPackedTable` 读常量（`Cleat.Emit/EmitConstants.cs`）；`.inl` 内联文件里是 Scheme fasl 的 Lcore；
类型提示由 `Cleat.Scheme.Clojure.HostSyntax*`（C#）决议；`Driver.cs`（931 行）有读入、简单形式解释、错误格式化、接口文件。

**编译器实际用到的 nanopass 特性**：`define-language`（terminals、entry、`extends` 的 `+` / `-`、`maybe`、一层省略号含 `e* ... e`、`unparse-Ln`）；
`define-pass`（额外形参、`definitions`、多个处理器、处理器带额外形参、处理器之后的体、`-> * (v)` 返回值的 pass、自动补全子句与处理器、
普通 catamorphism `,[e]` / `,[e*] ...`、`guard`、`else`）；约 125 处 `nanopass-case`；`with-output-language` + 反引号模板（含 `[,x* ,e*] ...`、`,(expr) ...`）。
**没用到**：`define-parser`、`in-context`、显式 catamorphism（`,[f : x -> y]`）、多层省略号、`language->s-expression`、`diff-languages`、`prune-language`、trace / echo。
多处表按**节点身份**做键（`letrec-refs`、闭包的 `fvt`、codegen 的若干表）。

**codegen 里只为 Scheme 存在的部分**：Scheme `Procedure` / `VariadicProcedure` 调用约定与共享持有类、gref / gset（`Symbol.Value`）、`library-letrec*` 模块字段、
Int32 fixnum 与装箱缓存、`EmptyList` / `Unspecified` / `Cons`、`call-with-values`、`escape-frame` / `try-handle`、R6RS letrec 前向检查、
`$%b` boot 过程体系（primdata 折叠、boot `.ifc`）、fixnum / flonum 推测循环、`cp0-inline`（cleat-078 起只对 Scheme 代码）、rtd / rcd / 条件等常量编码。
这些在只编 Clojure 时多数用不到，但**哪些真用不到以普查为准**（D4）。

## §1 总体路线

```
阶段 A  并行建设（Scheme 仍在位，一切照旧由 Scheme 编译器编）
        cleat.nanopass + cleat.compiler（Clojure 源码，src/clojure/cleat/…）由现役 Scheme 编译器编成 cleat.compiler.dll，跑在 Clojure 镜像上
        差分门控 G1 / G2：同一输入两边各编一次，逐 pass 比较
阶段 B  切换：CLEAT_COMPILER=clojure 时 eval / load / compile / --compile-boot 全走 Clojure 编译器；G3（全量测试、基线、基准）
阶段 C  自举：Clojure 编译器编 core + 自身 → stage1 → stage2 → stage3，G4（stage2 == stage3 逐字节）；Lcil 文本种子入库；默认切到 Clojure 编译器
阶段 D  去 Scheme 化：只剩一个编译器以后，再改生成代码（try/catch 不用 Procedure、去 EmptyList 占位、常量不走 Scheme fasl……），重生成种子
阶段 E  删除 Scheme：src/scheme、Cleat.Scheme、Cleat.Stage0、Scheme 测试、构建目标；文档归档
```

关键点：**先忠实，后清理**。阶段 A ~ C 的 Clojure 编译器以"与 Scheme 编译器给出相同 IR"为目标，连 Scheme 痕迹（处理器闭包、`'()` 占位）一起照搬，
这样每个 pass 都能差分、出错能定位；去掉这些痕迹放到阶段 D，那时只有一个编译器要改，不存在双轨同步。

## §2 决策

**D1 时机：编译后的镜像上写编译器**（"第三段"）。`cleat.nanopass` 与 `cleat.compiler` 是普通 Clojure 命名空间，由现役 Scheme 编译器编译，
在 Clojure 镜像上以编译方式运行。写的时候 `clojure.core` 完整可用，不需要任何解释器，不受 07 D7 的子集限制。

**D2 nanopass 重写，按 07 D1 ~ D6 的表面，范围分两层**：
- 第一层（编译器需要，阶段 A 的前提）：§0 列出的"实际用到"的特性全部覆盖。
- 第二层（给用户的库，取代 [cleat-056](../tasks/cleat-056-nanopass-clojure-surface.md)）：多层省略号、显式 catamorphism `~[f :> x -> y z]`、`in-context`、
  `define-parser`、`language->s-expression`、trace；以上游 `tests/` 翻译为验收。第二层不阻塞阶段 B / C。
- 表面：模板用显式 `(out …)`（Clojure 的反引号在读入时已展开，宏看不到模板），`~e` / `~@e*` / `~e ...` / `~[e]`；产生式与模式里 `[ ]` 等同 `( )`。
- 记录：每个产生式一个 `deftype`（字段按元变量名 + int 标签），**引用相等**（不实现值相等，身份表直接用 `ReferenceEqualityComparer` 的 Dictionary）；
  处理器按标签 `case` 分派；`define-pass` 在命名空间级生成 `defn`，不在每次调用时构造闭包集合（iron-034 的教训）。
- 语言描述存在注册表（07 D5），宏展开期和加载期都登记；类型名与标签只取决于定义顺序（不动点需要）。

**D3 前端：照 Clojure 语义的分析器，不要语法对象**。用户级卫生宏已搁置（cleat-057），psyntax 的语法对象、wrap、rib 对 Clojure 没有用处。
新前端照 JVM `Compiler.analyze` 的结构：宏 = Var 上的 `:macro`，以 `(&form &env …)` 调用，`&env` 为符号 → `LocalBinding` 的映射；
命名空间与决议直接用 `clojure.lang.Namespace`（现在 `namespaces.sls` 就是在镜像它）；特殊形式、宿主语法糖（`.m`、`T.`、`T/m`）、类型提示（继续用
`HostSyntax` 的 C# 规则）、`:inline`、小函数内联、错误的 phase 全部照现行前端。**前端直接产出 L0**（合并现在的"展开 → Lcore → parse-core"两步），
L0 形状与现行 parse-core 的输出相同（D5 的 G1 在 L0 上比较）。`Cleat.Core/Expander`（cleat-013，死代码）作参考后删除，`LocalBinding` / `ExpanderException` 保留。

**D4 普查驱动的裁剪**。只编 Clojure 时，L0 的哪些节点会出现、哪些 pass 改不改变结果，不靠推断：阶段 A 开头先给现行 Scheme 编译器加普查
（编整个 boot 镜像与包时统计每种 L0 节点、每个 pass 是否改变输入、`cp0-inline` / 推测循环 / primdata 折叠开关前后 Lcil 是否相同），
结果写进 cleat-085 的结论，Clojure 编译器只实现普查证明会出现的部分。预期可去掉：`library` / `mref` / `gref` 等 Scheme 全局、`cp0-inline`、fixnum 推测循环、
`$%b` 折叠、`escape-frame` / `try-handle`、多值；以普查结果为准。

**D5 分级门控**（评估笔记 §3.2）：

| 门控 | 内容 | 用在 |
|---|---|---|
| G1 | 每个 pass 的输出 unparse 后与 Scheme 版结构相同（gensym 与临时变量名规范化后）；输入集 = boot 镜像全部顶层形式 + 随附包 + 语料 | cleat-086 ~ 087 |
| G2 | 每个单元的 Lcil 规范化后（局部变量、gensym、常量表顺序重编号）相同 | cleat-088 ~ 089 |
| G3 | `CLEAT_COMPILER=clojure` 下全量测试（现 632 项）、语料 / cts / test_clojure 基线、错误黄金文件、基准不退化；编译耗时记录在案（目标：编 boot 镜像不超过 Scheme 编译器的 2 倍） | cleat-090 |
| G4 | 自编译不动点：stage2 与 stage3 的 Lcil 与 dll 逐字节相同 | cleat-091 起每次重生成种子 |

差分工具（cleat-085）：两边都能把每个 pass 的结果 unparse 成 S 表达式，所以比较在数据层做，**不需要 Scheme 记录与 Clojure 记录互通**，也不需要把单个 Clojure pass 插回 Scheme 的 pass 链。

**D6 编译器的位置与加载**。源码 `src/clojure/cleat/nanopass/*.clj`、`src/clojure/cleat/compiler/*.clj`；编成独立程序集 `cleat.compiler.dll`（与 `cleat.boot.dll` 同在 `src/boot`、同样随运行时发布），
首次 eval / load 源码 / compile 时才加载。只引用预编译包程序集的 C# / F# 宿主不再需要加载编译器（cleat-067 的冷加载因此更轻）。

**D7 自举与信任根**（阶段 C 起）：

```
种子：src/boot/seed/cleat.boot.lcil.gz + cleat.compiler.lcil.gz（Lcil 文本，gzip 入库）
  Cleat.Emit（C#）发射 → stage1（boot + compiler 两个 dll）
  stage1 从源码编 boot-order.edn 的全部命名空间与编译器 → stage2
  stage2 再编一次 → stage3；判据 G4：stage2 == stage3（Lcil 与 dll 逐字节）
  tools/build.cs seed：用当前镜像重生成种子，并验证新种子再走一遍仍是不动点
```

- 种子是**可审阅的文本**，不需要解释器：取代 `boot-seed.core` + `Cleat.Stage0`。体积以实测为准（cleat-091 记录；未压缩约 15 MB 量级，故 gzip）。
- 运行时契约（Cleat.Core 的 C# 方法签名）改动时按两步法：先让旧种子还能发射出可运行的 stage1（保留旧入口），重生成种子后再删旧入口。

**D8 运行时去 Scheme（阶段 D）的落点**：
- 纯辅助类先搬（cleat-082，阶段 A 就做，Scheme 编译器只改类型名字符串）：`Locals`、`ClojureOps`、`ClojureStaticCalls`、`HostSyntax*`、`RT.IsTrue` / `Bool`、
  `Driver.cs` 里与 Scheme 无关的部分（简单形式解释、错误格式化、接口文件）→ `Cleat.Core`（命名空间 `Cleat.Core.Compiler` / `Cleat.Core.Interop`，名字在任务里定）。
- 语义改动留到阶段 D（cleat-092，只改 Clojure 编译器）：catch 处理器内联成代码而不是 `Procedure` 闭包、不再重抛 `ContinuationException`、"无泛型参数"占位改 `null`、
  顶层不用 `StaticProcedure`、常量表改由 Cleat.Core 的 Clojure 数据序列化读写（取代 `Cleat.Scheme.Fasl`）、`.inl` 改存 L0 的 Clojure 数据、Lcil 头 `(runtime scheme)` → `(runtime clojure)`、`RT.Box(int)` 去掉。

**D9 双轨窗口期的纪律**。从 cleat-085 开始到 cleat-091 默认切换为止：Scheme 编译器**只修缺陷、不加功能**；修的缺陷同时在 Clojure 编译器里修，并补一条 G1 / G2 的用例。
新的编译器功能（性能、新特殊形式）一律等到切换之后做在 Clojure 编译器里。

## §3 不做的

- 不保留"Scheme 作为种子文物"（评估笔记的原方案）：用户选了全部移除，信任根改为 Lcil 种子。
- 不做 Scheme 记录与 Clojure 记录的互通、不做"单个 pass 插回 Scheme pass 链"（D5 的数据层差分足够定位）。
- 不移植只为 Scheme 存在的 pass 与节点（以 D4 的普查为准）。
- R6RS 套件、Chez mats、Scheme 数值塔等只对 Scheme 有意义的测试与运行时，随阶段 E 删除，不迁移。
- LLVM 后端仍搁置。

## §4 风险

| 风险 | 应对 |
|---|---|
| 编译速度：Clojure 写的编译器比 Scheme 的慢，拖慢 REPL eval 与构建 | G3 记录编译耗时；身份表用 CLR Dictionary、处理器不分配闭包、热点用 `^long` 与 `deftype` 字段；必要时把热点放进 C#（如 Lcil 构造） |
| 栈深：IFn 调用不发 `tail.`，pass 是结构递归 | 栈深 = IR 嵌套深度；编译线程已是 512 MB 栈（`CleatRuntime`）；极深的 `do` / `let` 链用 `loop` |
| 双轨窗口期变长 | D9 冻结 Scheme 编译器的功能；任务按 G1 → G2 → G3 顺序推进，每步可单独交付 |
| 前端语义细节（`&env`、错误 phase、宏展开停止点）与现行不同 | 错误黄金文件（cleat-035 / 055）与 cts / test_clojure 基线是 G3 的一部分；前端在 L0 上做 G1 |
| 种子与运行时契约不同步 | D7 的两步法；`tools/build.cs seed` 自带不动点验证 |

## §5 任务

| 任务 | 阶段 | 内容 |
|---|---|---|
| [cleat-081](../tasks/cleat-081-self-hosting-plan.md) | — | 本设计与任务拆分 |
| [cleat-082](../tasks/cleat-082-move-clojure-runtime-helpers.md) | A | 纯辅助类从 Cleat.Scheme 搬进 Cleat.Core（D8 第一条），Scheme 编译器改引用，重生成镜像 |
| [cleat-083](../tasks/cleat-083-nanopass-languages.md) | A | `cleat.nanopass` 语言层：`define-language`、`extends`、记录、`unparse`、注册表 |
| [cleat-084](../tasks/cleat-084-nanopass-passes.md) | A | `cleat.nanopass` pass 层（第一层）：`define-pass`、`nanopass-case`、`(out …)` 模板、catamorphism、自动补全 |
| [cleat-095](../tasks/cleat-095-nanopass-user-layer.md) | — | `cleat.nanopass` 第二层：给用户的特性与上游测试（不阻塞阶段 B / C；2026-10-15 完成） |
| [cleat-085](../tasks/cleat-085-compiler-skeleton-and-diff.md) | A | `cleat.compiler.dll` 的构建、`CLEAT_COMPILER` 开关骨架、逐 pass 导出与差分工具、L0 普查（D4） |
| [cleat-086](../tasks/cleat-086-clojure-analyzer.md) | A | Clojure 前端（分析器）直接产出 L0；G1@L0 |
| [cleat-087](../tasks/cleat-087-middle-passes.md) | A | 前中端 pass：lower-loops、analyze、simplify-letrec、check-forward、cp0-lite、simplify-if、convert-assignments、uncover-free；G1@la/l1/l2/l3 |
| [cleat-088](../tasks/cleat-088-codegen-core.md) | A | codegen 一：单元与方法上下文、常量、Var 与调用、控制流、try、fn* 类、局部变量清除、接口文件；G2（不含类型推断的单元） |
| [cleat-089](../tasks/cleat-089-codegen-types-and-classes.md) | A | codegen 二：类型格与收窄、l64 / 原始签名 / 宿主调用类型、deftype / reify / proxy / gen-class、case、直接链接、async；G2 全部单元 |
| [cleat-090](../tasks/cleat-090-clojure-compiler-driver.md) | B | 编译驱动用 Clojure 写：eval / load / compile / compile-boot / compile-package、`.clj.dll` 与包程序集；G3 |
| [cleat-091](../tasks/cleat-091-self-hosting-and-lcil-seed.md) | C | 自举不动点 G4、Lcil 文本种子、构建流程不再经 stage0 产出 Clojure 镜像，默认编译器切换 |
| [cleat-092](../tasks/cleat-092-de-scheme-codegen.md) | D | 生成代码去 Scheme（D8 第二条），常量与 `.inl` 新格式，裁掉 Scheme 专用节点 |
| [cleat-093](../tasks/cleat-093-remove-scheme.md) | E | 删除 Scheme：源码、程序集、测试、构建目标、NOTICE、文档归档 |
| [cleat-094](../tasks/cleat-094-republish-self-hosted.md) | E | 新版本（Cleat.Runtime / Cli 1.1.0 等）重推 BaGetter，`cleat.nanopass` 作为用户库的文档 |

关键路径：081 → 082 → 083 → 084（第一层）→ 085 → 086 → 087 → 088 → 089 → 090 → 091 → 092 → 093 → 094。
095（nanopass 第二层）与 088 之后的工作可以并行。
