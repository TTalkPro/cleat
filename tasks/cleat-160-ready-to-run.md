# cleat-160 ReadyToRun：Core、镜像、编译器包与随附库

- 里程碑：M19　依赖：cleat-159　状态：完成
- 设计依据：[29 §3.2](../designs/29-usability-and-engineering.md)

## 问题

`cleat.boot.dll`（2.0 MB）、`cleat.compiler.dll`（3.3 MB）、`Cleat.Clojure.Libs.dll` 是 PersistedAssemblyBuilder
产出的纯 IL，C# 项目也没有开 ReadyToRun，启动时所有初始化代码都要 JIT。

## 工作内容

1. 引入 crossgen2（`Microsoft.NETCore.App.Crossgen2`；本机 NuGet 缓存里暂时没有，需要恢复），在 `tools/build.cs`
   增加 R2R 步骤，覆盖 `Cleat.Core` / `Cleat.Emit` / `Cleat.Host` / `Cleat.Hosting` 与三个 Clojure 程序集。
2. **不动 `src/boot/*.dll`**：种子比较对象保持纯 IL。R2R 只作用在 `bin/` 输出、`publish`、`pack` 产物上；
   Core 重编后镜像必须一起重新 R2R（版本泡），构建链按此排序并加核对。
3. 评估 composite 模式（Core + 镜像一个版本泡，允许跨程序集内联）与逐程序集模式的差异，两种都量。
4. 确认 R2R 代码仍会被分层编译重编为 tier-1，长跑基准不受影响。

## 验收

- 159 画像中 JIT 份额下降，`-e nil` 达到或接近 159 定的目标值。
- `HostProcessTests`、`NuGetColdLoadTests`、`PackageTests` 在 R2R 包上通过；ilverify 只针对 IL 产物，不受影响。
- r7rs 56 项同轮配对（本机 5 轮）没有可声明的回退。
- 包体积变化写进任务书。

## 进展（2026-10-29）

- `tools/build.cs r2r` 从 Release bin + `src/boot` 复制出快照，对 Cleat.Core / Cleat.Emit / Cleat.Hosting /
  宿主 cleat / cleat.boot / cleat.compiler / Cleat.Clojure.Libs / Cleat.Tools* 做 crossgen2；`publish` 默认对
  自包含目录里的同一组程序集做 R2R，`--no-r2r` 保留对照臂；`pack` 在独立的 `artifacts/pack-r2r` 快照里编译，
  再把 nupkg 里的同名 cleat DLL 替换成 R2R 版本。`src/boot/*.dll` 原件保持纯 IL，种子比较不受影响。
- 画像脚本支持 `CLEAT_HOST` 指向 apphost。R2R 的量化必须走 apphost：`dotnet cleat.dll` 不会吃到这批
  app-local R2R（entry 仍在 ~540 ms），直接执行 `artifacts/r2r/cleat` 后 entry 降到 ~330 ms。
- 本机热窗口 5 次中位（apphost，Release 快照）：`-e nil` 473 ms / entry 330，pprint 663 ms / entry 337，
  REPL 首提示符 535 ms；tier0-only 与 default 并拢（nil 457 vs 473 ms，差值不可声明）。自包含 publish 对照：
  nil 700 → 497 ms、pprint 1032 → 705 ms，tier0-only − default 也缩到负 / ~-38 ms。159 的三项量化目标均达成。
- 体积：9 个 cleat 程序集 IL 6,520 KB → 13,980 KB（+7,460 KB；IL 保留）。自包含目录 89.4 → 96.4 MB
  （+7.0 MB）。Cleat.Runtime / Cleat.Cli 1.1.0 nupkg 分别 5.0 / 5.6 MB。
- composite 评估不采用：`crossgen2 --composite` 对 9 个程序集产出单个 native PE 8,205,312 bytes，
  逐程序集合计 14,315,520 bytes（省 6.1 MB）；但该产物需要 single-file 部署，不能替换多文件目录里的各个 DLL。
  临时 `PublishSingleFile=true + PublishReadyToRunComposite=true` 能编过，宿主在加载镜像前抛
  `$Constants` 类型初始化失败；Cleat.Core / Emit 现有 `Assembly.Location` / `DependencyContext.Load`
  路径也正是 publish 的 IL3000 警告点。把 single-file 兼容修成独立任务后再重测启动收益，本任务保留逐程序集模式。
- 待收口：r7rs 56 项 × 5 轮同轮配对、全量 R2R 包测试
  （已过 Host startup 2 项与 PackageTests 3 项；NuGet cold-load 端到端未完成）。

## 进展（2026-10-30）

- 修正 `Crossgen2()` 恢复路径：restore 之后重新解析工具路径；修复测试重定向时 Roslyn `VBCSCompiler`
  继承输出管道导致 fixture 等 EOF 挂起（构建脚本禁 MSBuild 节点 + 共享编译服务器，NuGet fixture 显式
  `UseSharedCompilation=false`）。`NuGetColdLoadTests` 4/4 通过，`tests/Cleat.Host.Tests` Release 构建通过。
- 默认 `ShouldR2R` 收窄为 compiler + 随附库 / tools 包（4 个程序集 4,066 KB → 8,288 KB）；
  `cleat.boot`、宿主 `cleat`、`cleat-emit`、`Cleat.Core`、`Cleat.Hosting` 保持 IL，`--only` 可做 opt-in。
- 同构建 on/off 配对（56 项 × 5 轮、同一快照，off 用 `DOTNET_ReadyToRun=0`）：11 项过地板判定，
  其中 8 项收益（equal x0.791、fib x0.836、conform x0.926、fibfp x0.910、graphs x0.908、maze x0.918、
  mbrot x0.902、pi x0.923）、3 项回退（bv2string x1.062、read1 x1.056、sum1 x1.102），判定项几何平均
  x0.935；56 项全表几何均值另见 29 §3.2。不满足"无回退"，不能标完成。
- 组件拆分（敏感项 × 5 轮、同快照 on/off）：compiler-only 与 Core-only 各自全部不可判定；
  两者组合时 ray x1.100（5/5）。跨 158 基线的旧 56 × 5 有 8 项回退，但混入 158→161 代码布局差异，
  只作诊断。`--Od` 不改三个回退项；`--compile-no-methods` 只剩元数据、没有方法体，不能作为启动方案。
- 当前默认产物 apphost 5 次中位：`-e nil` 668 ms（total 640 / entry 496 / compiler 0）、
  pprint 937 ms、REPL 首提示符 1588 ms（requires 716）。未达 159 的 ≤650 / ≤680 / ≤1150 目标；
  compiler 段为 0 来自 161 的延迟加载，不是 160 的证据。
- 组件归因矩阵（2026-10-30 下午，同一快照 `DOTNET_ReadyToRun=0`、敏感项 × 5 轮、臂序轮换）：libs-only 仍有
  `read1 x1.051`、`sum1 x1.084`（均 5/5）；fresh compiler-only 仍有 `ray x1.061`、`sum1 x1.104`
  （均 5/5）。compiler `--Os` 仍有 `sum1 x1.100`（5/5）；两臂同设 `DOTNET_TC_AggressiveTiering=1` 只是整体
  变慢并让 `bv2string x1.084`，不是修复。`--partial + StandardOptimizationData.mibc` 对 cleat 程序集没有命中，
  输出仍是 3.4 MB（无 R2R 方法体），不能作为启动方案。结论更新为：回退不是单一程序集组合造成，而是 R2R
  数据改变分层 JIT 热循环安装时机的系统性敏感点；crossgen 档位与分层开关都不能绕过。
- 测试链修复：NuGet fixture 的外层 `dotnet run` 会保留 MSBuild 节点，节点继承重定向管道导致等 EOF 挂起；
  fixture 现在显式 `UseSharedCompilation=false` + `MSBUILDDISABLENODEREUSE=1`（外层命令同样继承）。
  `Crossgen2()` 的默认缓存回退原来不检查文件是否存在，在隔离 `NUGET_PACKAGES` 下会跳过恢复直接指向
  不存在的 crossgen2；已改为存在性检查。`NuGetColdLoadTests` 4/4 通过，Release 构建 0 警告。
- 待做：做启动画像自己的 MIBC（`dotnet-trace` 采集 → dotnet-pgo/crossgen2 `--partial -m`）只预编译启动热方法，
  或把“同构建 R2R on/off 长跑无回退”改成明确的 160 放弃/拆分决策；收口前不改 159 的启动目标判定。
- **MIBC partial 候选（2026-10-30 傍晚）**：`dotnet-pgo` 没有上架 NuGet，官方 `dotnet-eng` feed 有
  `11.0.0-preview.6.26310.106`（裸 .NET 11 程序集；本机临时装 runtime 11 后可用）。用
  `DOTNET_ReadyToRun=0` 的快照采集 nil / pprint / REPL prompt 的 JIT trace（providers
  `Microsoft-Windows-DotNETRuntime:0x1E000080018:4`），三份 MIBC 合并后是 11,329 方法根 / 219 个带流量
  数据的方法。`crossgen2 -O --partial -m startup.mibc` 覆盖 9 个 cleat 程序集：IL 6,520 KB → 8,520 KB
  （+2.0 MB；快照目录还有 boot LCIL / PDB，`du -s` 25.1 MB）。同窗口 apphost 5 次中位：
  nil 461 ms / entry 319、pprint 665 ms / entry 332、REPL 首提示符 481 ms——160 的三项 159 量化目标
  通过；对比旧默认 R2R 同窗口 nil 762 / pprint 1064 / prompt 780。
- **partial 敏感矩阵**：同一 partial 快照，off 臂只加 `DOTNET_ReadyToRun=0`（`pairbuild.py` 新增
  `R7_OFF_ENV`），bv2string / ray / read1 / sum1 × 5、臂序轮换。按本机地板表判定全部“不可声明”：
  bv2string x1.013（同向 4）、ray x0.947、read1 x1.100（同向 4）、sum1 x1.099（同向 4）。没有重复旧
  R2R 的 5/5 稳定回退，但 read1 / sum1 仍偏慢，不能提前宣称通过。
- `r2r` / `publish` 新增 `--mibc <path>` 与 `--partial`。partial 时候选放宽到全部 cleat 程序集（含镜像），
  方法 rooting 由 MIBC 控制；宿主 / Core / Emit 也用 `-O`，与上述实测一致。默认产物仍保持旧保守集合，
  等 56 × 5 全表通过后才改默认。
- 56 × 5 全表已开跑并可续跑（`pair160-pgo-startup-56`），本窗口手动停在第 25 格；敏感矩阵通过不满足
  验收，任务仍进行中。

## 收口（2026-11-02）

- **partial 56 × 5 全表完成**（`pair160-pgo-startup-56`，560/560 格、臂序轮换、quiet 窗口，本机
  地板表判定）：判定 9 项——收益 6（chudnovsky x0.937、ctak x0.918、fibfp x0.946、graphs x0.886、
  maze x0.878、ray x0.892），**回退 3（fib x1.213、nucleic x1.067、read1 x1.081，均 5/5 超地板）**；
  判定项几何均值 x0.974，56 项全表几何均值 x0.989。加上全量 R2R 的 3 项回退（bv2string / read1 /
  sum1），**两条路线都不满足"无可声明回退"验收**。
- **裁决（照 29 §3.2 预设的决策点）**：放弃默认产物切 R2R。`tools/build.cs` 的 `publish` / `pack`
  R2R 改为 opt-in（`--r2r`；publish 的 `--no-r2r` 保留兼容写法，partial 另需 `--mibc`），默认产物
  回到纯 IL；`r2r` 命令与 `--mibc / --partial / --only` 保留为 opt-in 工具。159 的三项启动目标
  只在 opt-in partial 上达成（461 / 665 / 481 ms）并记账，默认路径未达标的缺口移交 cleat-169 复核；
  不改 159 的目标判定本身。
- 验证：默认 `publish` 产物 compiler 回到 IL 3.4 MB 且 `-e '(+ 1 2)'` 正常；`pack` 默认产出
  Cleat.Runtime 1.9 MB / Cleat.Cli 2.3 MB（纯 IL），`pack --r2r` opt-in 产出 3.7 / 4.3 MB
  （4 个程序集 4,066 → 8,288 KB，与之前一致）；build.cs 编译仅余 3 条既有 CS8600 警告。
