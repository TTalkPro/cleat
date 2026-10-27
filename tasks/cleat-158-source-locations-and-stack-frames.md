# cleat-158 源位置、序列点、PDB 与栈帧

- 里程碑：M19　依赖：无　状态：完成（2026-10-27 收口：实现 + 镜像源名修复 + 位置只报用户源；56 项性能配对移交 cleat-169，见文末）
- 设计依据：[29 §1 第 1 条 / §2](../designs/29-usability-and-engineering.md)

## 问题

运行期异常的栈帧没有行号，顶层报错位置指向最外层形式。本机实测（2026-10-26）：

```clojure
(ns t)
(defn f [x]
  (/ x 0))              ; 第 3 行
(defn g [] (inc (f 1)))
(g)                     ; 第 5 行
```

输出 `Execution error (ArithmeticException) at (t.clj:5)`；`CLEAT_TRACE_ERRORS=1` 下只有 `t$g.invoke()`；
`-e` 下 `pst` 显示 `user$g.invokeStatic (:0)`、`#Module.Init0 (:0)`。JVM 报 `t/f (t.clj:3)`。

代码取证：发射器已经支持 `(source …)` 子句和 `(line l1 c1 l2 c2)` 指令（`src/Cleat.Emit/Model.cs:284`、`:314`），
`_debug` 时会 `MarkSequencePoint`（`Emitter.cs:630`、`:683`），也能写 Portable PDB（`Emitter.cs:786 ~ 804`）；
但 codegen 从不发这两种形式，`driver.clj:680` 的 `SaveData … false` 也从不要 PDB。
`f` 的帧缺失，是 analyzer 的跨 fn 内联（`inline-target` / `inline-call`）造成的。

## 工作内容

1. codegen：每个方法发 `(source 文件)`；在 analyzer 已有 `:pos` 的形式（调用、宿主调用、`throw`、
   `let` 绑定初始化、`if` 条件）处发 `(line …)`，粒度对齐 JVM Clojure。没有 `:line` 的形式沿用外层位置。
2. 驱动：包程序集、镜像、`-M` / 脚本加载的单元生成 PDB（与 dll 同名）；eval 的内存程序集走 `_debug` 路径挂文档。
   `pack` 把 PDB 打进 Cleat.Runtime / Cleat.Cli。
3. 内联：被内联的函数体保留它自己源文件的序列点。量化默认模式与"关跨 fn 内联"在 r7rs 上的差距后，
   再决定是否提供 `-g` 命令行选项（不用环境变量，29 §9）。
4. `clojure.repl/pst`、`*e`、`Throwable->map` 的 `:trace` 读到 `文件:行`；帧名 demunge 后对齐 JVM
   （`t/f`），隐藏 `invokeStatic` / `#Module.Init0` 一类实现细节。
5. 顶层 `Execution error … at (文件:行)` 取异常栈里第一个属于用户源的帧，不再取顶层形式的 `:pos`。

## 验收

- 上面的脚本输出 `t.clj:3`，`pst` 列出 `t/f (t.clj:3)`、`t/g (t.clj:4)`。
- `ErrorGoldenTests` 增加内层行号、宏展开后的行号、`-e` 行号三组黄金文件。
- PDB 不参与种子逐字节比较；`clojure-seed` 不动点仍然成立。
- r7rs 56 项同轮配对（本机 5 轮）没有可声明的回退；镜像 / 编译器包体积变化写进任务书。
- 29 §9 的全套门禁。

## 进度（2026-10-26）

- 实现已落地：IR 节点源位置与 nanopass 元数据传播、方法 `(source …)` 与节点 `(line …)`、
  Portable PDB（镜像 / 编译器包 / 库包 / 发布 / eval 内存程序集）、`-g` 关跨 fn 内联、
  顶层错误位置取最内层用户源帧、`pst` 隐藏单元入口帧。
- 驱动的源文件名放线程局部 `DriverOps.SourceFile`，不用 Clojure 动态绑定；被求值代码自己的
  `pushThreadBindings` 不会被驱动的 `binding` 弹出帧误伤。
- 黄金与包测试更新：包程序集现在同产 PDB，`tools.build` 的 `pack` 也包含 PDB。
- 语义门禁：Release build 通过；Core 123/123。全量 Host 在修复前为 283/288，5 个失败
  分别是 seed 滞后、两处 PDB 包目录期望、两处驱动动态绑定回归；修复后相关套件复跑通过：
  Bootstrap + Package 9/9、Error + ValueBoundary 42/42（修复后的全量 Host 复跑仍在待办）。
- `selfhost` / `clojure-seed` 不动点通过；seed 已重生成，`from-seed == stage3`。
- `-g` 量化：本机 10 个代表项 × 5 轮同轮配对，debug / default 几何平均 x1.406；
  destruc x2.053（5/5 慢）、fft x3.076（5/5 慢）、browse x1.253（5/5 慢）、
  takl x1.326、scheme x1.204、parsing x1.102。`-g` 因此只作 opt-in，不改默认。
- 待完成：56 项 × 5 轮完整同轮配对（外部负载导致先中止在 2 轮，另补代表项 5 轮）、
  修复后全量 Host 复跑、默认相对改动前基线的序列点 / PDB 成本确认、最终体积记录与全套门禁复核。
  → 2026-10-27 收口见下一节：全量 Host 复跑、体积与门禁在此收掉；跨构建的序列点 / PDB 成本配对移交 cleat-169。

## 进度（2026-10-27）：镜像的 `(source …)` 全是占位符

### 缺陷与修复

`unit-source` 取的是**发射时**的源名。镜像与包是攒完整单元再编（`load-built` 只绑 `DriverOps/SourceFile`），
发射时绑定早已还原到 `RT/SourcePathVar` 的根值，于是 HEAD 的种子里 **boot 7,277 个方法、compiler 5,929 个方法挂
`NO_SOURCE_PATH`**（boot 只有 11 个是对的），PDB 的文档名跟着全是占位符：
`CLEAT_TRACE_ERRORS=clojure` 显示 `clojure.core$load.doInvoke (NO_SOURCE_PATH:6417)`，验收第 1 条在镜像里根本不成立
（用户源文件是 load-file / 脚本现编的，走 `DriverOps/SourceFile`，所以 `t.clj:3` 那组黄金一直是绿的——缺陷只在预编产物里）。

- `analyzer.clj`：新增 `real-source`（`NO_SOURCE_PATH` / `NO_SOURCE_FILE` 当"没有源文件"）；`source-of` 在**分析时**
  把文件记进 `:pos`——reader 只盖 `:line` / `:column`，文件取形式自带的 `:file`，否则取当时的 `*source-path*`。
- `driver.clj`：`load-built` 期间把 `*source-path*` 也绑成加载名（`Var/pushThreadBindings` + `finally popThreadBindings`），
  与 `DriverOps/SourceFile` 同步；绑的是加载名（`clojure/core.clj`），不带构建机的绝对路径（cleat-091）。
- `codegen.clj`：`unit-source` 与 `seq-point-code` 都过滤占位符——没有真源文件就不发 `(source …)`，
  发射器对无文档的 `(line …)` 是硬错误。
- 结果：`clojure.core$load.doInvoke (core.clj:6417)`，与 JVM 同形（JVM 的栈也只显示文件名，不带包路径）；
  `(source load)` / `(source /)` 能读出镜像里的源文本；`-i` / `-e` 的行为不变。
- 种子按影响面重生成，`clojure-seed` 不动点通过（stage2 == stage3、from-seed 与 stage3 逐字节相同）。
  boot 的 `(source …)` 7,288（7,277 占位符）→ 6,445 个真名、0 占位符；compiler 5,929 → 5,242。
  `(line …)` boot 12,221 → 12,221、compiler 18,857 → 18,876：**序列点覆盖没变，只改归属**，所以这不是性能改动。
  seed 六个文件合计 2,124,192 → 2,131,940 B（+7,748，+0.37%）。

### 位置仍只报用户源（镜像带上真源名后暴露）

`FirstUserLocation` 原来按"有行号 + 文件名是 .clj/.cljc"挑帧，注释写着"core.clj 也算"——占位符时代这条永远不成立，
修复后 `-e '(load "no_such_lib_xyz")'` 变成 `Execution error (FileNotFoundException) at (core.clj:6417)`。
JVM 1.12 本机对照：同一表达式报 `user/eval1 (REPL:1)`；把 `load` 放进用户库（`libx/g`）才报 `libx/g (libx.clj:2)`。
即 JVM 的位置只取**非库命名空间**的第一个帧，库帧留在 `pst` / 栈里。`Diagnostics.cs` 据此加 `IsLibraryFrame`
（`clojure.*` / `cleat.*` / `Cleat.*`，按 `$` 前的命名空间段判断）。
新增 `ErrorGoldenTests.MirrorFramesReportTheirUnitSourceFile` 同时守住两头：栈里有 `(core.clj:`、
不再有 `NO_SOURCE`、位置仍是 `(REPL:1)`。

### 体积（本机 Release，同机）

| 产物 | 158 之前（92d831c） | 现在（含本节修复） | 同产 PDB |
|---|---:|---:|---:|
| cleat.boot.dll | 2,074,624 | 1,671,680 | 354,816 |
| cleat.compiler.dll | 3,290,624 | 3,492,352 | 401,376 |
| src/boot/seed 合计（.lcil/.ifc/.inl 六个 .gz） | 2,275,090 | 2,131,940 | — |

PDB 不进种子：`src/boot/seed/` 只有 `.lcil` / `.ifc` / `.inl`，`src/boot/*.dll` 与 `*.pdb` 都在 .gitignore 里；
`SaveData` 的逐字节比较与 `BuildFromTheSeedReproducesTheSeed` 都照旧通过。

### 记账：镜像 IL 形状差 3.4×，跨构建配对不作数

92d831c 的种子与 HEAD 的种子（c55de56 重生成，与本节的源名修复无关）不是同一个形状：
`boot.lcil` 原始 18,421,969 → 13,831,240 B（本节修复 +6,475 B），`(stloc` 66,004 → 19,481、`(call` 77,102 → 59,380、
`(label` 23,490 → 9,247；方法 / 类型 / 字段 / `ldstr` 计数完全相同，`boot.inl` 反而 742,861 → 898,820 B（内联表变大）。
隔离实验：基线源码 + HEAD 的种子 stage1 重编仍是精简版（19,477 stloc）——差异来自"正在编它的那个编译器"的行为，
不是源码文本；两棵树各自都是自洽不动点（fresh lcil 的 md5 == 自己的 seed）。机制未查明，怀疑 158 的
`:pos` / 元数据传播进了 cp0 与内联的判定。

因此**跨构建（92d831c vs HEAD）的同轮配对不能回答"序列点与 PDB 的默认成本"**：基线与实验臂先差了 3.4× 的 IL 形状，
任何差值都归因不清。56 项 × 5 轮改为在 HEAD 内部做 A/B（或直接进三臂复核），随 cleat-169 的 M19 收口跑；
本节已跑到的 172 / 560 格（0 个污染轮）留在被忽略的 `bench/r7rs/results/pairbuild.jsonl`，
跨构建 runner `bench/r7rs/pairbuild.py` 入库备用。同构建的 `-g` 对照（10 项 × 5 轮，几何平均 x1.406）不受影响，
`-g` 只作 opt-in 的结论不变，也说明默认模式的内联仍然在起作用（形状差不是内联被关掉）。

### 门禁

- Release 构建 0 error（新增的 IDE0040 只在测试项目的既有全量重建里出现，与本节无关）；Core 123/123；
  全量 Host **288/288**（36m24s）——这一遍覆盖了 Bootstrap 不动点与 stage3 冒烟、Package、Error 黄金、ValueBoundary、
  Host 进程、nrepl 与 Conformance（语料 `--gate`、CTS、test_clojure），是上一节欠的"修复后全量 Host 复跑"。
- `IsLibraryFrame` 与 `MirrorFramesReportTheirUnitSourceFile` 落地后复跑：Error + HostProcess + ValueBoundary **199/199**、
  `BuildFromTheSeedReproducesTheSeed`、Conformance **3/3**（3m40s）。C# 运行时改动不触镜像 IL，种子没有再重生成。
  29 §9 的整套门禁按 M19 的口径在 cleat-169 收口时再整体跑一遍（本任务的开发已收口，跑分不占用它）。
