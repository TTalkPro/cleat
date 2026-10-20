# 设计文档索引

Cleat：.NET 上的 Clojure 实现。运行时 C#，编译器用 Clojure 写（`cleat.nanopass`）并已自举，信任根是入库的 Lcil 文本种子（[08 §0](08-bootstrap.md)），只依赖 .NET SDK；后端是 CIL，LLVM 后端设计保留、实现搁置。

2026-10-15 的现状：M8（Clojure 自寄宿，[19](19-clojure-self-hosting.md)）与 M9（质量与收口，cleat-096 ~ 099）完成，Scheme 元层全部删除。00 ~ 09 写于更早的阶段，其中 stage0 解释器、C# 展开器、Scheme 元层等已被 19 取代，各文件开头的注记说明了哪些部分仍有效。

| 文档 | 内容 |
|------|------|
| [00-overview.md](00-overview.md) | 目标、约束、总体架构、关键决策 D1 ~ D16、仓库布局、里程碑、风险 |
| [01-reference-analysis.md](01-reference-analysis.md) | 蓝本分析：IronScheme、ClojureCLR、jank、jolt、nanopass 各取什么、不取什么；许可事实 |
| [02-runtime.md](02-runtime.md) | 运行时 `Cleat.Core`：值表示、Var、函数模型、原语表、钩子、reader、无 DLR 的互操作、加载器、从 ClojureCLR 提取的清单 |
| [03-language-core.md](03-language-core.md) | 核心语法范围、C# 展开器、Lcore 文法、与 Clojure 的偏离、core 库分层与加载顺序 |
| [04-compiler.md](04-compiler.md) | nanopass 编译器：L0 ~ L3、pass 链、机器描述表、原语编译、直接链接、互操作节点、`deftype`、驱动 |
| [05-cil-backend.md](05-cil-backend.md) | CIL 后端：继承 IronScheme 的 Lcil 与发射器，扩展项、常量编码、代码生成约定、决议规则、加载约定 |
| [06-llvm-backend.md](06-llvm-backend.md) | LLVM IR 后端：AOT 交叉编译、C 内核运行时、用 Clojure 写的原生集合、对象模型与调用约定、M0 验证项 |
| [07-nanopass-port.md](07-nanopass-port.md) | 表面设计 D1 ~ D6 由 19 重新启用（stage0 前提的部分仍失效）：nanopass 框架移植到 Clojure：表面语法、上下文切换、注册表、运行时表示、验证 |
| [cleat.nanopass 用户文档](../src/clojure/cleat/nanopass/README.md) | 定义语言、写 pass、模式与模板的写法、与上游 nanopass 的对照表（cleat-094） |
| [08-bootstrap.md](08-bootstrap.md) | 自举：**§0 现行**——Lcil 文本种子、stage1、改名的工具编译器、不动点判据与种子纪律（cleat-091）；其余节是 stage0 解释器时期的记录 |
| [09-testing.md](09-testing.md) | 符合性判据（jolt 语料、clojure-test-suite、test_clojure）、测试分层、基线 |
| [15-scheme-core-vendoring.md](15-scheme-core-vendoring.md) | **历史**（被 19 取代，元层已在 cleat-093 删除）：纳入 IronScheme 元层的取舍与更名、可扩展语法对象协议、try / catch 异常模型、为何 nanopass 不改、新里程碑 M2′ / M4′ / M5′ |
| [14-three-layer-architecture.md](14-three-layer-architecture.md) | **历史探讨**（元层已删除）：VM / Scheme 元层 / Clojure 三层架构的四个结构性问题与修正、剩余风险 |
| [13-syntax-objects-and-scheme-core.md](13-syntax-objects-and-scheme-core.md) | **历史探讨**（元层已删除）：语法对象重塑展开流程、驱动 nanopass 的最小 Scheme 元语言、"Clojure on IronScheme"方案 C 与 A / B 的取舍、PoC 计划 |
| [16-nanopass-host-survey.md](16-nanopass-host-survey.md) | **调研**：nanopass 宿主语言对比（SBCL/CLOS、Erlang、Clojure），pass 分发本质取证，ellipsis 难度定位，07 号文的可行性背书 |
| [17-lazy-runtime-startup.md](17-lazy-runtime-startup.md) | **实现**：包程序集（每包一个、命名空间索引、去掉源码旁 `.clj.dll`）、运行时 NuGet 包 `Cleat.Runtime`、生成类型惰性启动 CleatRuntime，让 C# / F# 直接引用 cleat 的 NuGet 包（cleat-067） |
| [19-clojure-self-hosting.md](19-clojure-self-hosting.md) | **现行方向**（2026-10-14 起，M8 完成）：Clojure 版 nanopass 与编译器、分级差分门控、自举不动点与 Lcil 文本种子、生成代码去 Scheme、删除 Scheme 元层；任务 cleat-081 ~ 095，收口 cleat-096 ~ 099（M9） |
| [18-tools-build.md](18-tools-build.md) | **实现**：tools.build 包（`cleat.edn` 的 NuGet / 本地依赖、`dotnet restore` 解析的 basis、编包程序集、`dotnet-build`、`pack` / `install` / `push`、可部署目录）与 tools.namespace 的部分移植（cleat-066） |
| [20-skeg-optimizations.md](20-skeg-optimizations.md) | M10（完成）：skeg iron-060 ～ 087 逐条对照、cleat 实测差距（反射构造 / 属性、无提示算术装箱）、Num 与双局部的 Clojure 语义改法、循环推测、类型化入口；任务 cleat-100 ～ 105 |
| [21-r7rs-gaps.md](21-r7rs-gaps.md) | **现行方向**（2026-10-15 起，M11）：cleat-107 的 r7rs 对比（cleat / JVM 几何平均 4.52）找出的慢区与任务 cleat-108 ～ 115 |
| [22-gc-and-performance-followups.md](22-gc-and-performance-followups.md) | **提案**（2026-10-16）：M11 之后的 GC 与性能跟进——tier-0 / OSR 生命周期、原始值贯通的类型化调用、本方法内 throw → leave、有限标量替换、字符串局部优化与非本地逃逸协议 |
| [23-type-specialization.md](23-type-specialization.md) | **提案**（2026-10-16，M13）：按参数类型特化——无提示数组形参的推测（类型化入口扩展到数组）、运行时类型反馈特化（剖面记录、后台编译、守卫与退回）；任务 cleat-127 ～ 130 |
| [24-runtime-signature-specialization.md](24-runtime-signature-specialization.md) | **提案**（2026-10-18，M14）：运行时签名特化——编译期 IR 保留、invoke 入口签名观察与查表、PersistedAssemblyBuilder 按需生成类型化 CIL、任意类型 / 任意参数个数；任务 cleat-133 ~ 135 |
| [25-runtime-reference-signature-specialization.md](25-runtime-reference-signature-specialization.md) | **实现**（2026-10-18，M14）：引用类型运行时签名特化——签名仍用 `L/D/O`、精确 `Type[]` 分发、特化体 `Tagged` 静态决议；任务 cleat-134 |
| [26-runtime-signature-tables.md](26-runtime-signature-tables.md) | **实现**（2026-10-18，M14）：运行时签名表与高元数特化——5～20 参候选、多签名补生成、每函数与进程生成上限；任务 cleat-135 |
| [27-jit-style-runtime-specialization.md](27-jit-style-runtime-specialization.md) | **现行方向**（2026-10-19，M15）：JIT 式运行时重新编译——删除 LL / DD 预生成、单一签名 key 与最优路径查表、含原生类型形参的函数整体特化并以 object 兜底、编译期调用路径类型推导；任务 cleat-137 ~ 141 |
| [28-hot-path-optimizations.md](28-hot-path-optimizations.md) | **提案**（2026-10-20，M16）：M15 归因复核（shapes3 推翻 nth 22x / 分配 12x 论据，LICM / EA 伪影）后的修正优先级——char 常量装箱、String seq / first 直读、跨 ns deftype 决议与访问器内联、keyword intern（P0），nth / 闭包 / 变长（P1），GC profile（P2），回归排查与测量口径（P3）；任务 cleat-142 ~ 150 |

任务索引见仓库根目录 [TASK.md](../TASK.md)。
