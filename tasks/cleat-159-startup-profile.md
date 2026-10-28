# cleat-159 启动剖析与目标值

- 里程碑：M19　依赖：无　状态：完成（2026-10-28，见文末收口）
- 设计依据：[29 §1 第 2 条 / §3.1](../designs/29-usability-and-engineering.md)

## 问题

本机（2026-10-26）`cleat -e nil` 需要 0.9 ~ 1.3 s。`CLEAT_TIME=1` 显示：Clojure 镜像 671 ms，编译器包 193 ms。
目前还不知道 671 ms 里 JIT、类型加载、命名空间初始化、常量池 / fasl 反序列化各占多少，160 / 161 的范围定不下来。

## 工作内容

1. `CLEAT_TIME` 扩展为分段计时：程序集加载、模块初始化（按 `boot-order.edn` 条目）、常量池、编译器包、宿主命令行解析。
   不设置时不输出、不计时（与现状一致）。
2. `dotnet-trace`（已安装）采集 `-e nil`、`-e '(require (quote clojure.pprint))'`、`-r` 到首个提示符三种场景，
   按 JIT / 类型加载 / GC / 用户代码分桶。
3. 对照组：`DOTNET_TieredCompilation=0`、`DOTNET_TC_QuickJitForLoops`、`DOTNET_ReadyToRun=0`（关掉框架 R2R 作为反证），
   确认 JIT 的份额。
4. 在 notes 增加"启动画像"一节，给出 160 / 161 的目标值。暂定 `-e nil` ≤ 0.4 s、require pprint ≤ 0.6 s，
   以本任务的画像为准修订。

## 验收

- 画像可以复现：命令、环境、5 次中位与极差都写进 notes。
- 160 / 161 的目标值和预计收益按桶写清楚。
- 只改计时代码与文档；改到 `src/clojure` 时重走种子。

## 收口（2026-10-28）

- 分段计时落地：`StartupTimings`（CLEAT_TIME 未设置时不计时、不输出）+ 镜像 in-ns 分桶 + REPL 首提示符两行
  （逐库 repl-requires）；守卫 `HostProcessTests.StartupTimeSegments` / `StartupTimeReport`。
- 画像落地：[notes/startup-profile.md](../notes/startup-profile.md)（三场景 × 四臂矩阵、dotnet-trace 分桶、160 / 161 的
  目标值与预计收益），工具在 `bench/startup/`（profile.py / tracebuckets.py，原始数据 results/ 不入库）。
- 关键数字（本机热窗口 wall 中位）：`-e nil` 978 ms = 镜像 entry 548（JIT 份额 ≈ 283，tier0-only 反证 +480）
  + 编译器包 212 + runtime 104；`-r` 首提示符 1771 ms，其中 671 ms 是 require `clojure.repl.deps` 的失败尝试。
  修订目标：160 后 `-e nil` ≤ 0.65 s、160+161 后 ≤ 0.45 s、首提示符 ≤ 0.55 s（已写回 29 §3.2 / §3.3 的验收行）。
- quickjit 臂与 default 差在噪声内（160 不依赖 QuickJitForLoops）；冷（-g 20）不比热慢。
- 性能纪律（29 §9）：本任务改到 `Cleat.Core`（计时代码，Enabled=false 时全部早退），在 111 上做
  HEAD `efec42f` vs 工作树的 r7rs 56 项同轮配对 5 轮（pairbuild，窗口 2026-10-28），结果与 111 侧启动画像见 notes §6。
- **111 复核发现的真实回退源**（notes §6.3）：4 vCPU 的 111 上，短基准（sum / ray）整段落在两个量化档之一
  （慢档 = 快档 ×1.32），`Cleat.Core` 里多几个"关掉时仍被逐个调用"的插桩方法就会把热循环的 tier-1 安装时机推后，
  sum 的配对比值因此到 x1.25（cur 快态 1/20）。只放一个从不被调用的 `StartupTimings` 类型的对照臂与基线同档（7/10），
  单独回退任何一个调用点都不回到快态 → 是"调用插桩"本身，不是某个点。
  据此把插桩改为**未启用时零调用**：调用点自判 `Enabled`（RT 的 in-ns 处判 `InBootEntry`），
  `Loader.LoadModule` 未启用时走与 159 之前逐字同形的路径，`Begin` 不再读环境变量（由 `Program.Main` 判过才调），
  计时表在 `Begin` 里分配。20 轮三臂复核：fix 对基线 x1.003（12/20）、快态 14/20，与 ref 的 17/20 同档；
  `CLEAT_TIME` 设置时的分段输出不变。
- runner 加**臂序轮换**（`bench/r7rs/pairbuild.py` 每轮交替新构建先跑 / 基线先跑，记 `order`；`R7_PAIR_SAMEORDER=1` 回旧行为）：
  固定臂序会把上面的抽样偏置放大成看起来的单边回退。以后跨构建配对按轮换臂序出数。
