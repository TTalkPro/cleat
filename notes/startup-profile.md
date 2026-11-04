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

机器 vm-david（4 vCPU，SDK 10.0.100 / 运行时 10.0.0；本机是 8 核的 Debian-VM01 / 运行时 10.0.12）。
两棵树：`~/workspace/cleat-159-ref` = 基线 efec42f，`~/workspace/cleat-159-cur` = 159 的工作树。
**镜像与编译器包逐字节相同**（`src/boot/cleat.boot.dll` md5 `b7e257f7…`、`cleat.compiler.dll` `1334696f…`），
差异只在 `Cleat.Core.dll`（Loader / Startup / RT + 新类型 StartupTimings）与 `Cleat.Host.dll`。
判定门槛借用本机地板表 `bench/r7rs/results/floor-Debian-VM01.json`（本机 8 核测的地板，111 只有 4 vCPU，
噪声结构不同，只当量级参考）；原始数据在 `bench/startup/results/` 与 `bench/r7rs/results/`（不入库）。

### 6.1 分段口径在另一台机器上复现

`profile.py -n 5 default`（热窗口，wall 中位，极差括在括号里）：

| 场景 | wall | runtime | image（ifc + entry） | packages | compiler（require + hooks） |
|---|---|---|---|---|---|
| `-e nil` | 788（19） | 79 | 464（25 + 435） | 4 | 182（176 + 6） |
| `-e '(require (quote clojure.pprint))'` | 826（14） | 80 | 469（26 + 440） | 4 | 183（175 + 6） |
| `-r` 走到 REPL | 1368（16） | 79 | 463（26 + 435） | 4 | 184（177 + 6） |
| 首个提示符 | 1370（13） | 78 | 468（26 + 440） | 4 | 182（176 + 6），提示符前 1352 = user 6 + repl-requires 597 + 提示符 0 |

桶结构与本机 §1 ~ §3 一致（entry 与 compiler 是两格大头），绝对值快约 20%。160 / 161 的目标值仍按 §5 的**本机口径**验收。

### 6.2 配对先给出的"回退"

`pairbuild.py -r 5` 全 56 项（同一 rep 内两臂先后跑、等安静窗口，`order` 见 §6.4），sum / ray / array1 / fib / mazefun
补到 10 轮（`bench/r7rs/results/pair159.jsonl`，汇总 `bench/startup/results/pair159-summary-floored.txt`）：

| 项 | 5 轮配对 | 10 轮配对 |
|---|---|---|
| mazefun | x0.927（5/5 快） | x0.937（9/10 快） |
| ray | **x1.101（5/5 慢）** | x1.062（8/10） |
| sum | 不可判定（双态） | **x1.254（9/10 慢）** |
| array1 / fib | 不可判定 | x1.039 / x1.040 |

其余 51 项在 5 轮下都够不上地板门槛（同向数不足）。mazefun 的"更快"与 sum / ray 的"更慢"同时出现，
说明这几格不是简单的"插桩开销"。

### 6.3 机制取证：短基准落在两个量化档上，落哪一档被启动期的方法数挪动

1. **工作量缩放**（`probe159e.log` / `probe159f.log`，把 sum 的 count 乘 1 / 5 / 10）：秒数只有两个取值，
   两臂共用同样的两档 —— x1 `0.73 / 0.92`、x5 `3.18 / 4.19`、x10 `6.28 / 8.28`。
   慢档 / 快档恒为 **×1.32** 且差值与工作量成正比 ⇒ 慢态是**整段运行都在低档**（热循环没被升到 tier-1），
   不是"启动窗口没装完"的固定偏移；同一档内两臂同速 ⇒ 159 的代码没有改每-op 代码质量。
2. `DOTNET_TieredCompilation=0`：四臂并拢 ~1.63 → 差值只在分层 JIT 下出现。`DOTNET_TC_QuickJitForLoops=1` 不抹平。
3. **形状对照** `cleat-159-shape` = 基线树 + 只放一个**从不被调用**的 `StartupTimings.cs`（编译进去、没有任何调用点）：
   与基线同档 → 新类型 / 更大的 dll 本身不是原因。
4. **回退对照**（六臂轮换、每项 10 轮的快态率：`measure159.log`，快态 = 秒数 < 0.83）：
   shape（基线 + 一个从不被调用的 `StartupTimings`）**7/10**、ref **7/10**、`armS`（只回退 Startup 的标记）4/10、
   cur 2/10、`armR`（只回退 RT 的 in-ns 标记）1/10、`armP`（只回退 Program 的标记）1/10；再早的 `varB` / `varC`
   （回退 / 搬移 Loader 的标记）也一样偏慢。**单独回退任何一处都不会回到快态**，只有"插桩根本不被调用"的 shape 与基线同档
   → 元凶不是某一个调用点，而是"**CLEAT_TIME 关闭时仍然逐个调用插桩方法**"这件事本身。
5. **三臂 20 轮、每轮轮换首臂**（去掉 runner 固定臂序的混淆；快态 = 秒数 < 0.83）：

| 臂 | 快态率 | 对基线的配对比值中位 | 同向 |
|---|---|---|---|
| cur（159 提交版：调用点后判 `Enabled`，方法内早退） | **1/20** | **x1.250** | 17/20 |
| fix（未启用时零调用） | 14/20 | x1.003 | 12/20 |
| ref（基线 efec42f） | 17/20 | — | — |

位置不产生差别（首臂快态率 10/20、次臂 12/20、末臂 10/20），所以 §6.2 里的 ray / sum 不是"先跑的臂吃亏"。

**解释（不当断言用）**：未启用时 `Begin` / `Mark` / `MarkNamespace` / `EndBootEntry` / `Report` 仍被逐个调用，
`StartupTimings` 的类型初始化器（`long[]` + `Dictionary` + `List`）与这些方法都进了 tier-0 编译和 tier-1 队列；
在 4 vCPU 上热循环的 tier-1 安装时机因此后移，短基准整段停在低档。至于启动期方法数为何能把落档概率推移到
1/20 对 17/20 这种程度，CLR 侧没有公开口径 —— 与 158 交接给 169 的"跨构建镜像 IL 形状差（3.4× stloc）"同族，
继续跟到 169；160（R2R）会改掉整个分层 JIT 前提，届时重测。

### 6.4 收口动作

1. **159 的插桩改为"未启用时零调用"**：调用点自己判 `StartupTimings.Enabled`（RT 的 in-ns 处判 `InBootEntry`），
   `Loader.LoadModule` 在未启用时走与 159 之前**逐字同形**的路径（不开 `try/finally`、不进任何插桩方法），
   `Begin` 不再读环境变量（由 `Program.Main` 判过才调），三格表挪进 `Begin` 里分配。
   `CLEAT_TIME` 设置时的输出不变（本机 `-e nil` 与 `-r` 的分段行、in-ns 分桶、repl-requires 逐库都在），
   守卫 `HostProcessTests.StartupTimeSegments` / `StartupTimeReport` 2/2、Core 123/123。
2. **runner 加臂序轮换**：`bench/r7rs/pairbuild.py` 每轮交替"新构建先跑 / 基线先跑"，记录 `order` 字段
   （`R7_PAIR_SAMEORDER=1` 回到旧行为）。固定臂序会把上面的抽样偏置放大成看起来的单边回退 —— 这类跨构建配对
   以后都按轮换臂序出数。
3. 修后复核（2026-10-28 完成）：`pair159fix`（fix vs ref，全 56 项 × 5 轮、臂序轮换）在 111 上跑完，产物
   `bench/r7rs/results/pair159fix.jsonl`，汇总 `bench/startup/results/pair159fix-summary.txt`。
   **§6.2 那几格的"回退"随 fix 消失**：配对比值中位全落在 x0.955 ~ x1.058，56 项没有一项够上地板门槛（判定列全部"不可判定"），
   56 项配对比值中位的几何平均 **x0.998**。逐项对照（fix vs ref → §6.2 的 cur vs ref）：
   sum x1.011 → x1.254、ray x0.955 → x1.101、array1 x0.982 → x1.039、fib x1.003 → x1.040、
   mazefun x1.011 → x0.927（那格"更快"也一并消失，同向 2/5）。
   §6.3 的机制（启动期插桩调用次数挪动热循环 tier1 安装时机）与"臂序轮换消除抽样偏置"两头都对上了：
   现在跨构建配对不再偏向任何一臂，短基准的双态抽样仍要在 10 轮以上才谈得上量级判定。

## 7. cleat-161 编译器延迟加载（2026-10-28）

**做了什么**：`Startup.Start` 不再无条件 require 编译器驱动，只登记惰性入口
`RuntimeHooks.EnsureCompiler`；`RuntimeHooks` 的四个编译类钩子（eval / load-source / compile / macroexpand-1）
的 getter 在槽为空时先调它一次——Clojure 侧的 `clojure.core/eval`（`core.clj:3217`）与
`clojure.core/macroexpand-1`（`4095`）本来就直接读这些属性，所以 **C# 与 Clojure 共用同一个入口，
不改 `src/clojure`、不动种子**。`Startup.EvalForm` 在编译器还没加载时对**平凡形式**（自求值常量、`()`、
`(quote x)`、递归不含符号且不带元数据的 vector / map / set）直接在 C# 侧给值，不为一个常量付那一整格；
`-m` / `-X` / `-T` 跑包程序集里的 AOT 代码时全程不加载编译器。自举的改名工具编译器
（`CLEAT_COMPILER_NS != cleat.compiler`）保持现状仍然 eager；`--compile-boot` / `--compile-package` 自己先调
`Startup.EnsureCompiler()`。后台线程预热**没有做**（任务书允许、非必需：111 与本机都显示首提示符没有变慢）。

**平凡形式的边界**（`SimpleForms` + `SimpleFormsTests` 固定）：认 nil / true / false / 数字（含 `1N` 的 BigInt、
BigDecimal、Ratio）/ 字符串 / 字符 / 关键字、`()`、`quote`（含 `clojure.core/quote`）、纯常量的集合字面量；
不认符号（含 `*ns*`）、任何调用、任何特殊形式、集合里藏着符号或调用的字面量、**带元数据的字面量**
（`^:foo [1 2]` 一律交回编译器，口径不变）。认多了会算错，认少了只是多加载一次编译器，所以边界只往严的一侧收。
`CLEAT_NO_SIMPLE_EVAL` 设置时快速路径整体关掉（与驱动侧的简单形式解释器同一口径，语义逐字回到 161 之前）。

**`CLEAT_TIME` 的口径**：第一行 `compiler (package|source) N ms` 格式不变（159 的守卫依赖它），未加载时是 0。
编译器在启动报告**之后**才按需加载时补一行
`cleat: compiler loaded on demand: package 187 ms (require 183 + hooks 8)`——没加载就不出现，
`CLEAT_NO_SIMPLE_EVAL` 或任何要编译的形式就会让它出现。跑过报告才补（自举的 eager 路径不补）。

**实测（wall 中位，极差括在括号里；default 臂、热窗口）**：

| 场景 | 本机 159 | 本机 161 | 111 159-fix | 111 161 |
|---|---|---|---|---|
| `-e nil` | 978 (37) | **678 (36)** | 797 (9) | **586 (16)** |
| `-e '(require (quote clojure.pprint))'` | 1019 (70) | 923 (46) | 818 (10) | 817 (12) |
| `-r` 到 `:repl/quit` | 1809 (222) | 1588 (25) | 1375 (20) | 1380 (21) |
| `-r` 到首个提示符 | 1771 (144) | 1575 (116) | 1348 (—) | 1347 (—) |

`-e nil` 省下的正是 compiler 那一整格（本机 −300 ms、111 −211 ms，`CLEAT_TIME` 的 compiler 段变 0）。
pprint 场景的 `(require …)` 是要编译的形式，编译器照样加载（本机 923 里含按需的 195 ms），只是不再出现在启动报告里。
**REPL 首提示符不变慢**（验收要求）：本机 1771 → 1575、111 1348 → 1347——`repl-requires` 里
`clojure.repl.deps` 的失败尝试照样把编译器拽起来（本机这一格 752 ms，111 上 600 ms），
159 画像里"REPL 跳过不可用库"的那 −670 ms 不在 161 的范围内，仍然挂着（要动 `clojure.main/repl-requires` 或宿主的选择策略）。

**副作用（要记账）**：纯 AOT 进程（只跑包程序集里的 ns）不再有**运行时签名特化**——`RuntimeSigSpec.SpecCompiler`
由编译器驱动登记，编译器没加载时 `Install` 直接返回 null，调用照旧留在通用体。刻意没在 `Install` 里
`EnsureCompiler`：那会在触发的调用线程上加载 200 ms 的编译器，而 §3.3 的验收正是这条路不加载编译器。
从源码加载命名空间的路径（r7rs 基准、REPL、`-e` 的调用形式）本来就要加载编译器，不受影响。
份额留给 [cleat-163](../tasks/cleat-163-sigspec-ablation.md) 的 sigspec 消融裁决。

**门禁**：Release 0 warning；Core 128/128（新增 `SimpleFormsTests` 5 个用例）；全量 Host 291/291
（新增守卫 `CompilerLoadsOnDemandOnly`：平凡形式连打不出现补行、`(+ 1 2)` 出现、`CLEAT_NO_SIMPLE_EVAL` 下
`-e nil` 也出现、`-m` 跑包程序集不出现）；conformance `--gate` OK；CTS / test-clojure 与基线一致；
`selfhost` 不动点通过（304 s）。种子未动（`cleat.boot.dll` `b7e257f7…`、`cleat.compiler.dll` `1334696f…` 与 159 相同）。
性能纪律（29 §9）：改到 `Cleat.Core`，在 111 上跑 161 vs 159-fix 的 r7rs 56 项 × 5 轮同轮配对（臂序轮换，
产物 `bench/r7rs/results/pair161.jsonl`）。

**配对复核结果（2026-11-04 收口）**：111 上 560 格（56 项 × 5 轮 × 2 臂）全干净，0 error、0 loaded。
配对比值中位的几何平均 **x1.0043**（56 项，整体中性）。可声明项两项，均为**改善**：
`cat` x0.920（5/5，−8.0%）、`nucleic` x0.942（5/5，−5.8%）。
另有一项 `sum` x1.273（5/5）判为**不可判定**：111 上短基准的分层 JIT 双态（慢档 ≈ 快档 × 1.32，
见 `bench/r7rs/pairbuild.py` 注释），同项 rep1 是 x1.014（两臂打平），rep2 ~ rep5 抽签落到不同档；
地板表 `floor-Debian-VM01.json` 没有 `sum` 条目（`sum` / `ray` / `ntakl` / `triangl` 这几个短项噪声最大，
估不出地板），m16sum 对它套了缺省 5% 门槛而误判成回退。其余 53 项落在缺省 3% 门槛内。
**161 无可声明回退**，启动目标（`-e nil` 111 797 → 586 ms）达成。

复现：`CLEAT=~/workspace/cleat-161-new python3 bench/startup/profile.py -n 5 nil pprint repl prompt default`
（111 上的两份 JSON / 表在 `bench/startup/results/profile161-cleat-{161-new,159-fix}.{json,table}`，不入库）。
