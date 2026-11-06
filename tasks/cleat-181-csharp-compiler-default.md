# cleat-181 默认切换到 C# 编译器

- 里程碑：M20　依赖：cleat-178、cleat-179、cleat-180　状态：完成（2026-11-05；G4 唯一可声明回退 mbrot 经用户裁决接受，见实施记录）
- 设计依据：[30 §1 阶段 D、§2 D8](../designs/30-csharp-compiler.md)

## 工作内容

1. 整体 G4：C# 编译器（全部优化默认开）对 Clojure 编译器，r7rs 56 项 × 5 轮同轮配对，本机与 111 各一组（151 口径）。
2. `CLEAT_COMPILER` 缺省改为 `cs`；`dotnet build` 的 `GenerateBootImage` / 编包目标改用 C# 编译器（不再从种子发射 stage1）。
   Clojure 编译器保留为 `CLEAT_COMPILER=clj` 一个周期，只修缺陷。
3. 启动与构建：`-e nil`、REPL 首提示符、首次 eval、镜像构建耗时与 cleat-159 / 161 的数字对比；评估 `Cleat.Compiler` 单独 R2R。
4. 全套 G2 / G3 / G5。

## 验收

- 整体 G4 无可声明回退（可声明回退须逐项归因并经用户裁决后才能切换）。
- G2 / G3 / G5 全过；启动与构建数字写入本任务书。

## 实施记录（2026-11-05）

### 1. 默认切换

- `CLEAT_COMPILER` 不设或 `cs` 为 C# 编译器（`Startup.UseCsCompiler`）；`clj` 为 Clojure 编译器，保留一个周期、只修缺陷，要镜像旁的 `cleat.compiler.dll`；其他值报错。
- 宿主恢复 `--no-boot`（`StartupOptions.NoBoot`）：不加载任何镜像；cs 模式下没有镜像也登记编译器的惰性入口（C# 编译器不在镜像上）。
- `dotnet build`（`Cleat.Host.csproj`）不再从种子发射 stage1：
  - `GenerateBootImage`：两步、都 `CLEAT_COMPILER=cs`——`--no-boot` 从 src/clojure 编出 `obj/cs-stage1/cleat.boot.dll`，再在 stage1 上编出 `src/boot/cleat.boot.*`。
  - `GenerateCompilerPackage`：Clojure 编译器包 `cleat.compiler.dll` 由 C# 编译器在新镜像上编（编它的不是它自己，不再需要 compiler0 改名工具）。
  - `EmitSeedStage1` 删除；种子文件本身留到 182。
- 从零编镜像时 C# 编译器的两处让步（只在没有起步镜像的那一遍，`FormAnalyzer.DeclareCoreForwardRefs`）：
  core.clj 在 `concat` 定义之前就有语法引用（`not` 的 `:inline`，cleat-117），所以 `clojure.core` 里还没有的 Var 先 intern 成未绑定的（同 declare）；
  执行失败的 inliner 退回普通调用。`src/clojure` 不改。stage1 因此少几处内联，第二遍（在 stage1 上）就与"从任何完整镜像起步"逐字节相同。
- 测试：`ClojureBootstrapTests.BuildFromTheSeedReproducesTheSeed`（种子发射的 stage1 = src/boot）在新流水线下不成立，换成
  `BuildImageIsAFixedPointOfTheCsCompiler`（在 src/boot 上再编一遍，镜像 / .ifc / .inl 逐字节相同）。
- 工具：`bench/r7rs/pairbuild.py` 加 `R7_ON_ENV`（只作用于新构建臂，与 `R7_OFF_ENV` 对称——一臂的变量不能放进进程环境，另一臂跑完 pop 会删掉）。

**种子已经多余**：从切换后的 src/boot（C# 编的镜像 + C# 编的 Clojure 编译器包）出发，`selfhost` 的 stage3 与旧流水线（种子）编出的镜像、
编译器包 SHA256 相同（17b3909a… / fd901811…）——Clojure 编译器的不动点从源码就能复现。C# 编的镜像在 13（.NET 10.0.11）与 111（10.0.0）上 SHA256 也相同。

### 2. 整体 G4（56 项 × 5 轮同轮配对，151 口径，地板表 `floor-Debian-VM01.json`）

| 组 | 臂 | 格子 | 几何平均（配对比值中位） | 可声明 |
|---|---|---|---|---|
| 111（vm-david，4 vCPU，.NET 10.0.0） | on：C# 编译器 + C# 编的镜像；off：Clojure 编译器 + 种子流水线的镜像（切换前的同一构建） | 560，0 error、0 loaded | **x0.988** | 收益 ctak x0.869、mazefun x0.950；**回退 mbrot x1.055（5/5）** |
| 本机（Debian-VM01，8 核，.NET 10.0.11） | on：切换后的缺省；off：`CLEAT_COMPILER=clj` + Clojure 编译器的不动点镜像（src/boot-clj，同 selfhost stage3） | 560，0 error、0 loaded | **x0.963** | 11 项收益（parsing x0.794、simplex x0.846、cat x0.870、ctak x0.883、ray x0.899、fibc x0.908、nucleic x0.914、mazefun x0.924、divrec x0.925、string x0.936、dynamic x0.949），**0 回退** |

原始数据：111 上 `~/workspace/cleat-181/bench/r7rs/results/pair181.jsonl`，本机 `bench/r7rs/results/pair181-local.jsonl`。

**mbrot x1.055（111）的归因**（111，各 7 轮同轮配对）：

| 对照 | 结果 | 说明 |
|---|---|---|
| C# 编译器：C# 编的镜像 vs Clojure 编的镜像 | x1.000（3/7） | 镜像无影响 |
| Clojure 编的镜像上：C# 编译器 vs Clojure 编译器 | x1.031（0/7） | 差异在"哪个编译器的进程" |
| 两个编译器给 r7rs.mbrot 编出的全部 eval 单元 IL（IlText） | 5255 行，0 行不同 | 生成的代码逐字节相同 |
| cs 模式借用 Clojure 分析器（进程里跑起 Clojure 编译器，代码仍由 C# 生成）vs clj | x0.970（6/7） | 回退消失 |
| cs 模式加大 gen0（`DOTNET_GCgen0size=64 MB`）vs clj | x1.094（0/7） | 不是 gen0 预算 |

结论：不是编译器回退，是进程环境效应——clj 模式在跑基准之前先在进程里运行了 Clojure 编译器，改变了 JIT / 动态 PGO 的状态；具体哪一种没有再追。
本机组 mbrot 不可判定。**用户裁决（2026-11-05）：接受，保持默认切换。**

### 3. 启动与构建（本机，wall 5 次中位；`dotnet <dll>`，开发布局）

| 场景 | 159 | 161 | 181（缺省 = C#） | 181 的 `CLEAT_COMPILER=clj` |
|---|---|---|---|---|
| `-e nil` | 978 | 678 | **658** | 666 |
| `-e '(require (quote clojure.pprint))'` | 1019 | 923 | **672** | 905 |
| `-r` 到 `:repl/quit` | 1809 | 1588 | **809** | 1469 |
| `-r` 到首个提示符（`CLEAT_TIME` 的 first prompt） | 1771 | 1575 | **829** | 1496 |
| 首次 eval `-e "(+ 1 2)"` | — | — | **679** | 935 |

编译器按需加载：C# 4 ms（hooks），Clojure 编译器包约 208 ms。`dotnet build` 编镜像 + 编译器包约 33 s（从零两步编镜像约 12 s）；原流水线是种子发射 +
镜像约 30 s + 编译器包约 60 s。159 的 REPL 目标"≤ 0.55 s"还没到（repl-requires 里 clojure.repl.deps 的失败尝试仍在）。

**`Cleat.Compiler` 单独 R2R 的评估**：`tools/build.cs r2r --only Cleat.Compiler.CSharp.dll`（363 KB → 1175 KB）对只 R2R 一个无关小程序集的同布局快照，
apphost 7 次中位：`(+ 1 2)` 689 → 682 ms、`defn` + 调用 799 → 753 ms、首提示符不变；160 的回退敏感项（bv2string / equal / ray / read1 / sum1 / fib）
5 轮跨快照配对：fib x0.711（5/5，收益），其余不可判定，**无可声明回退**。`ShouldR2R` 按名字已经包含它，默认 r2r / publish / pack 的 R2R 集合不改。

### 4. G2 / G3 / G5

- G3：src/boot 的镜像、随附库、Clojure 编译器包、tools 两个包 ilverify 全部 Verified。
- G5：清掉 src/boot 的镜像与编译器包后 `dotnet build` 两次，6 个文件（镜像 dll / ifc / inl / pdb、编译器包 dll / pdb）逐字节相同。
- G2（缺省 = C# 编译器）：Core 125 / 125、Host **291 / 291**（含 conformance `--gate`、CTS / test-clojure 基线、错误黄金文件、selfhost 不动点、新的 C# 不动点测试）；Compiler.Tests 45 / 45；21 个语义文件缺省与 `CLEAT_COMPILER=clj` 输出相同。`CLEAT_COMPILER=clj`（C# 编的镜像 + C# 编的 Clojure 编译器包）：Core 125 / 125、Host **291 / 291**。
