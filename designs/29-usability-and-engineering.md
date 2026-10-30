# 29 可用性与工程收口：从热路径微优化转向用户可感知的缺口

> 状态：**提案**（2026-10-26，M19）。来源：M17 / M18 的收口结论（[28 §7](28-hot-path-optimizations.md)、
> [notes M18](../notes/r7rs-performance.md)）与 2026-10-26 在本机（192.168.3.13）上对 Release 构建的实测。
> 任务 cleat-158 ~ 169。

## §0 判断：热路径微优化已到噪声地板

M17 / M18 的七项里，能落地并默认启用的只有 cleat-156。而 156 修的是 sigspec 自己引起的回退（152 归因到 cleat-133）：

| 任务 | 结论 |
|---|---|
| 145 访问器内联 | 中性（x1.002），browse 5/5 慢 5.8%，**默认关**的 opt-in |
| 153 / 157 调用点类型反馈 | 单态上限 1.3 ~ 2.5 ns/次，低于计数成本 4.6 ~ 5.7 ns/次，停手 |
| 154 变长已知元数直发 | 不达标，回退 |
| 155 闭包提升 | 语义否决（fn 身份），回退 |

r7rs 56 项对 JVM 的几何平均停在 **x1.59**（150 / 151 两次复核一致）。剩下的差距主要是结构性的：
C2 的内联与逃逸分析、tier-0 JIT、泛型分派。这些已经不是"一个任务一个开关"能拿到的收益。
按 151 的噪声地板（检出 5% 需要 5 ~ 175 对配对），继续在这一层投入，单位工作量的产出会越来越低。

**M19 暂停性能主线**，转去处理下面实测到的、用户可以直接感受到的缺口。性能纪律不放松：
M19 的任何改动都不能让 r7rs 56 项出现可声明的回退（§9）。

## §1 实测缺口（2026-10-26，本机 Release 构建）

1. **栈帧没有行号，报错位置不对**。脚本
   ```clojure
   (ns t)
   (defn f [x]
     (/ x 0))              ; 第 3 行
   (defn g [] (inc (f 1)))
   (g)                     ; 第 5 行
   ```
   报 `Execution error (ArithmeticException) at (t.clj:5)`，`CLEAT_TRACE_ERRORS=1` 下栈里只有 `t$g.invoke()`；
   `-e` 下 `pst` 显示 `user$g.invokeStatic (:0)`。JVM 报 `t/f (t.clj:3)`。原因（代码取证）：
   - 发射器支持 `(source …)` 子句和 `(line l1 c1 l2 c2)` 指令，`_debug` 时会 `MarkSequencePoint`（`src/Cleat.Emit/Emitter.cs:630`、`:683`）；
     但 **codegen 从不发 `(source …)` / `(line …)`**，`driver.clj:680` 调 `SaveData … false`，PDB 永远不生成。
   - `f` 的帧缺失，是因为小函数被跨 fn 内联了（`analyzer.clj` 的 `inline-target` / `inline-call`）。
   - 报错位置是 analyzer 的 `:pos`，取的是最外层顶层形式；运行期异常没有内层位置可用。
2. **启动慢**：`cleat -e nil` 0.9 ~ 1.3 s。`CLEAT_TIME=1` 显示 Clojure 镜像 671 ms、编译器包 193 ms。
   `cleat.boot.dll`（2.0 MB）和 `cleat.compiler.dll`（3.3 MB）由 PersistedAssemblyBuilder 产出，是纯 IL，
   每次启动都要 JIT；仓库里没有任何 ReadyToRun / crossgen 配置。`-e nil` 也会加载编译器包。
3. **生态缺口**：`clojure.spec.alpha`、`clojure.core.async` 都 require 不到。偏离表 521 行中 408 行是 `:host` 类，
   以 Java 成员名 / Java 类为主。
4. **没有 CI**：没有 `.github/workflows`。Host 套件约 20 分钟（含自举不动点、ILVerify），完全靠手工跑；
   种子逐字节一致这条纪律，最容易在"忘了重生成种子"时被破坏。
5. **开关与环境变量**：21 个 `CLEAT_*` 环境变量，`CLEAT_NO_OPT` 有 21 个记号
   （accinline arrayspec boxcache boxdouble charbox collapse crossnstype host imagetier infer inline joinraw known
   loopinl loopspec loopweight num scalar sigspec typed typefeedback）。`opt-off?` 用子串匹配
   （`codegen.clj` 的 `str/includes?`、`analyzer.clj` 的 `.Contains`），所以 **`CLEAT_NO_OPT=accinline` 也会关掉 `inline`**——
   这是一个缺陷，单开关归因时可能已经混进了别的开关。
6. **sigspec 体系的复杂度**：Runtime 下 `RuntimeSigSpec` / `RuntimeSigTable` / `RuntimeSigKey` / `TypeFeedback`
   约 1000 行，编译器里 43 处引用。它的全局净收益只在 150 的开关贡献分解里出现过，没有单独、同轮配对的整体消融数据。
7. **编译器结构**：analyzer 和 codegen 之间只有 loops / front / closures 三个 pass（合计约 520 行）。
   codegen.clj 有 4140 行、369 个顶层定义，`cg*` 一个函数 330 行；类型推导（`infer-walk`、`infer-var-types-into!`）、
   运行时签名入口（`cg-runtime-sig-entry!`）、类生成（`genclass-class!`、`clj-fn-class!`）都在其中，和 nanopass 的分 pass 思路不一致。
8. **文档滞后**：README 还写着"M0 ~ M9 已完成"、测试数 117 / 265；没有面向用户的 CHANGELOG 和已知限制清单。

## §2 源位置与栈帧（cleat-158）

目标：运行期异常的栈帧带 `文件:行`，顶层报错位置指向**最内层抛出点所在的形式**，与 JVM 的报告形态对齐。

1. codegen 给每个方法发 `(source 文件)`。在 analyzer 已有的 `:pos` 处（调用、宿主调用、`throw`、`let` 绑定的初始化）
   发 `(line …)`，粒度先对齐 JVM Clojure（每个有 `:line` 的形式一个序列点），不追求列精度。
2. 包 / 镜像 / `-M` 路径构建时生成 Portable PDB（与 dll 同名，随包打进 NuGet）；eval 的内存程序集走
   `_debug` 路径挂内存文档。PDB 不参与种子逐字节比较（§9）。
3. 内联：跨 fn 内联保留"内联来源"的序列点（行号指向被内联函数的源）。是否保留被内联函数的独立帧，
   由 158 量化后裁决：先量开 `-g`（调试模式，关跨 fn 内联）和默认模式下 r7rs 的差距，不预设新开关。
4. `clojure.repl/pst`、`*e`、`Throwable->map` 的 `:trace` 读到行号；demunge 后的帧名对齐 JVM
   （`t/f`、`t/g`，去掉 `invokeStatic` / `#Module.Init0` 这类实现细节）。
5. 错误黄金文件（`tests/errors/`、`ErrorGoldenTests`）补"内层行号"用例，固定上面的脚本输出 `t.clj:3`。

风险：序列点会限制 JIT 的部分优化吗？Release 下序列点只是 PDB 映射，不插 nop，不影响 JIT。
158 仍要在 r7rs 上做同轮配对确认，按 §9 判定。

## §3 启动（cleat-159 ~ 161）

### 3.1 先剖析（cleat-159）

671 ms 的镜像时间先分解，再决定后两项的范围：
- `dotnet-trace`（已安装）采集 `-e nil`，按 JIT 时间、类型加载、命名空间初始化（Var root、常量池 / fasl 反序列化、
  `boot-order.edn` 各条目）分桶，`CLEAT_TIME` 扩展为分段计时（只在设置时输出，与现状一致）。
- 对照组：`DOTNET_TieredCompilation` 各档、`DOTNET_TC_QuickJitForLoops`、`DOTNET_ReadyToRun=0`，确认 JIT 占比。
- 产出：notes 新增"启动画像"一节，给出 161 的最终目标值。暂定目标：`-e nil` ≤ 0.4 s，
  `-e '(require (quote clojure.pprint))'` ≤ 0.6 s（本机冷 / 热各 5 次取中位）。
- **159 的结果**（[notes 启动画像](../notes/startup-profile.md)）：`-e nil` 978 ms 里镜像入口 548（纯 IL 的分层 JIT
  份额 ≈ 283，tier0-only 反证 +480）、编译器包 212（require 驱动整格可在 161 省掉）、runtime 104（反射 / 类型系统）；
  REPL 首提示符 1771 ms，其中 **671 ms 是 require `clojure.repl.deps` 的失败尝试**（源码存在→真走一遍编译器再抛）。
  `quickjit` 无感，160 不依赖它。目标值据此修订（§3.2 / §3.3 的验收行）：160 之后 `-e nil` ≤ 0.65 s、
  160 + 161 之后 ≤ 0.45 s、REPL 首提示符 ≤ 0.55 s。

### 3.2 ReadyToRun（cleat-160）

- 用 crossgen2（`Microsoft.NETCore.App.Crossgen2` 包，本机 NuGet 缓存里暂时没有，需要恢复）对
  `Cleat.Core` / `Cleat.Emit` / `Cleat.Host` 和 `cleat.boot.dll`、`cleat.compiler.dll`、`Cleat.Clojure.Libs.dll` 做 R2R。
- **R2R 产物与种子比较分开**：`src/boot/*.dll` 保持纯 IL，自举不动点与种子逐字节核对不变；R2R 只作用在
  `bin/` 输出、`publish` 和 `pack`（Cleat.Cli / Cleat.Runtime）上。R2R 镜像依赖 Cleat.Core 的版本泡，
  Core 重编后镜像必须一起重新 R2R，构建链按此排序。
- eval 时生成的内存程序集仍然走 JIT，这一点不变。
- 验收：159 的画像里 JIT 份额下降；`HostProcessTests`、`NuGetColdLoadTests` 在 R2R 包上通过；
  长跑基准（r7rs 56 项）不能因为 R2R 代码挡住 tier-1 重编而回退（R2R 代码仍会被分层重编，要实测确认）。
  159 给的量化验收：`-e nil` ≤ 0.65 s（现 978 ms）、entry 段 ≤ 350 ms（现 548）、
  tier0-only − default 的差从 +480 ms 缩到 < 150 ms。
- **160 当前结果**（2026-10-30，实现未收口）：`tools/build.cs r2r` / `publish` / `pack` 已接 crossgen2，
  `--no-r2r` 保留对照臂。默认产物只预编译编译器包与随附库（4 个程序集 4,066 KB → 8,288 KB；
  compiler 3,410 KB → 7,079 KB）；`src/boot` 原件不动。注意量化口径必须执行 apphost——
  `dotnet cleat.dll` 不吃这批 app-local R2R，画像脚本用 `CLEAT_HOST` 固定这条路径。
  2026-10-29 的 9 程序集 / entry 330 ms 读数来自更激进的探索臂，**不是默认产物验收**；
  composite 只能出单个 native bundle，需要 single-file 部署，且当前 `Assembly.Location` /
  `DependencyContext.Load` 假设在镜像前失败，因此不采用，留给 single-file 兼容任务。
- **同构建 R2R on/off 配对**（同一 compiler-only 快照，`DOTNET_ReadyToRun=0` 关 off 臂；
  56 项 × 5 轮、臂序轮换、本机地板 5% / 同向 5）：R2R 带来 8 项可声明收益
  （equal x0.791、fib x0.836、conform x0.926 等），但 **bv2string x1.062、read1 x1.056、sum1 x1.102
  三项 5/5 稳定回退**；判定项几何平均 x0.935 不满足"无回退"验收。诊断矩阵显示 compiler-only 与
  Core-only 曾各自通过短敏感矩阵，但 2026-10-30 的 fresh 归因显示 libs-only 仍有 read1 / sum1 回退，
  compiler-only 仍有 ray / sum1 回退；`--Os` 与两臂同设 `DOTNET_TC_AggressiveTiering=1` 也不消除。
  因此不能归因成某一个程序集组合：R2R 数据会系统性挪动 tier-1 热循环的安装时机 / 落址，
  `--compile-no-methods`（无方法体、无启动收益）与框架训练 `StandardOptimizationData.mibc --partial`
  （对 cleat 程序集零命中）都不能解决。可行方向是启动画像自己的 MIBC 做 partial R2R，或明确放弃该验收并拆任务。
  跨 158 基线的旧配对另有 8 项回退，但混入 158→161 代码布局差异，只作诊断、不作 160 判定。
  当前默认产物 apphost 5 次中位：`-e nil` 668 ms（entry 496、compiler 0）、pprint 937 ms、
  REPL 首提示符 1588 ms——nil 距 650 目标仅差 18 ms，但 pprint / 首提示符远未达标。
- **MIBC partial 候选（2026-10-30 傍晚，未收口）**：`dotnet-pgo` 可从官方 `dotnet-eng` feed 安装
  11.0.0-preview 版本；在 `DOTNET_ReadyToRun=0` 的快照上采集 nil / pprint / REPL prompt trace，合并为
  11,329 方法根 / 219 个带流量方法的 MIBC。`r2r --mibc startup.mibc --partial` 后 9 个 cleat 程序集
  6,520 → 8,520 KB（+2.0 MB），同窗口启动 5 次中位 461 / 665 / 481 ms，比旧默认 R2R 的 762 / 1064 /
  780 ms 明显下降，达到 160 继承的三项量化目标。敏感项 × 5 on/off（`R7_OFF_ENV=DOTNET_ReadyToRun=0`）
  按地板表全部不可声明：bv2string x1.013、ray x0.947、read1 x1.100（4/5）、sum1 x1.099（4/5）。
  没有 5/5 稳定回退，但 read1 / sum1 仍偏慢；56 × 5 全表未完成，因此不把 partial 设为默认。

### 3.3 编译器延迟加载（cleat-161）

- `Startup` 现在会无条件 require 编译器驱动（`Startup.cs:57 ~ 94`）。改成第一次需要编译时（eval 非平凡形式、
  `load` 源文件、`compile`）再加载；`CLEAT_NO_SIMPLE_EVAL` 路径语义不变。
- 只跑 AOT 代码的路径（`-m` 指向包程序集里的 ns、`-X` / `-T` 调已编译函数）不加载编译器。
- 验收：159 定下的目标值；`-r` 首个提示符出现前不得因为延迟加载而变慢（REPL 可以在后台线程预热编译器）。
  159 给的量化验收：`-e nil` ≤ 0.45 s（160 之后）、`CLEAT_TIME` 的 compiler 段为 0；REPL 首提示符 ≤ 0.55 s，
  其中逐库画像要求 REPL 不为 require 失败的库付编译器成本（`clojure.repl.deps` 现状 671 ms）。
- **161 的结果**（2026-10-28，[notes 启动画像 §7](../notes/startup-profile.md)）：惰性入口挂在 `RuntimeHooks`
  四个编译类钩子的 getter 上（`Startup.EnsureCompiler`，幂等 + 同线程重入直接返回），Clojure 侧的
  `clojure.core/eval` / `macroexpand-1` 本来就读这些属性，所以两条路共用一个入口，**`src/clojure` 与种子未动**。
  `Startup.EvalForm` + `SimpleForms` 让平凡形式（常量 / `()` / `quote` / 递归不含符号且不带元数据的集合字面量）
  在 C# 侧直接给值，边界是驱动侧 `simple?` 的真子集，`CLEAT_NO_SIMPLE_EVAL` 可整体关掉。
  `-e nil` 本机 978 → 678 ms、111 797 → 586 ms（`compiler` 段为 0）；`-m` / `-X` / `-T` 的 AOT 路径全程不加载编译器；
  REPL 首提示符没有变慢（本机 1771 → 1575、111 1348 → 1347），后台预热没做（非必需，任务书允许）。
  **首提示符 ≤ 0.55 s 未达成**：那一格的大头仍是 repl-requires 里 `clojure.repl.deps` 的失败 require
  （本机 752 / 111 600 ms），"REPL 不为 require 失败的库付编译器成本"这条不在 161 的改动范围，继续挂着。
  副作用记账：纯 AOT 进程不再有运行时签名特化（`SpecCompiler` 由驱动登记），份额留给 §5.1 的 163 消融。

## §4 CI（cleat-162）

GitHub Actions（`origin` 是 `github.com:TTalkPro/cleat`）分两层：

| 层 | 触发 | 内容 | 目标时长 |
|---|---|---|---|
| PR | push / PR | `dotnet build -c Release`；Core 全量；Host 按 `Category("Host")` + `Packages` | ≤ 15 min |
| nightly | 定时 + 手动 | `Bootstrap`（自举不动点、种子逐字节）、`Conformance --gate`、CTS 与 test_clojure 基线比较、ilverify | 不限 |

- 子模块 `tests/clojure-test-suite` 用 `submodules: recursive` 检出。
- **种子守卫**放进 PR 层：只要改了 `src/clojure/**`，就必须同时改 `src/boot/seed/*.gz`，否则失败
  （便宜的检查，不用跑自举就能挡住最常见的疏漏）。
- 性能不进 CI（共享 runner 噪声远超 151 的地板）；性能仍按 §9 在本机 / 111 上做。
- 失败产物（conformance 报告、CTS 差异）上传为 artifact。

## §5 复杂度收口（cleat-163、164）

### 5.1 sigspec 整体消融（cleat-163）

- 在同一构建上做 `CLEAT_NO_OPT=sigspec` 对默认的 56 项同轮配对（本机 5 轮，111 在线时用 111 复核），
  再加 `typefeedback`、`typed` 两个相关记号的组合，得出 M14 / M15 体系的**整体**贡献。
- 同时统计代码面：sigspec 相关的 C# / Clojure 行数、种子里的体积、`CLEAT_RTSPEC_STATS` 下各基准的运行时编译次数，
  以及后台编译线程对启动和内存的影响（与 159 共用画像）。
- 裁决只给三档，不中途调参：
  (a) 整体贡献可声明且 ≥ 5% → 保留，补 designs/24 ~ 27 的现状注记；
  (b) 可声明但 < 5% → 保留编译期调用路径推导（cleat-140），删运行时观察 / 重编一半；
  (c) 不可声明 → 整体删除，另开任务执行，种子重走。
  5% 这个门槛是本文提议的，163 开工前可以调整，但必须在跑分前定下来。

### 5.2 开关与环境变量（cleat-164）

- **修子串匹配缺陷**：`opt-off?`（codegen.clj）与 analyzer.clj 里的 `.Contains` 改成按逗号 / 空白分词后精确匹配，
  `CLEAT_NO_OPT=1` 语义不变。修完后核对 notes 里用到 `accinline` 记号的归因数据，确认是否需要重算。
- **accinline 去留**：28 §7.2 规定"既有开关是测量仪器，不物理删除"，但 accinline 不是默认开优化的归因仪器，
  而是一条**默认关、实测中性**的代码路径（eval / 包两条，含 `InterfaceFile.FindInline`）。164 提议删除
  （保留在 git 历史和 145 任务书里），由用户裁决；不删时至少在 README 已知限制里写清楚它是实验开关。
- 生成一张开关清单（`designs/README` 附表或 `docs`）：每个 `CLEAT_*` 环境变量 / `CLEAT_NO_OPT` 记号的用途、
  默认值、是否影响语义、引入任务。只整理文档，不改名，避免破坏历史 notes 的可复现命令。

## §6 编译器结构（cleat-165）

纯重构，**种子逐字节不变**是硬验收（stage2 == stage3 且与改前 stage3 相同）。如果做不到逐字节不变
（例如函数定义顺序影响常量池编号），退一步要求镜像 / 编译器包的 Lcil 文本只在可解释的符号名上有差异，并在任务书里列出每一处。

- 从 codegen.clj 拆出：
  - `cleat.compiler.infer`：类型推导（`infer-walk`、`infer-var-types-into!` 等），作为 codegen 之前的独立 pass
    （输出带类型注解的 L3，而不是在 codegen 中途回填）；
  - `cleat.compiler.rtspec`：运行时签名入口（`cg-runtime-sig-entry!`、`cg-rtspec-entry!` 等）；如果 163 裁决为 (b) / (c)，
    这一步合并进那边执行；
  - `cleat.compiler.classes`：`genclass-class!`、`clj-fn-class!`、deftype / reify / proxy 类生成；
  - `cg*` 按节点种类拆成多方法或分派表，单个函数不超过约 80 行（指导值，不是硬门槛）。
- 排在 158（会改 codegen 发序列点）和 163 之后，避免三方冲突。
- `boot-order.edn` 与 `all.clj` 同步更新。

## §7 生态（cleat-166 ~ 168）

- **cleat-166 clojure.spec.alpha**：以上游 `clojure/spec.alpha` + `core.specs.alpha` 为蓝本
  （参照 ClojureCLR 的 clr.spec.alpha 处理宿主差异），作为 `packages/spec.alpha` 的 cleat 项目，打 NuGet 包；
  上游测试按 nREPL 的做法全部移植。core 的宏 spec（`defn` / `let` / `ns` 的 :args 检查）是否默认启用由 166 裁决，
  因为它会影响宏展开的时间和错误消息黄金文件。
- **cleat-167 clojure.core.async**：以 ClojureCLR 的 clr.core.async 为起点；`go` 宏的 IOC 状态机依赖
  tools.analyzer，要么移植 analyzer 的最小子集，要么用 cleat 自己的 analyzer 实现 IOC 变换。167 先做一周 spike，
  在这两条路里选一条再落；线程池与定时器用 .NET 原生设施，可以复用 `AsyncSupport.cs`。
- **cleat-168 jvm-compat 评估**：不改"不模拟 Java"的总决策。评估一个**可选**库 `cleat.jvm-compat`：常用 Java 成员名别名
  （`.length` / `.getMessage` / `.toString` …）和少量 `java.util` / `java.io` 类型映射，只在移植 cljc 库时显式启用。
  产出：对 408 行 `:host` 偏离按"垫片可覆盖 / 需要改库 / 不可能"三类分组的统计，加上实现成本估计；是否实施另立任务。

建议的移植验证顺序（166 / 167 完成后再挑）：tools.reader、data.json、test.check、malli——用真实库的移植结果驱动缺陷修复。

## §8 收口（cleat-169）

- README 刷新（现状、测试规模、M10 ~ M19 摘要）；新增 `CHANGELOG.md`（从 1.0.0 起按里程碑汇总）与"已知限制"一节
  （行号 / 启动 / 缺失库的现状，由 158 ~ 168 的结果填写）。
- M19 全量门禁（§9）与 r7rs 56 项三臂复核，确认 M19 整体没有性能回退。
- TASK.md 与本文状态同步。

## §9 纪律

- **种子**：改 `src/clojure` 必须重走 `clojure-seed`（stage2 == stage3、发射回来逐字节一致）。PDB 和 R2R 产物
  不进种子比较。
- **门禁**：Release 0 warning；Core / Host 全过；conformance `--gate`；CTS / test_clojure 与基线一致
  （有意改变时用 `--write` 并在任务书里列出差异）。
- **性能**：每个改到 `src/clojure` 或 `Cleat.Core` 的任务，都要做 r7rs 56 项同轮配对（本机 5 轮，口径同 151 / 156），
  不允许出现可声明的回退；158 的序列点、160 的 R2R、161 的延迟加载各自单独确认。
  159 在 111 的复核加两条口径（[notes §6.3](../notes/startup-profile.md)）：**启动期的插桩必须调用点自判开关**，
  未启用时连采样方法都不能被调用（"调用了但立即早退"会把方法送进分层 JIT 队列，4 vCPU 上短基准整段从快档挪到慢档，
  看起来像 25 % 的回退）；**跨构建配对每轮轮换臂序**（`bench/r7rs/pairbuild.py` 记 `order`），
  固定臂序会把这种双态抽样放大成单边结论。
- **开关**：沿用 M18 的口径，本批不新增优化开关。158 第 3 条如果需要调试模式，走命令行选项（`-g`），
  不用环境变量。

## §10 任务拆分与依赖

| 任务 | 内容 | 依赖 |
|---|---|---|
| cleat-158 | 源位置、序列点、PDB 与栈帧（§2） | 无 |
| cleat-159 | 启动剖析与目标值（§3.1） | 无 |
| cleat-160 | ReadyToRun：Core / 镜像 / 编译器包 / 随附库（§3.2） | cleat-159 |
| cleat-161 | 编译器延迟加载（§3.3） | cleat-159 |
| cleat-162 | GitHub Actions：PR 与 nightly 两层、种子守卫（§4） | 无 |
| cleat-163 | sigspec 整体消融与三档裁决（§5.1） | 无（建议排在 164 修完子串缺陷之后跑分） |
| cleat-164 | `CLEAT_NO_OPT` 精确匹配、accinline 去留、开关清单（§5.2） | 无 |
| cleat-165 | codegen 拆分：infer / rtspec / classes（§6） | cleat-158、cleat-163 |
| cleat-166 | clojure.spec.alpha 包（§7） | 无 |
| cleat-167 | clojure.core.async 包（spike 后落地，§7） | 无 |
| cleat-168 | jvm-compat 评估（§7） | 无 |
| cleat-169 | M19 收口：文档、CHANGELOG、全量门禁与 r7rs 复核（§8） | cleat-158 ~ 168 |

关键路径：164 → 163 → 165；158 → 165；159 → 160 / 161 并列；162、166、167、168 可以随时并行；最后 169。
优先级：158（用户最直接感受到）、159 ~ 161（启动）、162（CI 能保护后面所有任务）排在前面。
