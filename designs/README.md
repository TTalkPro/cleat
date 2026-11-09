# 设计文档索引

Cleat：.NET 上的 Clojure 实现。运行时与编译器都用 C#（编译器见 [30](30-csharp-compiler.md)：nanopass 式小 pass、L0 ~ L5 逐层下降、ILGenerator 直接发射），
Clojure 镜像由编译器从源码两步编出，没有种子、不需要自举，只依赖 .NET SDK；后端是 CIL，LLVM 后端设计保留、实现搁置。

**2026-11-05 现状（cleat-182，M20 完成）**：Clojure 写的编译器、编译器包 `cleat.compiler.dll`、文本种子与自举、发射器 Cleat.Emit 及其 S 表达式 CIL 格式
全部删除；`cleat.nanopass` 移到 `packages/nanopass` 作为用户库。04 / 05 / 08 / 19 因此是**历史**（开头有注记），现行编译器设计只看 30。
（2026-11-03 的方向变更见 30；M8 ~ M19 期间编译器是 Clojure 写的、靠种子自举，下面各文档里的这类描述都是那时的记录。）

2026-10-15 的现状：M8（Clojure 自寄宿，[19](19-clojure-self-hosting.md)）与 M9（质量与收口，cleat-096 ~ 099）完成，Scheme 元层全部删除。00 ~ 09 写于更早的阶段，其中 stage0 解释器、C# 展开器、Scheme 元层等已被 19 取代，各文件开头的注记说明了哪些部分仍有效。

| 文档 | 内容 |
|------|------|
| [00-overview.md](00-overview.md) | 目标、约束、总体架构、关键决策 D1 ~ D16、仓库布局、里程碑、风险 |
| [01-reference-analysis.md](01-reference-analysis.md) | 蓝本分析：IronScheme、ClojureCLR、jank、jolt、nanopass 各取什么、不取什么；许可事实 |
| [02-runtime.md](02-runtime.md) | 运行时 `Cleat.Core`：值表示、Var、函数模型、原语表、钩子、reader、无 DLR 的互操作、加载器、从 ClojureCLR 提取的清单 |
| [03-language-core.md](03-language-core.md) | 核心语法范围、C# 展开器、Lcore 文法、与 Clojure 的偏离、core 库分层与加载顺序 |
| [04-compiler.md](04-compiler.md) | **历史**（现行见 30）：nanopass 编译器：L0 ~ L3、pass 链、机器描述表、原语编译、直接链接、互操作节点、`deftype`、驱动 |
| [05-cil-backend.md](05-cil-backend.md) | **历史**（发射器 cleat-182 删除，现行见 30）：CIL 后端：继承 IronScheme 的文本 CIL 与发射器，扩展项、常量编码、代码生成约定、决议规则、加载约定 |
| [06-llvm-backend.md](06-llvm-backend.md) | LLVM IR 后端：AOT 交叉编译、C 内核运行时、用 Clojure 写的原生集合、对象模型与调用约定、M0 验证项 |
| [07-nanopass-port.md](07-nanopass-port.md) | 表面设计 D1 ~ D6 由 19 重新启用（stage0 前提的部分仍失效）：nanopass 框架移植到 Clojure：表面语法、上下文切换、注册表、运行时表示、验证 |
| [cleat.nanopass 用户文档](../packages/nanopass/README.md) | 定义语言、写 pass、模式与模板的写法、与上游 nanopass 的对照表（cleat-094） |
| [08-bootstrap.md](08-bootstrap.md) | **历史**（种子与自举 cleat-182 删除，现行构建见 30 D8 与 [docs/boot.md](../docs/boot.md)）：§0 是 Clojure 编译器的文本种子、stage1、改名的工具编译器、不动点判据（cleat-091 ~ 181）；其余节是 stage0 解释器时期的记录 |
| [09-testing.md](09-testing.md) | 符合性判据（jolt 语料、clojure-test-suite、test_clojure）、测试分层、基线 |
| [15-scheme-core-vendoring.md](15-scheme-core-vendoring.md) | **历史**（被 19 取代，元层已在 cleat-093 删除）：纳入 IronScheme 元层的取舍与更名、可扩展语法对象协议、try / catch 异常模型、为何 nanopass 不改、新里程碑 M2′ / M4′ / M5′ |
| [14-three-layer-architecture.md](14-three-layer-architecture.md) | **历史探讨**（元层已删除）：VM / Scheme 元层 / Clojure 三层架构的四个结构性问题与修正、剩余风险 |
| [13-syntax-objects-and-scheme-core.md](13-syntax-objects-and-scheme-core.md) | **历史探讨**（元层已删除）：语法对象重塑展开流程、驱动 nanopass 的最小 Scheme 元语言、"Clojure on IronScheme"方案 C 与 A / B 的取舍、PoC 计划 |
| [16-nanopass-host-survey.md](16-nanopass-host-survey.md) | **调研**：nanopass 宿主语言对比（SBCL/CLOS、Erlang、Clojure），pass 分发本质取证，ellipsis 难度定位，07 号文的可行性背书 |
| [17-lazy-runtime-startup.md](17-lazy-runtime-startup.md) | **实现**：包程序集（每包一个、命名空间索引、去掉源码旁 `.clj.dll`）、运行时 NuGet 包 `Cleat.Runtime`、生成类型惰性启动 CleatRuntime，让 C# / F# 直接引用 cleat 的 NuGet 包（cleat-067） |
| [19-clojure-self-hosting.md](19-clojure-self-hosting.md) | **历史**（2026-10-14 ~ 11-03 的方向，被 30 取代，Clojure 编译器 cleat-182 删除）：Clojure 版 nanopass 与编译器、分级差分门控、自举不动点与文本种子、生成代码去 Scheme、删除 Scheme 元层；任务 cleat-081 ~ 095，收口 cleat-096 ~ 099（M9） |
| [18-tools-build.md](18-tools-build.md) | **实现**：tools.build 包（`cleat.edn` 的 NuGet / 本地依赖、`dotnet restore` 解析的 basis、编包程序集、`dotnet-build`、`pack` / `install` / `push`、可部署目录）与 tools.namespace 的部分移植（cleat-066） |
| [20-skeg-optimizations.md](20-skeg-optimizations.md) | M10（完成）：skeg iron-060 ～ 087 逐条对照、cleat 实测差距（反射构造 / 属性、无提示算术装箱）、Num 与双局部的 Clojure 语义改法、循环推测、类型化入口；任务 cleat-100 ～ 105 |
| [21-r7rs-gaps.md](21-r7rs-gaps.md) | **现行方向**（2026-10-15 起，M11）：cleat-107 的 r7rs 对比（cleat / JVM 几何平均 4.52）找出的慢区与任务 cleat-108 ～ 115 |
| [22-gc-and-performance-followups.md](22-gc-and-performance-followups.md) | **提案**（2026-10-16）：M11 之后的 GC 与性能跟进——tier-0 / OSR 生命周期、原始值贯通的类型化调用、本方法内 throw → leave、有限标量替换、字符串局部优化与非本地逃逸协议 |
| [23-type-specialization.md](23-type-specialization.md) | **提案**（2026-10-16，M13）：按参数类型特化——无提示数组形参的推测（类型化入口扩展到数组）、运行时类型反馈特化（剖面记录、后台编译、守卫与退回）；任务 cleat-127 ～ 130 |
| [24-runtime-signature-specialization.md](24-runtime-signature-specialization.md) | **提案**（2026-10-18，M14）：运行时签名特化——编译期 IR 保留、invoke 入口签名观察与查表、PersistedAssemblyBuilder 按需生成类型化 CIL、任意类型 / 任意参数个数；任务 cleat-133 ~ 135 |
| [25-runtime-reference-signature-specialization.md](25-runtime-reference-signature-specialization.md) | **实现**（2026-10-18，M14）：引用类型运行时签名特化——签名仍用 `L/D/O`、精确 `Type[]` 分发、特化体 `Tagged` 静态决议；任务 cleat-134 |
| [26-runtime-signature-tables.md](26-runtime-signature-tables.md) | **实现**（2026-10-18，M14）：运行时签名表与高元数特化——5～20 参候选、多签名补生成、每函数与进程生成上限；任务 cleat-135 |
| [27-jit-style-runtime-specialization.md](27-jit-style-runtime-specialization.md) | **现行方向**（2026-10-19，M15）：JIT 式运行时重新编译——删除 LL / DD 预生成、单一签名 key 与最优路径查表、含原生类型形参的函数整体特化并以 object 兜底、编译期调用路径类型推导；任务 cleat-137 ~ 141 |
| [28-hot-path-optimizations.md](28-hot-path-optimizations.md) | **进行中**（2026-10-20，M16；§2.1 / §2.2 / §2.4 已实现并远端验证）：M15 归因复核（shapes3 推翻 nth 22x / 分配 12x 论据，LICM / EA 伪影）后的修正优先级——char 常量装箱、String seq / first 直读、跨 ns deftype 决议与访问器内联、keyword intern（P0），nth / 闭包 / 变长（P1），GC profile（P2），回归排查与测量口径（P3）；任务 cleat-142 ~ 150 |
| [29-usability-and-engineering.md](29-usability-and-engineering.md) | **完成**（2026-10-26 提案，2026-11-08 收口，M19；162 待 GH 实测）：热路径微优化到噪声地板后转向用户可感知的缺口——源位置与栈帧、启动（剖析 / ReadyToRun / 编译器延迟加载）、CI、sigspec 整体消融与开关清理、codegen 拆分、spec.alpha / core.async、jvm-compat 评估；任务 cleat-158 ~ 170 |
| [30-csharp-compiler.md](30-csharp-compiler.md) | **现行方向**（2026-11-03 起，M20 于 2026-11-05 完成，取代 19 的自寄宿）：编译器改用 C#——小 pass、L0 ~ L3 保持、新增 L4 表示层与 L5 类层、ILGenerator 两阶段直接发射；并行建设 + 门控（G1 L0 差分 ~ G5 确定性）、优化逐项补、删除自举 / 种子 / 文本 CIL、nanopass 转为用户库；任务 cleat-171 ~ 182 |

任务索引见仓库根目录 [TASK.md](../TASK.md)。

## 附表：开关与环境变量清单（cleat-164）

`CLEAT_NO_OPT` 与下表全部变量都在进程启动时读一次（类型初始化时）。
读取点：`src/Cleat.Core/Runtime/OptFlags.cs`（`OptFlags.Off` 启动读一次；`OffNow` 供 GcProfile 的
进程内诊断测试现读），编译器（`Cleat.Compiler`）与运行时都经它。匹配自 cleat-164 起为**逗号 / 空白分词后的精确匹配**
（旧子串匹配会让 `CLEAT_NO_OPT=accinline` 顺带关掉 `inline`）；`=1` 的"全关"语义不变。cleat-164 时 `Cleat.Emit` 的 `tiered`
是子串匹配的例外，C# 编译器（IlEmitter）改经 OptFlags 后例外消失。改名不做，历史 notes 的命令仍可复现。
cleat-182 删除了 `CLEAT_COMPILER`、`CLEAT_COMPILER_NS`、`CLEAT_CS_ANALYZER` 与 Clojure 编译器的两个转储变量，以及没有读取处的记号 `inline`（原为小函数内联，旧 codegen 删除前已只定义不用；调用点内联用 `CLEAT_NO_INLINE` / `-g` 关）；下表"读取处"一栏的
analyzer / codegen / front 指 C# 编译器里对应的部分（Analyzer、Lower、Passes/Front），名字沿用原 Clojure 编译器的文件名。

### 环境变量（21 个）

| 变量 | 默认 | 用途 | 类别 | 引入 / 归属 |
|---|---|---|---|---|
| `CLEAT_NO_OPT` | 未设 | `=1` 关全部优化；否则精确匹配记号（见下表） | 测量 | cleat-042 起 |
| `CLEAT_OPT_ACCINLINE` | 未设（关） | 打开小访问器跨编译单元内联；与 `CLEAT_NO_OPT=accinline` 同设时关优先 | 实验（默认关） | cleat-145 |
| `CLEAT_NO_INLINE` | 未设 | 调用点内联 / 直接链接 / KnownCall 缺省关（`-g` 设它；原为影子模式 G1 的接口文件比较） | 诊断 | cleat-090 |
| `CLEAT_NO_SIMPLE_EVAL` | 未设 | 关掉 161 的平凡形式快速求值（语义逐字回到 161 前） | 语义 | cleat-161 |
| `CLEAT_TIME` | 未设 | 分段计时（host / runtime / image / packages / compiler 等），不设不计时不输出 | 诊断 | cleat-159 |
| `CLEAT_TRACE_ERRORS` | 未设 | 错误时打印内部栈跟踪 | 诊断 | cleat-055 |
| `CLEAT_TRACE_LOAD` | 未设 | 打印加载过程 | 诊断 | cleat-053 |
| `CLEAT_DEBUG_EVAL` | 未设 | eval 走调试发射（`-g`，关跨 fn 内联、保留序列点） | 诊断 | cleat-158 |
| `CLEAT_IR_CHECK` | 未设 | C# 编译器每个 pass 前后按层校验 IR（非空且不为 `0` 时开） | 诊断 | cleat-171 |
| `CLEAT_DUMP_IR` | 未设 | C# 编译器转储指定 pass 之后的 IR（逗号 / 空白分隔的 pass 名，不区分大小写；`input` 为管线输入，`*` / `all` 为全部） | 诊断 | cleat-171 |
| `CLEAT_DUMP_IR_FILE` | 未设（标准错误） | `CLEAT_DUMP_IR` 的输出追加到该文件 | 诊断 | cleat-171 |
| `CLEAT_GC_PROFILE` | 未设 | 启用 GC profile 计数器（`gcprofile` 记号仍可单独关） | 诊断 | cleat-148 |
| `CLEAT_LOAD_PATH` / `-I` | 未设 | 源码加载路径 | 语义 | cleat-012 |
| `CLEAT_BOOT` | 未设 | 覆盖镜像目录的加载位置 | 语义 | cleat-067 |
| `CLEAT_RUNTIME_ASYNC` | 未设（开） | `=0` 强制异步支持退化为同步实现 | 语义 | cleat-043 |
| `CLOJURE_SPEC_CHECK_ASSERTS` | 未设（关） | `true` 时 `s/assert` 检查（`RT.checkSpecAsserts`；`s/check-asserts` 运行时改）；只在用了 packages/spec.alpha 时有意义 | 语义 | cleat-166（ClojureCLR 同名） |
| `CLOJURE_SPEC_SKIP_MACROS` | 未设（检查） | `true` 时关掉宏的 spec 检查（`RT.instrumentMacros`）；检查只在加载了 clojure.spec.alpha 之后才做 | 语义 | cleat-166（ClojureCLR 同名） |
| `CLEAT_CROSSGEN2` | 未设 | 构建脚本指定 crossgen2 路径 | 工具 | cleat-160 |

### `CLEAT_NO_OPT` 记号（23 个）

| 记号 | 关掉什么 | 读取处 | 引入 |
|---|---|---|---|
| `known` | 宿主调用的编译期静态决议 | analyzer（cleat-172 起 C#） | cleat-042 / 110 |
| `infer` | 类型推导（infer-walk） | codegen | cleat-100 |
| `host` | 宿主调用决议 / 反射警告 | codegen（narrow 语义对照） | cleat-101 |
| `num` | Num：未定类型数的局部 / 循环 / 形参 | codegen | cleat-102 |
| `loopspec` | 循环 long / double 快慢两份推测 | codegen | cleat-103 |
| `typed` | 无提示函数的类型化入口 | codegen | cleat-104 |
| `joinraw` | 原始用途穿过 if / begin 尾位置 | codegen | cleat-108 |
| `boxcache` | long 装箱走 LongBox | codegen | cleat-109 |
| `boxdouble` | double 经 LongBox.Double 装箱 | codegen | cleat-110 |
| `scalar` | 循环变量 deftype 标量替换 | codegen | cleat-123 |
| `collapse` | 折叠内联子句的 arity 标签 | codegen | cleat-123 |
| `arrayspec` | 无提示数组形参按元素类型推测 | codegen | cleat-127 |
| `loopweight` | 循环内层重复装箱的权重 | codegen | cleat-110 |
| `imagetier` | 镜像 / 包里函数类的分层编译规则 | codegen | cleat-117 |
| `charbox` | char 装箱与常量走 CharBox | codegen | cleat-142 |
| `accinline` | 小访问器跨编译单元内联（默认关，见 `CLEAT_OPT_ACCINLINE`） | analyzer | cleat-145 |
| `loopinl` | 循环体内小函数内联 | analyzer | cleat-120 |
| `crossnstype` | 生成类跨 ns 决议（退回旧决议路径） | analyzer + RT | cleat-144 |
| `letrec` | front 的 const-in-run（letrec 前向变量常量绑定） | front | cleat-084 |
| `intern` | keyword / str 的 intern 快路径（退回先建 Symbol） | Cleat.Core | cleat-146 |
| `strseq` | String 的 first / seq / next / more 直读 | Cleat.Core | cleat-143 |
| `nthfast` | nth 的 ASeq / LazySeq / StringSeq 快路径 | Cleat.Core | cleat-147 |
| `gcprofile` | GC profile 计数器（宿主侧） | Cleat.Host | cleat-148 |
| `tiered` | eval 代码退回 RunAndCollect | Cleat.Compiler（IlEmitter；cleat-182 前在 Cleat.Emit） | cleat-114 |
