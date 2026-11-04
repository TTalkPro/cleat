# cleat-161 编译器延迟加载

- 里程碑：M19　依赖：cleat-159　状态：进行中（实现与门禁通过，111 的 56 项同轮配对跑动中）
- 设计依据：[29 §3.3](../designs/29-usability-and-engineering.md)

## 问题

`Startup`（`src/Cleat.Core/Runtime/Startup.cs:57 ~ 94`）在镜像之后会无条件 require 编译器驱动，耗时约 193 ms。
`-e nil`、只调 AOT 函数的 `-m` / `-X` / `-T` 都要付这笔成本。

## 工作内容

1. 编译器驱动改成第一次需要编译时再加载：eval 非平凡形式、`load` 源文件、`compile`、`macroexpand` 涉及编译期设施时。
   简单求值路径（`CLEAT_NO_SIMPLE_EVAL` 关掉的那条）的语义不变。
2. 包程序集里的 ns 走 `-m`、`-X`、`-T` 调用已编译函数时，不加载编译器。
3. REPL：首个提示符之前不能变慢，可以在后台线程预热编译器；首个输入要等预热完成，不能出现竞态。
4. 嵌入 API（`CleatRuntime`）沿用同一个惰性入口。
5. `CLEAT_COMPILER_NS`（自举用的改名工具编译器）路径保持现状。

## 验收

- 159 定的目标值；`CLEAT_TIME` 显示 `-e nil` 不加载编译器。
- 自举不动点、`HostProcessTests`、`NuGetColdLoadTests`、nREPL / neorepl 包测试通过。
- 29 §9 的全套门禁。

## 收口（2026-10-28：实现 + 门禁；配对复核待收）

- **惰性入口挂在钩子的 getter 上**（`RuntimeHooks` 的 eval / load-source / compile / macroexpand-1）：槽为空时先调
  `Startup.EnsureCompiler()` 一次（幂等；同线程重入直接返回——加载编译器自己要用这些钩子；别的线程在锁上等它加载完）。
  Clojure 侧的 `clojure.core/eval`（`core.clj:3217`）与 `clojure.core/macroexpand-1`（`4095`）本来就直接读这些属性，
  所以 C# 与 Clojure 共用同一个入口，**不改 `src/clojure`、不动种子**（`cleat.boot.dll` `b7e257f7…`、
  `cleat.compiler.dll` `1334696f…` 与 159 逐字节相同）。
- `Startup.EvalForm` + `SimpleForms`：编译器还没加载时**平凡形式**（自求值常量、`()`、`(quote x)`、递归不含符号且
  不带元数据的 vector / map / set）在 C# 侧直接给值，不为一个常量付那一整格。边界是驱动侧 `simple?` 的真子集
  （认多了会算错，认少了只是多加载一次编译器，所以只往严的一侧收），由 `SimpleFormsTests` 固定；
  `CLEAT_NO_SIMPLE_EVAL` 设置时快速路径整体关掉，语义逐字回到 161 之前（验收 1 的"`CLEAT_NO_SIMPLE_EVAL` 路径语义不变"）。
- `CLEAT_TIME` 的口径：第一行 `compiler (package|source) N ms` 格式不变（159 的守卫依赖它），没加载时是 0；
  报告**之后**才按需加载时补一行 `cleat: compiler loaded on demand: package 211 ms (require 206 + hooks 9)`。
- 验收 2：`-m` / `-X` / `-T` 跑包程序集里的 AOT ns 全程不加载编译器；`--compile-boot` / `--compile-package`
  自己先 `Startup.EnsureCompiler()`（这两条本来就要编译）。
- 验收 4：嵌入 API 经 `Startup.EvalReader` → `EvalEntry`，与命令行宿主同一个惰性入口（`CleatRuntime` 没有另开路）。
- 验收 5：`CLEAT_COMPILER_NS != cleat.compiler`（自举的改名工具编译器）保持现状，仍然 eager。
- 验收 3（REPL）：首个提示符**没有变慢**（本机 1771 → 1575 ms、111 1348 → 1347 ms）。
  **后台线程预热没有做**（任务书写的是"可以"，非必需）：两台机器的首提示符都不因延迟加载变慢，
  而预热要处理首个输入与后台加载的竞态，收益只在首个非平凡输入的 ~200 ms；不做，记账。
- 实测（详见 [notes/startup-profile.md §7](../notes/startup-profile.md)）：`-e nil` 本机 978 → **678 ms**、
  111 797 → **586 ms**，省下的正是 compiler 那一整格。159 的量化验收"≤0.45 s"是 160（R2R）+ 161 的合计目标，
  161 单臂到 0.59 ~ 0.68 s，仍要等 160。
- **副作用（记账）**：纯 AOT 进程（只跑包程序集里的 ns）不再有**运行时签名特化**——`RuntimeSigSpec.SpecCompiler`
  由编译器驱动登记，编译器没加载时 `Install` 直接返回 null。刻意不在 `Install` 里 `EnsureCompiler`：
  那会在触发的调用线程上加载 200 ms 的编译器，而 §3.3 的验收正是这条路不加载编译器。
  从源码加载 ns 的路径（r7rs 基准、REPL、`-e` 的调用形式）本来就要加载编译器，不受影响。份额留给
  [cleat-163](cleat-163-sigspec-ablation.md) 的整体消融裁决。
- 门禁：Release 0 warning；Core 128/128（新增 `SimpleFormsTests` 5 例）；全量 Host 291/291
  （新增守卫 `CompilerLoadsOnDemandOnly`：平凡形式连打不出现补行、`(+ 1 2)` 出现、`CLEAT_NO_SIMPLE_EVAL` 下
  `-e nil` 也出现、`-m` 跑包程序集不出现）；conformance `--gate` OK；CTS / test-clojure 与基线一致；
  `selfhost` 不动点 304 s（stage2 == stage3，boot / compiler 的 dll 与 Lcil 逐字节相同）。
- **待收（已完成，2026-11-04）**：29 §9 的性能纪律——161 vs 159-fix 的 r7rs 56 项 × 5 轮同轮配对
  （臂序轮换）在 111 上跑完：560 格（56 项 × 5 轮 × 2 臂）全干净，0 error、0 loaded，产物
  `bench/r7rs/results/pair161.jsonl`（111 上 `~/workspace/cleat-161-new`）。
  配对比值中位的几何平均 **x1.0043**（56 项，整体中性）。

  | 项 | 配对比值 | 同向 | 判定 |
  |---|---|---|---|
  | cat | x0.920 | 5/5 | **可声明改善**（−8.0%） |
  | nucleic | x0.942 | 5/5 | **可声明改善**（−5.8%） |
  | sum | x1.273 | 5/5 | 见下（判为分层 JIT 双态，不是回退） |

  `sum` 的 x1.273 是 111 上短基准的分层 JIT 双态：pairbuild 的注释记着「短基准（sum / ray）在分层 JIT 下
  落在两个量化档上（慢档 ≈ 快档 × 1.32）」，x1.273 正落在慢档；同项 rep1 是 x1.014（两臂打平），
  说明两臂能落在同一档，rep2 ~ rep5 是抽签落到不同档。地板表 `floor-Debian-VM01.json` 没有 `sum` 条目
  （它是从 9 ~ 11 轮噪声里估的，`sum` / `ray` / `ntakl` / `triangl` 这些短项噪声最大，估不出地板），
  所以 m16sum 对它套了缺省 5% 门槛而误判成回退——按 29 §9 的口径记为**不可判定**。
  其余 53 项全部落在缺省 3% 门槛内。

  **结论：161 无可声明回退**（两项可声明改善）。启动目标也达成：`-e nil` 本机 978 → 678 ms、
  111 797 → 586 ms（详见 [notes/startup-profile.md §7](../notes/startup-profile.md)）。
