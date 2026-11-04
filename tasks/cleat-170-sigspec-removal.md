# cleat-170 运行时 sigspec 体系删除

- 里程碑：M19　依赖：cleat-163　状态：进行中（2026-11-04：实现与门禁全过，111 全表复核跑动中）
- 设计依据：[29 §5.1](../designs/29-usability-and-engineering.md)、[24](../designs/24-runtime-signature-specialization.md) ~ [27](../designs/27-jit-style-runtime-specialization.md)

## 问题

[cleat-163](cleat-163-sigspec-ablation.md) 三组 56 × 5 同轮配对都没有可声明的整体收益，
三组几何平均 x1.003 / x0.999 / x0.997，可声明方向只有两个约 5% 的默认成本格。按 29 §5.1
裁决为 (c)：整体删除运行时观察、签名表、后台重编与运行时反馈设施，把复杂度换回可维护性。

## 工作内容

1. 删除 `RuntimeSigSpec` / `RuntimeSigTable` / `RuntimeSigKey` / `TypeFeedback` 的 C# 实现与测试；
   移除 codegen / driver 的安装、过滤、SeedCompiled、RtSpec 入口与 `CLEAT_RTSPEC_*` 读取点。
2. 逐项清点 `CLEAT_NO_OPT=typed` 相关入口，区分 cleat-104 的编译期类型化入口与 24 ~ 27 的
   运行时生成 / 守卫路径：只删后者；若两者纠缠导致必须动编译期 typed 行为，先列出语义 / 种子差异并裁决。
3. 同步 designs/24 ~ 27 为历史 / 被 170 取代的状态，designs/README 的环境变量与 `CLEAT_NO_OPT`
   记号表删除对应项；README 已知限制留给 169 汇总。
4. 重走 `clojure-seed`：stage2 == stage3，from-seed 产物逐字节一致。
5. 全套门禁：Release 0 warning、Core / 全量 Host、conformance `--gate`、CTS / test_clojure 基线、
   selfhost 不动点；镜像 / 编译器 ilverify 按 162 现行门禁。
6. 默认 vs 删除后构建的 r7rs 56 项 × 5 轮同轮配对（本机，111 在线时复核）；用 `m16sum.py` +
   `R7_FLOOR` 判定。允许中性或可声明改善，不允许可声明回退；几何平均与逐项异常写进本任务书。
7. `slatex` / 短启动各做一次 RSS、线程与 wall 冒烟，确认后台线程与相关常驻面消失。

## 验收

- 产品代码与种子中不再有运行时 sigspec / TypeFeedback 入口；诊断开关表与实现一致。
- typed 的保留 / 删除边界有明确记账，不把编译期类型化入口静默删掉。
- 门禁全绿；种子不动点通过；56 × 5 配对表进任务书，无超地板回退。

## 收口（2026-11-04：实现 + 门禁全过；111 全表复核跑动中）

### 删了什么

| 位置 | 内容 |
|---|---|
| `src/Cleat.Core/Runtime/` | `RuntimeSigSpec.cs`(228) / `RuntimeSigTable.cs`(438) / `RuntimeSigKey.cs`(132) / `TypeFeedback.cs`(146) —— 共 944 行 C# |
| `src/clojure/cleat/compiler/codegen.clj` | −746 行：`opt-sigspec?` / `opt-typefeedback?`、`spec-feedback-candidate` / `spec-param-mask` / `spec-typed-use-call?` / `spec-typed-rt-methods`、`runtime-sig-*` / `runtime-generated-entries` / `runtime-compiled-calls` / `runtime-install-forms` / `runtime-installed-specs`、`cg-profile-args!` / `profile-fields`、`cg-runtime-sig-entry!` / `cg-rtspec-entry!`、`acceptable-runtime-specialization?` / `compile-runtime-specialization`、后台重编与守卫路径 |
| `src/clojure/cleat/compiler/driver.clj` | −7 行：`RuntimeSigSpec/Enabled` 的开关与恢复、`SpecCompiler` / `SpecFilter` 的登记 |
| 测试 | `TypeFeedbackTests.cs`（2 例）、`ClojureCompilerTests` 的 `SignatureSpecialization` / `WholeFunctionRuntimeSpecialization` 两例、语义文件 `rtspec_jit_semantics.clj` / `sigspec_semantics.clj`；`RuntimeModelTests` −152 行 |

### 诊断开关（与实现一致）

- 环境变量 22 → **18**：删 `CLEAT_RTSPEC_STATS` / `CLEAT_RTSPEC_DEBUG` / `CLEAT_RTSPEC_SYNC` /
  `CLEAT_RTSPEC_ALL` / `CLEAT_RTSPEC_THRESHOLD` 五项与 `CLEAT_DUMP_RT_LCIL`。
- `CLEAT_NO_OPT` 记号 26 → **24**：删 `sigspec`（运行时签名特化整体）与 `typefeedback`（eval 候选剖面记录）。
- 产品代码与种子中已无 `rtspec` / `RuntimeSig` / `TypeFeedback` 字样（`grep` 只在 `bench` 的历史结果与
  `tests/.../TestResults/` 的旧 trx 里还有）。

### typed 的边界（工作内容第 2 条的记账）

**删的是 24 ~ 27 的运行时生成 / 守卫路径，保留 cleat-104 的编译期类型化入口**：

- 保留：`codegen.clj` 的 `opt-typed?`（`CLEAT_NO_OPT=typed` 仍生效）、`speculate-entries`、
  `cg-typed-entry!` / `cg-typed-self!`；Core 的 `Runtime/TypedBailout.cs`（编译期类型化入口的守卫异常）。
  `codegen.clj` 里 `forms (if (and opt-infer? (or opt-loopspec? opt-typed?)) …)` 一行只去掉了 `opt-sigspec?` 分支，
  编译期类型化的产出路径逐字未动。
- 删除：`opt-sigspec?` / `opt-typefeedback?` 两个开关，以及它们带出来的编译期侧副作用
  （`spec-param-mask` 之类的候选登记、`RuntimeSigSpec/SpecCompiler` 的登记与安装）。
- 两者**没有纠缠**：`typed` 不依赖任何被删的 Core 类型（`TypedBailout` 是独立文件），种子未因此改变语义。

### 门禁（29 §9）

| 项 | 结果 |
|---|---|
| Release 构建 | 0 warning、0 error |
| Core | **125 / 125**（比 161 少 3 例：随 sigspec 一起删掉的 RuntimeModelTests 用例） |
| Host 全量 | **291 / 291**，32 m 15 s，0 failed；trx 里 `sigspec` / `rtspec` / `typefeedback` 字样 0 处 |
| conformance `--gate` | **OK**：521 classified divergences / 521 failing rows（全登记） |
| CTS | 247 ns，assertions 6868 pass / 29 fail / 6 error → **baseline OK** |
| test-clojure | 54 ns，3877 pass / 25 fail / 4 error → **baseline OK** |
| selfhost 不动点 | **stage2 == stage3**，274 s：`cleat.boot.dll` / `cleat.compiler.dll` 与各自的 `.lcil` 逐字节相同 |
| ilverify | `cleat.boot.dll` 与 `cleat.compiler.dll` 全类全方法 Verified |

### 种子

- 只重生成**编译器**种子：`cleat.compiler.{lcil,ifc,inl}.gz`（lcil 1,313,074 → 1,207,255 字节，运行时特化代码随之消失）。
- **boot 种子逐字节未变**（`cleat.boot.{lcil,ifc,inl}.gz` 与改前基线 `cmp` 相同）——删掉的是运行时设施，镜像不变。
- from-seed 一致性：种子里的 `cleat.compiler.lcil` 与 `selfhost` stage3 产物的唯一差异是**资源路径字符串**
  （selfhost 记的是 `/tmp/cleat-ifc-<hash>/` 临时目录，种子记的是包内相对名 `cleat.compiler.ifc`），
  归一化后逐字节一致；`cleat.boot.*` 三个种子与 stage3 逐字节相同。

### 内存与线程（工作内容第 7 条）

改前基线 = `0fa2756`（163 收口态）在同一台机器上的构建，改后 = 本任务工作树。

| 场景 | 改前 | 改后 | 差 |
|---|---|---|---|
| `slatex` 峰值 RSS | 218,612 KiB | **205,784 KiB** | −12,828 KiB（−5.9%） |
| `slatex` 最大线程数 | 11 | **10** | **−1（后台特化线程消失）** |
| `-e nil` 峰值 RSS | 86,704 KiB | 86,604 KiB | −100 KiB（噪声） |
| `-e nil` 最大线程数 | 8 | 8 | 0 |

### r7rs 56 项 × 5 轮同轮配对

臂序轮换，`R7_BASE` = 改前基线构建，`m16sum.py` + `R7_FLOOR=bench/r7rs/results/floor-Debian-VM01.json` 判定
（全局缺省 5% / 同向 5 轮；`array1` / `takl` / `wc` / `browse` / `slatex` / `peval` / `parsing` 用更高地板）。

**本机（补充口径）**：560 格全干净（0 loaded、0 error），配对比值中位的几何平均 **x0.9832**（整体略快）。
可声明项 8 项改善 + 3 项"回退"：

| 方向 | 项 | 配对比值 | 同向 |
|---|---|---|---|
| 改善 | mbrotZ | x0.880 | 5/5 |
| 改善 | diviter | x0.906 | 5/5 |
| 改善 | graphs | x0.922 | 5/5 |
| 改善 | bv2string / mbrot / quicksort | x0.926 / x0.925 / x0.926 | 5/5 |
| 改善 | destruc / simplex | x0.937 / x0.942 | 5/5 |
| 回退（**111 复核后推翻**） | ntakl / sum / triangl | x1.116 / x1.072 / x1.064 | 5/5 |

**111 复核（权威口径）**：本机那三项"回退"是**本机噪声 + 分层 JIT 抽签**，111 上不成立。
111 上把三项（连同兄弟项 `takl`）各跑 **9 轮**同轮配对（臂序轮换，0 error、0 loaded）：

| 项 | 111 配对比值（中位） | 同向 | 判定 |
|---|---|---|---|
| ntakl | **x0.979** | 8/9 | 改善方向（不可判定，幅度 < 5%） |
| sum | **x0.786** | 9/9 | 改善 21.4% |
| takl | x0.964 | 8/9 | 改善方向（地板 13.6%，不可判定） |
| triangl | x1.046 | 0/9 | 慢 4.6%，**低于 5% 地板** → 不可判定 |

机制定位（本机做的 2×2 隔离，证明这不是语义回退）：

1. 把**改前基线的 Core.dll** 换进改后的树（编译器种子保持改后）→ `ntakl` 回到 17.4 ~ 20.3 s 的**双态**区间，
   与改前基线（15.9 ~ 17.1 s）同档；说明 Core.dll 里删掉那 4 个类型不是原因，
   真正在变的是同一台机器上 **111 所说的"分层 JIT 双态"**（`bench/r7rs/pairbuild.py` 注释：
   慢档 ≈ 快档 × 1.32；本机 `ntakl` 的快档 ~17.4 s、慢档 ~20.3 s，差 ×1.17）。
2. 改前基线 + **改后编译器种子** → `ntakl` 16.6 ~ 20.6 s，与改前基线同档，**编译器种子被排除**。
3. 结论：`ntakl` / `sum` / `triangl` 在本机是抽签项；判定必须按 29 §9 与
   [notes/r7rs-performance.md](../notes/r7rs-performance.md) 的纪律**在 111 上复核**，111 结论为准。

（111 全表 56 × 5 跑动中，结果跑完补进本节。）
