# cleat-177 功能切换点：C# 编译器编镜像与全部包，功能门控全过

- 里程碑：M20　依赖：cleat-176　状态：完成（2026-11-05；G2 / G3 / G5 全过，见实施记录）
- 设计依据：[30 §1 阶段 B、§2 D8](../designs/30-csharp-compiler.md)、[29 §9](../designs/29-usability-and-engineering.md)

## 工作内容

1. `tools/build.cs` 增加 `cs-image`：用 C# 编译器编镜像、编译器之外的随附库（`compile-libs`）与 packages/ 下全部包，输出到独立目录。
2. 在该目录上跑 G2：Core / Host 全量、conformance `--gate`、CTS / test-clojure 基线、错误黄金文件；ilverify boot 与全部包（G3）。
3. G5：`cs-image` 连跑两次逐字节相同。
4. 记录：镜像构建耗时（与 `--compile-boot` 现状对比）、首次 eval 耗时、`-e nil` 启动（cleat-159 的 `CLEAT_TIME` 分段）。
5. r7rs 56 项跑 1 轮，记下与 Clojure 编译器的差距作为阶段 C 的起点（本任务不设性能门槛）。

## 验收

- G2 / G3 / G5 全过；基线只允许变好（变好的项更新基线文件并在任务书列出）。
- 性能差距表写入本任务书，按 30 §4 的组归因到待补的优化。

## 前置修补（2026-11-05，cleat-172 ～ 180 合并后的 Host 全量测试）

`CLEAT_COMPILER=cs` 下 Host 全量第一次 274 / 291。修了下面这些（都不涉及 src/clojure、src/Cleat.Emit、src/boot）：

- `CsDriver.EvalToplevel`：宏展开结果不是序列时（`(and)` → `true`）对它调 `RT.seq` 抛异常；改为同 driver.clj 的 `seq?` 判断。conformance `--gate` 的 7 个新失败由此而来。
- 简单形式的解释：移植 driver.clj 的 `simple?` / `atomic?` / `seval` 为 `Driver/SimpleEval.cs`，`EvalForm` 先判断、通过则解释（计入 `DriverOps.CountSimple`），否则编译（`CountCompiled`）。
- tools.build `compile-clj` 生成的脚本原来直接调 Clojure 驱动的 var `cleat.compiler.driver/compile-package`：cs 下段落记进 Clojure 驱动的状态、C# 的求值钩子不往里写，包程序集是空的。
  改为 `(Cleat.Core.Runtime.Startup/EnsureCompiler)` 后经宿主钩子 `DriverOps/CompilePackageHook` 调用，两个编译器都可。demo 包在两种模式下编出的类型与程序集特性相同。
- `Cleat.Runtime` 包（嵌入用）加入 `Cleat.Compiler.CSharp`（`Cleat.Runtime.Package.csproj` 的 ProjectReference）；dotnet tool 包经宿主的引用本来就有。
- 栈过滤（`Diagnostics.IsClojureFrame`）也去掉 C# 编译器的帧 `Cleat.Compiler.*`；`ErrorGoldenTests.StackTracesHideCompilerFramesUnlessAskedFor` 两个驱动的帧名都认。

修补后：缺省模式 Host 291 / 291、Core 125 / 125；cs 模式 Host 283 / 291，余下 8 项：

| 测试 | 原因 | 去向 |
|---|---|---|
| SelfhostReachesAFixedPoint | stage2 / stage3 镜像逐字节相同，之后读 `cleat.boot.lcil` 失败（C# 编译器不产生 Lcil） | 182 删自举时改写或删除 |
| AccessorInliningCrossCompileUnits、ArrayAccessIsResolvedStatically、JoinRawUses、LoopSpeculation、NumUnboxedArithmetic、TypedEntries | 读 Lcil 转储文件断言指令形状 | 改成断言 IL / 行为（本任务或 181） |
| CoreFunctionsKeepLazySeqsCollectable | 镜像（仍由 Clojure 编译器编）里的核心函数停在 tier-0 时抓住惰性序列的头；缺省模式被 Clojure 编译器自身的运行预热掩盖（见 cleat-180 实施记录） | 用 C# 编译器编镜像时定位 |

测试环境的坑：`NuGetColdLoadTests` 的 fixture 用隔离的 `NUGET_PACKAGES` 跑 `tools/build.cs pack`，会把仓库里各项目的 `obj/project.assets.json` 改成指向 fixture 的临时包目录，
teardown 删掉后，同一棵树上之后的 `dotnet test` 不报错、退出码 0、什么都不跑。连着跑多轮全量时，每轮之前 `dotnet restore Cleat.slnx`。

## 实施记录（2026-11-05）

### 1. `tools/build.cs cs-image`

`dotnet run tools/build.cs -- cs-image [目录] [--twice] [--ilverify]`，全程 `CLEAT_COMPILER=cs`，输出缺省 `src/boot-cs/`（与 `src/boot/` 同层，宿主按开发布局对待：
`../clojure` 上加载路径、tools 从 packages/ 的源码，测试环境与 src/boot 一致；已加进 .gitignore）：

| 步骤 | 产物 | 耗时（一次的典型值） |
|---|---|---|
| image | `cleat.boot.{dll,pdb,ifc,inl}` | 6.5 ~ 7.6 s |
| libs | `Cleat.Clojure.Libs.dll`（4 个命名空间，同 compile-libs） | 1.3 ~ 1.6 s |
| tools.namespace / tools.build | `tools/Cleat.ToolsNamespace.dll`、`tools/Cleat.ToolsBuild.dll` | 1.3 / 2.0 s |
| nrepl / neorepl | `packages/Cleat.Nrepl.dll`、`packages/Cleat.Neorepl.dll` | 3.5 / 3.0 s |
| 合计 | 14 个文件 | 18 ~ 20 s |

**不需要自举**：从 Clojure 编译器编的镜像起步与从 C# 编译器编的镜像起步（再从后者起步一次），C# 编译器编出的镜像、.ifc、.inl 逐字节相同——输出与起步镜像无关。

`tools/build.cs` 同时改了"当前镜像目录"的口径：`srcBootDir` 固定是 src/boot（boot-order.edn、种子、Clojure 编译器包与自举），`bootDir` 是起作用的镜像目录
（设了 `CLEAT_BOOT` 时是它所在的目录）；compile-libs / compile-tools / pack / publish / r2r 针对 `bootDir` 并显式传 `--boot`，pack 经 `-p:CleatPackBootDir` 把它交给
Cleat.Runtime.Package 与 Cleat.Host（工具包）的 csproj，`cleat.compiler.dll` 按存在与否打包。原来在 `CLEAT_BOOT` 指向别的镜像时 pack 会按那个镜像编 src/boot 的随附库
（require 命中镜像旁已编好的包、源码不加载，编出 3.5 KB 的空包），G2 第一次就踩到，已修并重编 src/boot 的随附库。`selfhost` 固定 `CLEAT_COMPILER=clj`（Clojure 编译器自己的自举）。

### 2. G2（`CLEAT_COMPILER=cs CLEAT_BOOT=src/boot-cs/cleat.boot.dll`）

- Core 125 / 125。
- Host 291 项：全量 290 / 291，唯一失败 `PublishedDirectoryRunsStandalone`（硬要求发布目录有 cleat.compiler.dll）改成按镜像旁是否有它判断后单独复跑，两种模式都过。
  其中 conformance `--gate`、CTS / test-clojure 基线（`ClojureTestSuiteMatchesItsBaseline`、`TestClojureMatchesItsBaseline`）、错误黄金文件都在内，基线文件无变化。
- 缺省模式（Clojure 编译器、src/boot）同期全量：Core 125 / 125、Host 291 / 291。

为过 G2 修的（前置修补之外）：

- **惰性序列的头**：`RestFn.applyTo`、`AFn.applyTo`、`AFn.ApplyToHelper`（C# 运行时）停在 tier-0 时，形参与溢出的临时变量被报告为整个方法内活着，`(apply f lazy-seq)` 抓住头；
  手工置 null 不够。加 `AggressiveOptimization`（同 cleat-042 / 117 的先例）。这是两种模式共有的潜在问题，缺省模式下 `lazy_head_core.clj` 靠 Clojure 编译器自身的运行
  预热 RestFn.applyTo 才过；最小复现 `(apply + (big))` 修前两种模式都 OOM，修后都过。
- **访问器内联（cleat-145 / 178）在 C# 编译器里实际没生效**：驱动没有访问器表，后端的直读 / 直存是 174 的占位。移植 `Driver/Accessors.cs`（accessors-of、merge、
  specialize-accessor-clause、register-eval-accessors!，接口文件与 eval 内联都接上）与 `CgGetInstanceFieldDirect` / `CgSetInstanceField`。之后 accinline_semantics 的
  eval 单元 IL 两个编译器逐行相同（开 6053 行、关 5690 行，0 差异）。
- **生成代码取证不再依赖 Lcil**：新增 `Cleat.Core.Compiler.IlText`（按反射读已装载程序集的 IL，每条指令一行，跨用户程序集的成员带程序集名）；
  NumUnboxedArithmetic、AccessorInliningCrossCompileUnits、JoinRawUses、TypedEntries、LoopSpeculation、ArrayAccessIsResolvedStatically 改成断言 IL 文本，两个编译器都过。
- `CLEAT_TIME` 第一行的编译器来源加 `cs`（package / source / cs）；tools.build 的 uber 测试与发布测试按实际带的编译器断言。

### 3. G3 / G5

- G3：`cs-image --ilverify` 6 个程序集全部 Verified（镜像、随附库、tools 两个、nrepl、neorepl）；ILVerify 补引用宿主（tools.build 的 uber 读 `Cleat.Host.Program/ClojureBootPath`）。
- G5：`cs-image --twice` 14 个文件逐字节相同（本任务里重编多次，每次都相同）。

### 4. 耗时（本机，同一构建）

| 项 | Clojure 编译器（src/boot） | C# 编译器（src/boot-cs） |
|---|---|---|
| 编镜像（`--compile-boot`，两次） | 29.1 / 31.5 s | 5.9 / 6.4 s |
| `-e nil`（5 次中位） | 676 ms | 673 ms |
| `-e "(+ 1 2)"`（首次编译求值，中位） | 958 ms | 686 ms |
| `-e "(defn f [x] (* x x)) (f 7)"`（中位） | 1473 ms | 813 ms |
| 编译器按需加载（`CLEAT_TIME`） | package 208 ms | cs 4 ms |

`-e nil` 的分段两边相同（镜像 entry 约 500 ms 占大头，不加载编译器）。

### 5. r7rs 56 项 1 轮

`bench/r7rs/triage149.py -r 1 --shuffle`，逐项调用（每项两臂背靠背、顺序随机；整表一次调用时它按臂成块跑，1 轮下两臂相隔太久），
两臂：缺省（Clojure 编译器 + src/boot）与 `CLEAT_COMPILER=cs` + src/boot-cs。56 项全部跑完、无错误；原始数据 `bench/r7rs/results/triage149-m20-177.jsonl`。

cs/clj 几何平均 **x0.954**。变快超过 10% 的 9 项：slatex x0.638、simplex x0.826、parsing x0.830、array1 x0.836、fibc x0.836、browse x0.843、ctak x0.860、
takl x0.869、cat x0.887；变慢超过 4% 的 5 项：maze x1.065、scheme x1.052、wc x1.049、ack x1.042、tak x1.040（其余在 x0.89 ~ x1.03）。
单轮、本机地板 17 ~ 43%（151 §1），这些都不可判定；函数类 IL 两个编译器逐方法相同（179 / 180），差异的系统性来源可能是 cs 模式不加载 Clojure 编译器包（堆与 JIT 状态不同）
与镜像不同（C# 编的镜像的 IL 未与 Clojure 编的逐方法比对）。**没有可归因到待补优化的差距**：30 §4 的各组在 178 ~ 180 已移植，181 的 56×5 配对才是性能门控。
