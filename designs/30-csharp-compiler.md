# 30 C# 编译器：小 pass、逐层下降、ILGenerator 直接发射

> 状态：**现行方向**（2026-11-03 起，M20）。取代 [19](19-clojure-self-hosting.md) 的"编译器用 Clojure 写并自举"。
> 用户决定（2026-11-03）：编译器改用 C# 实现，保持 nanopass 的方法（很多小 pass、每层一个定义明确的中间语言、逐层优化），
> 最后一层直接驱动 `ILGenerator` 生成 CIL；不再走 Clojure 自寄宿。三个前提问题的答复：
> 迁移方式 = **并行建设 + 门控切换**；优化 = **先跑通，再逐项补**（每项一个独立 pass，各自验收）；
> `cleat.nanopass` = **保留为用户库**。任务 cleat-171 ~ 182。

## §0 为什么改

| 现状（19 的结果） | 代价 |
|---|---|
| 编译器 6.8k 行 Clojure + nanopass 框架 1.6k 行，跑在镜像上 | 改编译器要重生成种子（`clojure-seed` 约 3 分钟）；运行时契约改签名要走两步法；改名的工具编译器、`--target-image`、镜像核对 |
| 信任根是 Lcil 文本种子（`src/boot/seed/*.gz`） | 自举 stage1 → 3、不动点比较；CI 要有种子守卫（cleat-162） |
| codegen 3505 行，`cg*` 一个函数 330 行 | 类型推导、推测、装箱决策、指令选择混在一起；L3 之后没有中间语言，cleat-165 的拆分一直排不上 |
| Lcil 是 codegen 与发射器之间的数据边界 | eval 每个形式都要走"拼列表 → `ClojureData.Convert` → `FromTree` → 模型 → `Build()`"；类型与成员都按字符串传，发射时才决议 |

改成 C# 之后：编译器由 `dotnet build` 编出，**没有自举、没有种子**；镜像的生成退化为普通构建步骤
（C# 编译器逐形式"分析 → 编译 → 执行" `core.clj`，与 ClojureCLR 相同，没有循环依赖）。
nanopass 的本义（小 pass + 语言即不变量 + 每层可打印可校验）用 C# 的类型实现，L3 之后补上缺的几层。

不变的东西：运行时 `Cleat.Core`、`clojure.lang`、`core.clj` 与随附库、包程序集的形状（17）、`.ifc` / `.inl` 的语义、
`RuntimeHooks` 边界、全部测试基线与性能口径（151 / 29 §9）。

## §1 总体路线

```
阶段 A  并行建设    src/Cleat.Compiler（C#）在旁边长起来；CLEAT_COMPILER=cs 时 eval / load / compile 走它，默认仍是 Clojure 编译器
                    A1 IR 基建与骨架 → A2 分析器 → A3 前中端 pass → A4 基础下降与 ILGenerator 发射（全 object 表示）
                    → A5 类型、case、try、互操作、async → A6 持久化（镜像 / 包 / .ifc / .inl / PDB）
阶段 B  功能切换点  用 C# 编译器编镜像与全部包，跑全套功能门控（G2 / G3）；性能此时允许落后
阶段 C  逐项补优化  每项优化一个（或一组）独立 pass，各自开关、各自 56×5 配对验收
阶段 D  默认切换    整体性能门控（G4）通过后默认改为 C# 编译器；Clojure 编译器只修缺陷
阶段 E  删除        Clojure 编译器、种子、自举、Lcil 读取器与文本格式；nanopass 移到 packages/ 成为用户库；文档归档
```

**双轨纪律（cleat-171 起到 cleat-182 删除为止）**：Clojure 编译器只修缺陷、不加功能；修的缺陷同时在 C# 编译器里修
（C# 那边还没覆盖到的部分记账，覆盖时补）。新的编译器功能只做在 C# 编译器里。种子在阶段 E 之前照旧维护。

## §2 决策

**D1 项目边界**。新建 `src/Cleat.Compiler`（C#，引用 `Cleat.Core`；程序集名 `Cleat.Compiler.CSharp`——程序集按名字解析不区分大小写，叫 `Cleat.Compiler` 会顶替 Clojure 编译器的包程序集 `cleat.compiler.dll`，cleat-182 删除它之后可以改回），不放进 `Cleat.Core`：
运行时 NuGet 包（17）不带编译器；编译器延迟加载（cleat-161）按程序集粒度继续成立。
`Startup.EnsureCompiler` 改为：`CLEAT_COMPILER=cs`（阶段 D 后为默认）时加载 `Cleat.Compiler` 并调用其 `InstallHooks()`，
否则走现在的 `cleat.compiler.driver`。C# 编译器装同样的钩子：`RuntimeHooks.Set("eval" | "load-source" | "compile" | "macroexpand-1")`、
`DriverOps.InstallCompileHooks(compileBoot, compilePackage)`；`Cleat.Host` 与 `Loader` 不感知编译器换了实现。

**D2 中间语言与 pass 链**。每层语言都写清它的不变量（§3）；pass 只做一件事，输入输出都是某一层语言。

| 层 | 产生它的 pass | 不变量（相对上一层的差异） |
|---|---|---|
| **L0** | `Analyze`（形式 → L0） | 宏已展开；符号已分为局部 / Var / 宿主成员；特殊形式已检查；`clj-call` 的字符串操作码换成具名节点（§3.2） |
| L0 | `LowerLoops` | 具名 let 的 letrec 改为 `label` / `jump` |
| **L1** | `SimplifyLetrec`、`CheckForward`、`Cp0Lite`、`SimplifyLets` | letrec 只绑定 lambda |
| **L2** | `ConvertAssignments` | 被捕获又被赋值的局部改成 `Box`；`set!` 只剩对未捕获局部的 |
| **L3** | `UncoverFree` | lambda 带自由变量表 |
| **L4 表示** | `InferTypes`（分析）→ `ChooseRepresentation` | **新层**。每个局部、形参、返回值带表示 `Obj / Long / Double / Num / Bool / Char / Typed(T)`；装箱 / 拆箱是显式节点；宿主调用已静态决议成 `MethodInfo` 或明确标为反射 |
| L4 | 优化 pass（阶段 C，§4） | 不改变不变量，只改表示与形状：推测循环、类型化入口、标量替换、折叠标签…… |
| **L5 类** | `ConvertClosures` | **新层**。lambda 变成类描述（字段、构造器、`invoke` / `invokeStatic` / 类型化方法）；自由变量变成字段访问；顶层形式变成 `Init<k>`；常量进常量表（下标）；deftype / reify / proxy / gen-class 同样是类描述 |
| L5 | `LiftTry`、`ExplicitLocals` | 表达式位置的 `try` 提升为语句（进入 try 时栈为空）；每个方法的局部变量、标签显式化；局部清除（`Locals.Take`）的位置已决定 |
| **CIL** | `EmitIL` | 两阶段：先按 L5 的类表 `DefineType` / `DefineField` / `DefineMethod`，再逐方法用 `ILGenerator` 生成方法体，最后基类先 `CreateType` |

L4 与 L5 是这次补上的两层：现在 codegen 里混在一起的"类型推导 + 装箱策略 + 推测"与"类生成 + 指令选择"，各自成为可 dump、可单独关闭的 pass。

**D3 语言在 C# 里怎么写**。不做 C# 版的 nanopass 宏框架。约定：
- 所有层共用一个节点层次（`abstract record Node`，约 40 种节点），每种节点用特性标出它属于哪些层（`[Lang(L0, L1, L2, L3)]`）。
  层的差异就是节点集合的差异，加上 §3 写下的附加不变量。
- **校验器**：`IrValidator.Check(node, Lang)` 检查"只出现本层的节点"和本层的附加不变量；测试与 `CLEAT_IR_CHECK=1` 时在每个 pass 之后运行。
- **重写基类**：`Rewriter` 对每种节点有一个虚方法，默认实现是"递归子节点，子节点都没变就返回原节点"（结构共享）；
  pass 只覆盖它关心的节点。分析用 `Walker`（只读遍历）。Source Generator（`src/Cleat.Compiler.Generators`）从节点类的字段生成构造器、`Update`、
  这两个基类的访问方法与 `NodeKind` 枚举（cleat-171）；S 表达式的打印 / 读入按层手写（各节点形状不规则，手写比模式描述更直接）。
- **打印**：每层都能打印成 S 表达式；`CLEAT_DUMP_IR=<pass,…>` 把指定 pass 之后的 IR 写出（取代 `CLEAT_DUMP_LCIL`）。
  L0 的打印格式与现在 Clojure 编译器的 `unparse` 一致，供 G1 差分与 `.inl` 复用（D6）。
- 节点身份做键的表（现在 `letrec-refs`、闭包 `fvt` 等）用 `ReferenceEqualityComparer`。

**D4 发射**。`EmitIL` 直接驱动 `ILGenerator`，不经 Lcil。现在 `Cleat.Emit` 里与 Lcil 无关的部分搬进 `Cleat.Compiler.Emit` 复用：
类型名解析（`TypeResolver`）、成员决议与 `clr-*` 互操作规则（`EmitInterop.cs`，改为编译期由 L4 的 `ChooseRepresentation` 调用）、
常量表 fasl（`FaslEncoder`）、`PersistedAssemblyBuilder` 与 MVID / 时间戳回填、Portable PDB 与序列点、内存模式每 1000 个类型一个程序集、
`EvalLoadContext` 与 `RunAndCollect`、`tiered` / `optimize` / `async` 方法属性、`(lazy-start)`、嵌入资源、`CleatModule` / `CleatPackage` 属性。
短格式选择、`leave` / `endfinally` 由 `ILGenerator` 自己处理。`Cleat.Emit` 的 Lcil 读取器与文本格式在阶段 E 删除。

**D5 分析器全部用 C# 重写**。现在 analyzer.clj（1711 行）在 Clojure 里做宏展开、符号决议、特殊形式、宿主重载选择、跨单元内联与已知调用。
C# 版按同样语义实现，直接复用已经在 C# 里的 `HostSyntax*`、`CompileTimeTypes`、`AotPaths`、`Diagnostics`、`InterfaceFile`。
必须保持的语义（不是优化，阶段 A 就要做对）：
- 逐形式交替：每个顶层形式**分析 → 编译 → 执行**之后才读下一个（`def` 的宏立即生效）。
- 分析期的副作用：`def` 在分析初值之前 intern Var 并写 `:dynamic` / `:arglists` / `:tag` 元数据；`deftype*` 在分析期 `HostSyntax.DefineStub`。
- `&env` 是 sym → `Cleat.Core.Expand.LocalBinding` 的映射；宏调用约定 `(&form &env args…)`；错误按 Clojure 1.12 的阶段分类（错误黄金文件不变）。
- 源位置：`:pos` 在分析时记文件；内联体保留自己的源位置（cleat-158）。

**D6 `.ifc` / `.inl` 格式保持不变**。`.inl` 里是 L0 子句的 S 表达式（`[:cleat/l0 clause]` 的 fasl），C# 编译器读写同一格式（D3 的 L0 打印 / 读入）。
这样阶段 A ~ D 两个编译器编出的镜像与包可以互相加载对方的接口文件，切换不需要一次性重编所有包。格式的清理（如果需要）放到阶段 E 之后。

**D7 构建与确定性（阶段 E 之后）**。

```
dotnet build：Cleat.Core → Cleat.Compiler → cleat --compile-boot（C# 编译器）→ src/boot/cleat.boot.dll + .ifc / .inl
                                          → 随附库与包程序集（--compile-package）
判据：同一提交构建两次，镜像与包逐字节相同（取代不动点比较）；ilverify 通过
```

确定性措施继续有效（08 §7）：每个形式重置 `RT.nextID`、镜像构建不做新的内联决策、MVID 按 SHA256 回填、map / set 常量按打印形式排序。
`src/boot/*.dll` 是构建产物（忽略），不再入库任何种子；`ClojureBootstrapTests` 换成"构建两次逐字节相同"。

**D8 门控**。

| 门控 | 内容 | 用在 |
|---|---|---|
| G1 | L0 差分：同一输入两个分析器各出 L0，打印后比较（局部名的编号按出现顺序规范化） | A2，阶段 A 期间常驻于 Host 测试 |
| G2 | 功能：Core / Host 全量、conformance `--gate`、CTS 与 test-clojure 基线不变差、错误黄金文件 | A4 起逐步打开，B 必须全过 |
| G3 | C# 编译器编出的镜像与全部包，在其上重跑 G2；ilverify boot / 包 | B |
| G4 | 性能：56 项 × 5 轮同轮配对（151 口径），对照 Clojure 编译器；单项优化看它针对的基准，整体看全表无可声明回退 | C 每项、D 整体 |
| G5 | 确定性：同一提交构建两次逐字节相同 | B 起 |

IR 不做跨编译器的逐层比较（L1 之后两边的层次本来就不同）；L0 之后靠行为与性能门控，出错时用 `CLEAT_DUMP_IR` 与 `CLEAT_DUMP_LCIL` 并排定位。

**D9 `cleat.nanopass` 保留为用户库**。阶段 E 把 `src/clojure/cleat/nanopass*` 与 `tests/nanopass` 移到 `packages/nanopass`（包程序集，18 / 17 的形状），
由 C# 编译器编译，`NanopassTests` 改为包测试。`cleat.compiler.languages_test` 等只为编译器存在的测试删除。

## §3 中间语言细节

### §3.1 节点

L0 ~ L3 的节点集合与现在 `languages.clj` 相同（`x`、`quote`、`if`、`begin`、`set!`、`lambda`、`clause`、`letrec`、`let`、`call`、`throw`、
`try-finally`、`try-catch`、`label`、`jump`、`uninitialized`、`check-initialized`、`clj-var-ref`、`clj-var-set!`、`clj-def`、`clj-invoke`、
`clj-keyword-invoke`、`clj-static-call`、`clj-case`、`clj-deftype`；L2 加 `box` / `unbox` / `set-box!`；L3 的 lambda 带自由变量表），
只有 `clj-call` 拆开（§3.2）。

### §3.2 `clj-call` 拆成具名节点

现在 `(clj-call "名字" …)` 有两种：codegen 特殊处理的（`CallStatic`、`CallInstance`、`NewOf`、`GetStaticField`、`GetInstanceField`、
`IsInstanceOf`、`PrimInvoke`、`Tagged`、`KnownCall`、`TypedSelf`、`TypedEntry`、`Speculated`、`GetInstanceFieldDirect`、`SetInstanceField`、
`EscapePropagate`、`AwaitAsync`……），和其余一律发射为 `ClojureOps.<名字>` 静态调用的。C# 版：
- 前一种各自成为具名节点（`HostCall`、`HostNew`、`HostField`、`InstanceOf`、`PrimInvoke`、`Tagged`、`KnownCall`……），字段是强类型的；
- 后一种统一为 `RuntimeCall(MethodInfo)`，`MethodInfo` 在分析时从 `ClojureOps` 取得（拼写错误在编 C# 编译器时就暴露，而不是发射时）。

L0 的 S 表达式打印仍按 `clj-call "名字"` 的旧形状输出，保证 G1 与 `.inl` 兼容（D6）。

### §3.3 L4 的表示

`Repr = Obj | Long | Double | Num | Bool | Char | Typed(Type)`。`InferTypes` 是现在 `infer-walk` / `infer-var-types-into!` 的格
（`:fx :fl :l64 :num`）与使用权重；`ChooseRepresentation` 根据推导结果和开关定表示、插入 `Box(repr)` / `Unbox(repr)`、
把宿主调用决议成 `MethodInfo`（决议不了的留 `ReflectiveCall`）。阶段 A 只实现"全部 `Obj`"这一档。

**实际做法（cleat-174 / 179 起，取代上面两段的"新节点"设想）：L4 = L3 的节点 + 按变量的表示表**，不另建节点集合，也不插显式的 `Box` / `Unbox` 节点。
理由：codegen.clj 的表示决策本来就是"按变量查表、发射时按上下文装箱 / 拆箱"，原样移植最能保证同样的决策得到同样的 IL（cleat-179 的验收用
IL 形状逐方法比对）；显式节点要在 L3 上做一遍与发射重复的上下文分析。
- 表（`Lower/InferTypes.cs` 的 `ReprTables`，每个编译单元清空重算）：`VarTypes`（存储的原始类型 `:fx :fl :l64 :num`）、`SoundTypes`（收敛后的类型，
  宿主重载按它选）、`FixedTypes`（原始签名形参、`^long` / `^double` 字段、类型化入口的形参）、`VarFacts`（已知类型、不改存储）、`NumAliases`（Num 形参的副本）、
  `AssignedVars`，以及推测与分层的 `SpecEntries` / `TierClasses`。
- 表示（`Raw = None | L64 | Fl | Fx | Num`）落在变量位置上：`Loc` 的 `TLocal` / `TArg`（原始局部 / 形参）、`NumArg`（Num 形参：装箱用途读原实参、原始用途读入口
  转换出的 Num 局部）；`Big`（方法体超过 2000 个节点）时变量一律 object。`Typed(T)` 仍由 `Tagged` / 宿主静态决议表达（L3 的 `CljCall`），不进 `Raw`。
- `InferTypes` = `InferVarTypesInto`（infer-walk + 不动点 + Num 档）；`ChooseRepresentation` 不是独立 pass，而是发射侧读表的那部分
  （`Lower/RawEmit.cs`：`CgRaw` / `CgRawNum` / `CgRawJoin`、`BindLocal` / `BindNumParams`、jump 的原始临时变量与 keep 集）。
- `CLEAT_DUMP_IR` 不打印这些表；对照手段是 IL 形状（方法表、局部变量类型、IL 长度、方法实现标志）逐方法比较（cleat-179 实施记录）。

### §3.4 L5 的类描述

`ClassDecl { Name, Base, Interfaces, Fields, Methods, Attributes, Source }`、`MethodDecl { Name, Attrs, Params, Ret, Body(L5 语句树) }`。
`EmitIL` 只看 L5，不回头看 L3。L5 的语句树仍是树（不是线性指令），标签、局部、`try` 区域显式，每个节点对应确定的 IL 序列。

## §4 优化的迁移（阶段 C）

每项一个独立 pass（或一组），沿用现在的 `CLEAT_NO_OPT` 记号，开关读取用 cleat-164 的共用助手。验收按 G4，口径同 29 §9。

| 组 | 现在的开关 | C# 里的位置 |
|---|---|---|
| 调用 | `known`、`inline`（`CLEAT_NO_INLINE`）、`loopinl`、`accinline`（默认关）、`crossnstype` | `Analyze` 里的调用点决策（读 `.ifc` / `.inl`）；阶段 A 全关 |
| 前端 | `letrec` | `SimplifyLetrec` |
| 表示 | `infer`、`num`、`joinraw`、`loopweight`、`host`、`boxcache`、`boxdouble`、`charbox` | `InferTypes` / `ChooseRepresentation` |
| 推测 | `typed`、`loopspec`、`arrayspec`、`scalar`、`collapse` | L4 → L4 的独立 pass：`SpeculateEntries`、`SpeculateLoops`、`SpecializeArrays`、`ScalarReplace`、`CollapseEntryLabels`；之后重跑 `InferTypes` |
| 分层 | `imagetier`、`tiered` | `MarkTierClasses`（L5 方法属性） |

现在的结论直接继承，不重新评估：155 闭包提升（语义否决）、154 变长已知元数直发（不达标）、157 调用点反馈（停手）、163 sigspec（已删除）不移植。

**落地位置（cleat-179 / 180，2026-11-04）**——都在 `src/Cleat.Compiler/Lower/`，按 codegen.clj 逐函数移植（每个成员注明对应的 Clojure 函数）：

| 开关 | C# 位置 |
|---|---|
| `infer` `num` `joinraw` `loopweight` | `InferTypes.cs`：`InferVarTypesInto`（infer-walk / infer-var-types-into!）、`ExprType`（带环境）、`CollectAssigned`、`BiggerThan`、`L3Some` |
| `num` `joinraw`（发射）、`boxcache` `boxdouble` `charbox` | `RawEmit.cs`（cg-raw 全档、Num 路径、cg-raw-join!、类型化局部）；装箱助手在 `CodeGen.cs` |
| `host` | `HostCalls.cs`（静态决议，实参类型按 `SoundTypes` / 推断环境） |
| `collapse` `loopspec` `arrayspec` `scalar` | `SpeculateLoops.cs`：`CollapseEntryLabels`、`SpeculateLoops`（每个标签：`ScalarLabel` → 否则 `SpecLabel`，再 `ArraySpecLabel`） |
| `typed` | `SpeculateEntries.cs`：`SpeculateEntries` pass、`CgTypedSelf` / `CgTypedEntry`、`TypedEntryMethod` |
| `imagetier` `tiered` | `Tiering.cs`：`MarkTierClasses`、`TieredMethod`（*tiered-method*）、`ArgTake`；`FnClasses.cs` 给分层类的方法加 `Tiered`（转发 / 桥方法除外）；`Emit/IlEmitter.cs` 的 `TakeLoadsFor`（Cleat.Emit 的同名逻辑） |

pass 顺序在 `FnClasses.cs` 的 `RunReprPasses`（generate-unit）：`MarkTierClasses`（eval 或 `imagetier`）→ `InferTypes`（只推带循环 / `^long` 形参的形式，标志由
`CsDriver.CompileUnit` 按 LowerLoops 的 label 数与 typed-clauses 算）→ `CollapseEntryLabels`（改了就清表重推）→ `SpeculateEntries` → `SpeculateLoops`
（改了就清表重推，类型化入口的体按签名固定形参后推断）。推测 pass 与原版一样读写同一组表（试算推断的结果也写进去，只涉及新名字）。

## §5 对进行中任务的影响

| 任务 | 处理 |
|---|---|
| cleat-165 codegen 拆分 | **取消**，由 M20 取代（L4 / L5 就是拆分的目标形状） |
| cleat-162 CI | 照常收口；种子守卫在 cleat-182 换成确定性守卫（G5） |
| cleat-166 ~ 168 | 不受影响（Clojure 库与评估），可与 M20 并行 |
| cleat-169 M19 收口 | 不受影响；不依赖 M20 |
| cleat-170 | 已完成实现，111 复核照常 |

## §6 风险

| 风险 | 后果 | 对策 |
|---|---|---|
| 分析器语义偏差（宏展开顺序、`&env`、分析期副作用、错误阶段分类） | 大量细小的行为差异，难以定位 | G1 L0 差分在 A2 常驻；错误黄金文件进 G2 |
| 阶段 C 优化补不齐，整体性能回退 | 阶段 D 无法切换 | 每项单独验收；整体门控只在 D 收；Clojure 编译器在 D 之前一直是默认 |
| 双轨期间两边都要修缺陷 | 工作量翻倍 | §1 双轨纪律；尽量缩短 A ~ D |
| C# 节点 + 重写基类样板过多 | pass 难写难读 | D3 的 Source Generator 在 A1 先做；节点约 40 种，规模可控 |
| 镜像构建耗时变化 | `dotnet build` 变慢 | B 时记录；C# 编译器可 R2R（160 的结论只针对镜像与包，编译器单独评估） |
| `.inl` 绑死 L0 的 S 表达式形状 | L0 想改形状时受限 | D6 只承诺到阶段 E；之后可以带版本号换格式 |

## §7 任务

| ID | 标题 | 阶段 | 依赖 |
|---|---|---|---|
| cleat-171 | `Cleat.Compiler` 骨架、IR 节点与 Source Generator、校验器、打印 / 读入、`CLEAT_COMPILER` 开关 | A1 | 无 |
| cleat-172 | C# 分析器：形式 → L0（宏展开、决议、特殊形式、分析期副作用、宿主重载选择）+ G1 差分 | A2 | 171 |
| cleat-173 | 前中端 pass：LowerLoops、SimplifyLetrec、CheckForward、Cp0Lite、SimplifyLets、ConvertAssignments、UncoverFree | A3 | 172 |
| cleat-174 | 基础下降与发射：L4 全 Obj、ConvertClosures、LiftTry、ExplicitLocals、EmitIL 两阶段；eval 路径跑通 | A4 | 173 |
| cleat-175 | deftype / reify / definterface / proxy / gen-class、case、try、async、宿主互操作静态决议 | A5 | 174 |
| cleat-176 | 持久化：compile-boot / compile-package / `*compile-files*`、`.ifc` / `.inl`、PDB 与序列点、lazy-start、确定性 | A6 | 175 |
| cleat-177 | 功能切换点：C# 编译器编镜像与全部包，G2 / G3 / G5 全过 | B | 176 |
| cleat-178 | 优化·调用：known、inline、loopinl、accinline、crossnstype | C | 177 |
| cleat-179 | 优化·表示：InferTypes / ChooseRepresentation（infer、num、joinraw、loopweight、host、boxcache、boxdouble、charbox） | C | 177 |
| cleat-180 | 优化·推测与分层：typed、loopspec、arrayspec、scalar、collapse、imagetier / tiered | C | 179 |
| cleat-181 | 默认切换：整体 G4、默认 `CLEAT_COMPILER=cs` | D | 178 ~ 180 |
| cleat-182 | 删除：Clojure 编译器、种子与自举、Lcil 读取器、相关测试；nanopass 移到 packages/；文档归档（00 / 04 / 05 / 08 / 19 / README） | E | 181 |

关键路径：171 → 172 → 173 → 174 → 175 → 176 → 177 → 179 → 180 → 181 → 182；178 可与 179 / 180 并行。

## 附：阶段 D 的实际做法（cleat-181，2026-11-05）

`dotnet build` 编镜像：宿主 `--no-boot`（不加载镜像）由 C# 编译器从 src/clojure 编出 stage1，再在 stage1 上编出 src/boot。C# 编译器的输出与起步镜像无关，
第二遍即最终结果（跨机器、跨 .NET 补丁版本逐字节相同）。从零那一遍的两处让步只在没有起步镜像时生效：clojure.core 的前向引用先 declare、失败的 inliner 退回普通调用。
Clojure 编译器包也由 C# 编译器编。整体 G4：111 x0.988（唯一可声明回退 mbrot x1.055 为环境效应、IL 逐字节相同，用户裁决接受）、本机 x0.963（0 回退）。见 cleat-181。

## 附：阶段 E 的实际做法（cleat-182，2026-11-05）

Clojure 编译器、编译器包、种子与自举命令、发射器 Cleat.Emit 删除；程序集名改回 `Cleat.Compiler`；`cleat.nanopass` 移到 `packages/nanopass`。
G5 由 Host 的 `DeterminismTests`（从零两步 = 构建产物、不动点；PR CI 的确定性守卫）与 `tools/build.cs image-set --twice --ilverify`（整套产物，nightly）承担。
全新克隆 `dotnet build` 一步 15 秒得到镜像。M20 完成。见 cleat-182。
