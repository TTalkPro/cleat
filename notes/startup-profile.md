# 启动画像（cleat-159，2026-10-27/28）

> 设计依据：[29 §3.1](../designs/29-usability-and-engineering.md)。任务书：[cleat-159](../tasks/cleat-159-startup-profile.md)。
> 工具：`CLEAT_TIME` 分段计时（`src/Cleat.Core/Runtime/StartupTimings.cs`）、`bench/startup/profile.py`（场景 × 对照组矩阵）、
> `bench/startup/tracebuckets.py`（dotnet-trace 采样分桶）。原始数据在 `bench/startup/results/`（不入库）。

## 0. 环境与产品口径

- 本机（画像机）：Debian 13 / x86_64 / 8 核，.NET SDK 10.0.401，运行时 Microsoft.NETCore.App 10.0.12；
  Release 构建，2026-10-27 的工作树（HEAD `efec42f` + 159 的计时代码，编译器来自包路径 `cleat.compiler.dll`）。
- 111（复核机）：4 核，`~/.dotnet` 单文件运行时（版本见 §6）。`-e nil` 单跑 total 743 ms / image 467 ms / 编译器 184 ms
  —— 比本机低约 20%，桶的**份额结构一致**（core > prologue > pprint > protocols）。
- "wall" = `dotnet <host dll> …` 的进程墙钟（含 dotnet muxer 与 CLR 自身起来，约 50~60 ms）；
  "total" = 宿主 `StartupTimings.Begin()`（Main 第一行，大栈线程创建之前）到启动报告输出的时间线。
  两者都以"进程能跑"为终点口径，notes 的目标值用 **wall**。

## 1. CLEAT_TIME 分段（实现与形态）

`CLEAT_TIME` 未设置时不计时、不输出（`Begin` 读一次环境变量，之后所有采样点直接返回）；设置时输出四行：

```
cleat: Clojure image 646 ms, compiler (package) 205 ms          ← cleat-052 以来的第一行，格式不变
cleat: segments host 1 / cli 1 / paths 4 / runtime 93 / image 550 (assembly 1 + register 0 + ifc 34 + entry 513)
        / packages 5 / compiler 199 (require 191 + hooks 8) / other 0 = total 857 ms
cleat: image module init per in-ns boundary (top 10 of 24): clojure.core 147 ms, (entry prologue) 100 ms,
        clojure.pprint 84 ms, clojure.core.protocols 43 ms, clojure.clr.io 27 ms, …
（仅 REPL）cleat: repl setup 741 ms (user 7 + repl-requires 733 + prompt 0) = first prompt 1656 ms after start
（仅 REPL）cleat: repl-requires per lib: [clojure.repl :refer (…)] 41 ms, [clojure.pprint :refer (…)] 0 ms,
        [clojure.repl.deps :refer (…)] 671 ms
```

- 段的边界：`host`（Main→大栈线程入口）/ `cli`（命令行解析）/ `paths`（找镜像与编译器包）/
  `runtime`（Num 标签、原语注册、`RT.Init`、*out*/*err* 绑定）/ `image`（`cleat.boot.dll` 的加载 + 登记 + `.ifc` + 模块入口）/
  `packages`（旁边包程序集与 `src/clojure` 加载路径登记）/ `compiler`（require 驱动 + `install-hooks!`）/ `other`（报告前的剩余）。
- 间隔按 Stopwatch tick 累计、只在输出时换算毫秒（逐格截断会把镜像的 24 条 in-ns 边界丢掉几十毫秒）。
- **镜像入口按 in-ns 边界分桶**（`RT.cs` 的 `in-ns` 处 `MarkNamespace`）：`boot-order.edn` 的 52 条里 24 个 `:boot true`
  的条目边界，`s_lastNs` 把"上一条 in-ns 到下一条边界"记给上一条目——这一列就是"哪个 boot 命名空间的初始化最贵"。
- `repl` 场景再补两行：首个提示符写出前（阻塞读之前）记 `user` / `repl-requires`（逐库）/ `prompt` 三格。
- 守卫：`HostProcessTests.StartupTimeSegments`（顶层段与子段自洽、加总 ≤ total 且不漏 20 ms 以上）、
  既有的 `StartupTimeReport`（第一行格式 + REPL 行 + 逐库行）。

## 2. 怎么复现

```sh
dotnet build src/Cleat.Host -c Release
python3 bench/startup/profile.py -n 5                      # 4 场景 × 4 臂（热窗口，紧跑），表到 stderr、JSON 到 stdout
python3 bench/startup/profile.py -n 5 -g 20 default        # 冷口径（两次之间静默 20 s），只跑 default 臂
dotnet-trace collect --profile cpu-sampling --format speedscope -o /tmp/tr-nil -- \
    dotnet src/Cleat.Host/bin/Release/net10.0/cleat.dll -e nil
python3 bench/startup/tracebuckets.py /tmp/tr-nil.speedscope.json
```

`profile.py` 的场景：`nil`（`-e nil`）、`pprint`（`-e '(require (quote clojure.pprint))'`）、
`repl`（`-r` 到 `:repl/quit` 的整进程）、`prompt`（`-r` 只量到首个提示符出现，随后才喂 quit）。
对照组臂：`default` / `tier0-only`（`DOTNET_TieredCompilation=0`，禁分层＝预编译代码不再被 tier-1 重编）/
`quickjit`（`DOTNET_TC_QuickJitForLoops=1`）/ `no-r2r`（`DOTNET_ReadyToRun=0`，关掉框架自身的 R2R 作反证）。

## 3. 总量：场景 × 臂（本机，5 次中位 [min,max]）

热窗口（`-g 0`，2026-10-27，results/hot-matrix）与最新代码复跑（results/final-default）：

| 场景 | default wall | default total | tier0-only wall | no-r2r wall | quickjit wall |
|---|---|---|---|---|---|
| `-e nil` | **978 [974,1011]** | 917 | 1458 | 1429 | 1040 |
| `-e '(require …pprint)'` | **1019 [978,1048]** | 919 | 1474 | 1448 | 1051 |
| `-r` 到 `:repl/quit` | 1809 [1693,1915] | 956 | 2592 | 2146 | 1669 |
| `-r` 到首个提示符 | **1771 [1656,1800]** | 958 | 2592 | 2159 | 1723 |

冷口径（`-g 20`，2026-10-27，results/cold-default-matrix）：nil wall 929 [901,937]、pprint 1027、repl 1561
—— 冷并不比热慢（页缓存常驻、且静默期 CPU 降频结束），**极差反而更大**；下表用热中位做归因。

default 臂的段分解（热，`-e nil`）：runtime 104 / image 587（**entry 548** + ifc 36 + assembly 2）/ packages 6 /
compiler 212（require 201 + hooks 9）/ 前置（host+cli+paths）7 / other 0。
镜像条目份额（in-ns 分桶，top）：**clojure.core 147 / (entry prologue) 100 / clojure.pprint 84 / clojure.core.protocols 43 /
clojure.clr.io 27 / clojure.reflect 22**，其余 18 条合计约 30（24 条边界合计 ≈ entry 段）。

三个可直接读出的结构事实：
1. `-e nil` 与 pprint 场景几乎同价（pprint **已经在镜像里**，条目初始化 84 ms 已经在 entry 段付过）；
2. `-e` 与 REPL 共同的固定开销：**镜像 590 + 编译器 210 + runtime 100**；
3. REPL 首个提示符另有 **~790 ms**，其中 **671 ms 是 require `clojure.repl.deps` 的失败尝试**——
   该 ns 在 `src/clojure/clojure/repl/deps.cljc` 有源码，require 会真的走一遍编译器然后抛异常
   （宿主 `ReplRequires` 逐库 catch）。这一格既证明"编译器每被唤起一次就要几百毫秒"，也说明 161 的收益范围
   （REPL 少付这一格）。

## 4. JIT 的份额（对照组反证）

`cleat.boot.dll` / `cleat.compiler.dll` 由 PersistedAssemblyBuilder 产出，是**纯 IL、无 R2R**；每次启动都要 JIT。

| 反证 | 差值（热中位） | 归因 |
|---|---|---|
| tier0-only − default | **+480 ms**（entry +283、compiler require +89、runtime +19、其余 ~60） | 分层编译在启动窗口里的全部成本：tier-0 快编 + 后台 tier-1 重编与抢占。**上限**：R2R 把这 480 换成预编译代码时还要扣掉后台重编本来就省掉的那部分 |
| quickjit − default | +16~62（不可声明） | `QuickJitForLoops` 对本品无感；160 不依赖它 |
| default − no-r2r | **+451 ms**（runtime −79、ifc −70、packages −27、paths −11、assembly −9、entry −100） | **框架 BCL 程序集吃 R2R** 的份额：关掉后连 `RT.Init`、`.ifc` 登记、程序集加载本身都变贵。反证框架 R2R 与本品的收益是分开的两笔账 |

dotnet-trace 的分桶（cpu-sampling，采样只算执行线程，是"在做什么"的下界；JIT 总量看上表）：

| 桶 | `-e nil`（671 ms 托管时间） | `-r` 到提示符前（956 ms 跨度） |
|---|---|---|
| 镜像 cleat.boot | 254.0 | 291.7 |
| Cleat.Core | 116.9 | 143.0 |
| reflection/type | 73.8 | **142.5** |
| threading/locks | 99.1 | 112.6 |
| expression-compile | 53.8 | 74.8 |
| 编译器包 | 92.9 | 18.5 |
| native/wait（PollGCWorker 根帧：原生、后台 JIT、等待） | 109.9 | 76.8 |
| native/jit（被采到的 JIT 线程自身） | 24.0 | 24.1 |
| gc | 2.3 | ~0 |

自身耗时 top（nil）：`cleat.boot!#Module.Initialize()` 112 / `Monitor.Enter_Slowpath` 96 /
`DynamicMethod.CreateDelegate` 47 / `RouterModule.GetDefinedTypes()` 32 / `clojure.core$_reset_methods` 23。

- `Module.Initialize` ≈ 镜像模块 cctor（boot 条目顺序执行）——与 in-ns 分桶对得上，大头是 clojure.core 的 147。
- **`Monitor.Enter_Slowpath` 96~117 ms**：Var root / intern / 事务锁的启动风暴，锁本身就位（160 消不掉，
  但 JIT 掉进预编译代码后慢路径占比会降）。
- `CreateDelegate` 47 ms、`GetDefinedTypes` 32~90 ms：类型系统与反射（`.ifc` 登记、`GetTypeCore`）——对应 runtime 段 100 ms 与 ifc 36 ms。
- `expression-compile` 54~75 ms：Clojure 侧 `reify`/`deftype` 一类走 `System.Linq.Expressions` 的路径。

## 5. 结论：160 / 161 的目标值与预计收益

按桶给（本机热窗口 wall 中位）：

| 项 | 手段 | 预计收益（`-e nil`） | 依据 |
|---|---|---|---|
| cleat-160 R2R | crossgen2 预编译 boot / compiler / Libs（`src/boot/*.dll` 种子保持纯 IL） | **−250 ~ −350 ms**（entry 段 548 里的 JIT 份额 283 + 编译器 require 的 ~60 + 少量登记侧） | tier0-only 反证 +480 是上限；no-r2r 结构差证明同一形态在框架侧值 +451 |
| cleat-161 编译器延迟加载 | `-e nil` / AOT 路径不 require 编译器驱动 | **−210 ms**（compiler 段整格） | default 分解：require 201 + hooks 9 |
| cleat-161 附带 | REPL 跳过不可用库（`clojure.repl.deps`） | repl 场景 **−670 ms** | repl-requires 逐库：deps 失败尝试 671 / 782 |

**修订后的目标值**（替换 29 §3.1 的暂定值；口径 = 本机热窗口 wall 5 次中位）：

| 场景 | 现状（2026-10-27） | 160 之后 | 160 + 161 之后 |
|---|---|---|---|
| `-e nil` | 978 ms | **≤ 0.65 s** | **≤ 0.45 s** |
| `-e '(require (quote clojure.pprint))'` | 1019 ms | ≤ 0.68 s | ≤ 0.48 s |
| `-r` 到首个提示符 | 1771 ms | ≤ 1.15 s | **≤ 0.55 s** |

设计文档原先的暂定目标（`-e nil` ≤ 0.4 s、pprint ≤ 0.6 s）据此修订为：两项全落地才要求 0.4 s 量级，
单 160 先按 ≤ 0.65 s 验收；REPL 首提示符从"不在目标表里"改为显式目标（它是用户最先感知的数字）。
160 的验收画像：entry 段 ≤ 350 ms、tier0-only − default 的差缩到 < 150 ms（R2R 生效的直接反证）；
161 的验收画像：`-e nil` 不再出现 compiler 段（`CLEAT_TIME` 显示 compiler 0）。

## 6. 111 复核（2026-10-28）

（占位：pairbuild 窗口结束后同机跑 `profile.py -n 5 default`，回填。）
