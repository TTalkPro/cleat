# M16 回归排查与测量口径（cleat-149，2026-10-20）

> 本文件是 cleat-149 的排查记录与结论。**不改产品代码**（`src/**` 只读），需要改运行时的修法只写在这里与任务书，
> 由后续任务（145 / 147 / 150）实施，避免与并行任务冲突。`notes/r7rs-performance.md` 由 owner 集成，本文件不引用其结论之外的数字。
> 设计依据：[28 §5](../designs/28-hot-path-optimizations.md)。工具：`bench/r7rs/triage149.py`（开关矩阵 / 多构建同窗口交替）、
> `bench/r7rs/bisect149.py`（二分与线性扫描）、`bench/r7rs/src/r7rs/probe.cljc`（探针口径与伪影自检）。

## 0. 本轮环境与口径

- 机器：本机（Debian 13 / x86_64 / 8 核），.NET SDK 10.0.401，**运行时 Microsoft.NETCore.App 10.0.12**；
  JVM = GraalVM CE 25.0.2（`UseJVMCICompiler=true`，即 **Graal 而非 C2**），Clojure 1.12.4。
- 与 192.168.3.111 的两点差异会直接影响结论可否复现：远程 .NET 运行时 10.0.0（服务更新差集体现在 JIT / GC），
  远程 JVM 是 OpenJDK 25.0.4 **C2**。所以本轮所有数字只在"本机 + 同一时间窗口 + 同一构建目录"内可比。
- 并发污染：本轮测量期间 144 / 147 / 148 三个 worktree 在同一台机器上构建与跑测试，1 分钟负载一度到 14.9。
  因此 triage149.py 的口径是：**跑每个样本前等到 load1 ≤ R7_MAXLOAD（默认核数的 25%）**，
  等不到就把这一轮记为"污染轮"（另存 jsonl、不进主口径），并额外报**同轮配对比值**（同一轮里参照设置 vs 对照设置的比值，
  取中位）——并发只把两边同时变慢，配对比值比"各自最小值相除"可靠得多。
- 双态项：max/min > 1.5 自动标 `双态?`，报告区间（全部轮次 + min/median/max），不取最小参与倍数。

## 1. 代码里真实存在的 `CLEAT_NO_OPT` 开关（先枚举再谈矩阵）

判据都是"环境变量含某个子串"（前 23 个是本里程碑之前的存量，后 3 个是 M16 的 144 / 147 / 148 新增；新增三个的行号按各自分支，合进 main 后会下移，**定位以符号名为准**）（`codegen.clj` 的 `opt-off?` 还额外把 `CLEAT_NO_OPT=1` 当作全关）：

| 开关 | 出处 | 关掉的东西 |
|---|---|---|
| known | `codegen.clj:70` / `analyzer.clj:462` | 用户已知函数的直接调用（cleat-110） |
| inline | `codegen.clj:71` | `.inl` 内联接口文件（只影响包 / 镜像构建） |
| infer | `codegen.clj:72` | 局部变量类型推断 |
| host | `codegen.clj:73` | 宿主调用编译期静态决议（cleat-042/101/106/111） |
| loopspec | `codegen.clj:74` | 循环 long / double 推测（cleat-103） |
| typed | `codegen.clj:75` | 无提示函数的类型化入口（cleat-104） |
| joinraw | `codegen.clj:76` | 原始用途穿过 if / begin（cleat-108） |
| boxcache | `codegen.clj:78` | long 装箱走 LongBox（cleat-109） |
| charbox | `codegen.clj:79` | char 装箱与 char 常量走 CharBox（cleat-142） |
| imagetier | `codegen.clj:80` | 镜像 / 包里的函数类分层编译（cleat-117；eval 路径不受影响） |
| arrayspec | `codegen.clj:81` | 循环里无提示数组按元素类型推测（cleat-127） |
| scalar | `codegen.clj:82` | deftype 循环变量标量替换（cleat-123） |
| collapse | `codegen.clj:83` | 内联子句的 arity 标签折叠（cleat-123） |
| typefeedback | `codegen.clj:84` | eval 候选的形参类型剖面（cleat-128；cleat-129 后默认不触发） |
| sigspec | `codegen.clj:85` | 运行时签名特化整条路径（cleat-133 ~ 140） |
| boxdouble | `codegen.clj:90` | double 装箱走 LongBox.Double（cleat-116） |
| num | `codegen.clj:92` | Num：未定类型数的局部 / 形参（cleat-102） |
| loopweight | `codegen.clj:604` | 候选按循环加权（cleat-139） |
| loopinl | `analyzer.clj:493` | 循环体里不内联"展开后还调用用户函数"的小函数（cleat-120） |
| letrec | `front.clj:123` | letrec 绑定提升为运行期常量（`const-in-run?`） |
| strseq | `RT.cs:97` / `StringSeq.cs:23` | String 的 first / seq / next / more 直读（cleat-143，**运行时代码**） |
| intern | `RT.cs:113` / `Keyword.cs:71` | keyword intern 字符串键快路径与 `(str <long>)`（cleat-146，**运行时代码**） |
| tiered | `Emitter.cs:93` | eval 单元的分层编译，回到 RunAndCollect（cleat-114，**发射器**） |
| nthfast | `RT.cs:117`（`s_nthfast`） | `RT.nth` 的 ISeq / LazySeq / StringSeq / counted-list 快路径（cleat-147，**运行时代码**） |
| crossnstype | `RT.cs:2853`（`s_crossNsTypes`）/ `analyzer.clj:67`（`opt-crossns-type?`） | 跨 ns 生成类决议与 gen-interface 编译期桩（cleat-144，**分析器 + 运行时**） |
| gcprofile | `src/Cleat.Host/GcProfile.cs` | `CLEAT_GC_PROFILE` 的声明校验（cleat-148，**宿主**；关掉只是不校验，不改 GC 配置） |

两个坑（写进矩阵的解读，别当成开关失效）：

1. `CLEAT_NO_OPT=1` **不等于全关**：只有 `codegen.clj` 的 `opt-off?` 与 `front.clj` 认 `"1"`；
   `analyzer.clj` 的 known / loopinl 用 `.Contains "known"` / `"loopinl"`，运行时侧 strseq / intern / tiered 用
   `.Contains <名字>` —— 所以 `=1` 时这三类仍然开着。要真全关得写 `1,known,loopinl,strseq,intern,tiered`。
2. 判据是**子串**匹配：`sigspec` 含 `spec`，但 `spec` 本身不是开关；反过来 `typed` 与 `sigspec`、`charbox` 与 `boxcache`
   互不包含，单开关隔离是安全的（M16 的 lattice 归因就是这么抓到 strseq 的）。

## 2. 端点复现性：M16 的改动不回退，M14/M15 的改动方向一致地回退

三构建同窗口交替（`--shuffle`，每格 5 轮，跑前等 `load1 <= 核数 x 25%`），设置 = 构建目录：
`old` = `/tmp/bisect-149`（**6ed0da4 / cleat-118**，M14 之前）、`m15` = `/tmp/m15-149`（**65d407d / M15 收口**）、
`head` = 本 worktree（**4b29e38**，即 m15 + cleat-142 / 143 / 146 + 分支顺序修正）。
主口径 = **同一轮内 设置/参照 的比值取中位**（并发与漂移对两边同等作用）；`参照 = m15`。

| 项 | old/m15 配对（5 轮） | 中位 | head/m15 配对（5 轮） | 中位 | 该设置内极差 max/min |
|---|---|---|---|---|---|
| parsing | 0.976 1.103 1.019 0.956 0.814 | **x0.976** | 0.888 1.235 1.235 1.120 0.833 | **x1.120** | old 1.22 / head 1.36 |
| mbrot | 0.904 0.913 0.812 0.954 0.875 | **x0.904** | 0.896 1.044 0.913 1.148 1.047 | **x1.044** | old 1.17 / head 1.26 |
| pi | 0.998 0.788 0.937 0.938 0.857 | **x0.937** | 0.817 1.074 0.961 1.133 0.961 | **x0.961** | old 1.30 / head 1.43 |
| dynamic | 0.759 0.883 0.968 0.829 0.812 | **x0.829** | 0.652 0.970 0.949 1.122 0.898 | **x0.949** | old 1.30 / head 1.36 |

- 四项几何平均：**old/m15 = x0.910**（cleat-118 比 M15 快 9%），**head/m15 = x1.016**（M16 比 M15 慢 1.6%）。
- **结论 1（M16 无罪）**：`head` 的每一轮配对都跨 1（parsing 0.833 ~ 1.235），项内极差 1.17 ~ 1.43 远大于 1.6% 的
  整体读数 → M16 的 142 / 143 / 146 在本机 **不构成可判定的回退**；这与 192.168.3.111 上 11 项 geomean 0.955
  （开优化更快）不矛盾：那 11 项里没有 parsing，且 111 是安静机器、极差通常 < 3%。
- **结论 2（回退确实在 M14 / M15）**：`old/m15` 在 mbrot（0.812 ~ 0.954）、pi（0.788 ~ 0.998）、dynamic
  （0.759 ~ 0.968）上**每一轮配对都 < 1**，方向一致 → 相对 cleat-118 的回退是真的，量级 7% ~ 21%
  （比任务书里 111 上记录的 15 ~ 28% 小，因为本机 JVM 是 Graal 而非 C2，且 runtime 是 10.0.12）。
  parsing 是唯一方向不一致的项（0.814 ~ 1.103）→ 任务书里的 "parsing +24%" 在本机不复现。
- **本机噪声地板**：同一构建、同一项、等过 load 之后，5 轮的极差仍有 17 ~ 43%。所以本机只能判"方向"
  （5/5 或 0/5 一致性），**不能判百分比**；任何 < 10% 的判定必须在 111 上做。这一条写进 §6 的口径清单。

## 3. 开关矩阵：单开关关闭不是线性成本，有两个悬崖读数

`bench/r7rs/triage149.py --settings base,no<name>=no:<name>`（§1 的 23 个开关逐个），项 = mbrot / pi，
每格 1 ~ 2 轮（并发窗口，只有 noknown / noinline 有 2 对）。`参照 = base`，值 = 关掉该开关后 / base。

| 关掉 | mbrot | pi | | 关掉 | mbrot | pi |
|---|---|---|---|---|---|---|
| known | x1.079 | x1.094 | | charbox | **x0.948** | **x0.856** |
| inline | x1.134 | x1.151 | | arrayspec | x0.955 | x0.894 |
| infer | **x16.97** | x1.152 | | imagetier | **x0.904** | x0.898 |
| host | **x202.7** | x1.356 | | num | **x0.906** | x0.905 |
| loopspec | x1.039 | x1.020 | | boxcache | x0.966 | x0.879 |
| typed | x1.048 | x1.109 | | loopweight | x0.972 | x0.903 |
| joinraw | x1.061 | x0.993 | | strseq | x0.942 | x0.886 |
| scalar | x1.004 | x1.142 | | loopinl | x1.021 | x0.933 |
| collapse | x1.016 | x1.084 | | letrec | x1.081 | x0.993 |
| typefeedback | x1.038 | x1.025 | | intern | x1.024 | x0.948 |
| sigspec | x1.062 | x0.967 | | tiered | **x0.798** | x0.953 |
| boxdouble | x1.113 | x0.902 | | 全关(1+known+loopinl+strseq+intern+tiered) | **x0.836** | x1.007 |

读法与限制：

1. **>1 = 这个开关确实在帮忙**（关掉更慢），与既有结论一致的是 known / inline / host / typed / scalar /
   collapse / boxdouble（pi）。**<1 = 这个开关在本机这两项上是净成本**：charbox / strseq / intern /
   boxcache / num / imagetier / arrayspec / loopweight / tiered 都出现 0.80 ~ 0.95。
2. **两个悬崖读数**：mbrot 关 `host` = 145.8 s（base 0.719 s，**x202.7**）、关 `infer` = 12.2 s（x16.97），
   而全关只 0.601 s（全关的组合里没有 host / infer）。复测（同一脚本第 2 / 3 轮）见下方补记：
   这不是"慢一点点"的退化，而是关开关后掉进另一条路径（装箱链 / 反射式宿主调用 + 运行时签名特化的
   反复重编译），所以**开关的贡献不可加**，矩阵里 >2x 的格子只能当"该路径不能整体关掉"的证据，
   不能用来推算"开关注册了多少收益"。
3. 每格 1 ~ 2 轮 + 本机 17 ~ 43% 的项内极差 → **本表只有方向，没有量级**。111 上的同表才是可用的归因，
   已由 `bench/r7rs/m16full.py` 的 `NOPT` 覆盖成"只关一项"的形式（cleat-150 的收尾动作）。

补记（复测 host / infer 悬崖，安静串行、`-m r7rs.mbrot` 同一 stdin / cwd，本机 runtime 10.0.12）：

| 轮 | `CLEAT_NO_OPT=host` | `=infer` | 都不设（base） |
|---|---|---|---|
| 1 | **158.44 s** | 11.39 s | 0.800 s |
| 2 | **147.15 s** | 9.70 s | 0.705 s |

悬崖是真的、可复现的：关 `host` 后 mbrot 慢 **198x / 209x**（两轮各自对同轮 base），关 `infer` 慢 **14x / 14x**。合理解释是关掉宿主调用静态决议之后，mbrot 的 `Math/Sqrt` / 数组访问一类调用
落到每次调用的名字决议 + 装箱链（并触发运行时签名特化的反复重编译，`rtspec compiled=3 / compileMs 159`），
不是"每次慢几纳秒"的量级。**这条本身是一个健壮性缺陷**（退化路径应该是常数倍，不是 200 倍），
但它不影响开侧性能，归到 §7 的待办而不是本轮修复。

## 4. 二分 / 线性扫描：本轮没跑 commit 级 bisect，原因与替代口径

- `bisect149.py` 已就位（`--build-cmd` / `--items` / 每 commit N 轮取最小 + 同轮配对），
  但 6ed0da4..65d407d 有 **31 个 commit**，本机每次构建 ~105 s、每项 3 轮，且 §2 的噪声地板是 17 ~ 43%：
  在本机 bisect 出来的是噪声，不是结论。跑满一遍约 25 分钟机时，只有在 111（安静、极差 < 3%）上才有意义，
  而 111 的机时要先留给 cleat-150 的整体三臂重跑。
- 替代口径（已做）：**三构建端点**（§2）把区间收窄到 "M16 无罪 / M14+M15 有罪"，
  **单开关矩阵**（§3）把候选集合收窄到 M14/M15 引入的入口选择与特化类开关：
  `sigspec` / `typed` / `loopweight` / `typefeedback` / `collapse` / `scalar` / `imagetier` / `tiered`。
  其中 mbrot / pi 上"关掉反而更快"的 `tiered`(x0.798) / `imagetier`(x0.904) 与 M15 的
  RuntimeSigTable 兼容条目 / 负表 / LRU 正是任务书怀疑的那批。
- 下一步（写进 §7，交 cleat-150 之后）：111 上 `bisect149.py` 跑 5 步（log2 31）限定 dynamic + mbrot 两项，
  3 轮取最小 + 同轮配对；找到 commit 后只对那一个 commit 的开关做 A/B，确认修复不撤销 ctak / fibc / destruc。

## 5. 测量口径修正（已落地到代码）

### 5.1 探针的数据依赖与伪影自检

- 新增 `bench/r7rs/src/r7rs/probe.cljc`：`timed` 宏把每个形状的结果**写进 sink 数组的槽位**（下标随循环变量变化），
  循环后按校验和读回；实参一律经 `opaque-long` / `opaque-obj` 从数组里按循环变量取（JIT 无法证明它是常量）。
  出图同时给 `raw`（每次迭代总时间）与 `net = raw − 环底`（环底 = 同样的取参 + 写回骨架，不含被测操作）。
- 两档自检：
  - **动态**（`ARTIFACT?`）：`net ≤ 0.3 ns` 或 `net ≤ 0.15 × raw` → 这一侧看不见被测操作，数字不可用于倍数；
  - **静态**（`DEAD-VALUE`）：`dead-value-control` 宏 = 改口径前的写法（结果丢弃、实参与循环变量无关），
    按定义不可比，不需要跑到慢值就会标出来。
- `micro.cljc`：保留原有累加链（结果参与数据依赖），新增两条环底（F-empty 纯循环+累加、F-load 再加一次不透明读）、
  `net-empty` / `net-load` 两列、`SUSPECT(<= 纯循环环底)` 标记，并给最容易被提出循环的两个读操作补了
  `mcar-read-opaque` / `mcdr-read-opaque` 变体（实参按循环变量取，提出不了）。
- `shapes2.cljc` / `shapes3.cljc`：全部形状改为 `timed`；各自保留 `dead-*` 对照行；头注写明
  "只有 net 列可用于跨宿主倍数"。M15 表里 nth / first / char 常量比较的倍数按 designs/28 §1 已作废，本轮把结论变成
  **结构上不可能再犯**（不写 sink 的形状会被标 DEAD-VALUE）。
- `bench/r7rs/run.py` / `attr.py` 头部加了同一条纪律（探针结果必须参与数据依赖，否则 JVM 侧数字无效），
  并把"探针对照纳入里程碑收尾"写进注释。

### 5.2 自检抓到伪影的实测演示（本机，load 7 ~ 8 的污染窗口，只看定性）

| 形状 | cleat raw / net | JVM(Graal) raw / net | 自检 |
|---|---|---|---|
| `dead-mcar`（`(dotimes [_ n] (mcar p))`，旧口径） | 5.4 / +4.5 ns | **1.3 / −0.1 ns** | JVM 侧 `DEAD-VALUE+HOISTED?`（≤ 纯循环环底 1.45 ns） |
| `mcar-read-opaque`（同操作，实参不透明） | 10.0 ns | 3.0 ns | 与 `mcar-read` 的差 = 被提出循环的量（JVM 2.4 → 3.0） |
| `dead-iface-direct`（`(.getCar ^LP p)` 死值） | 6.60 / +0.52 ns | **1.71 / −2.81 ns** | 两侧都标 `DEAD-VALUE+ARTIFACT?`（cleat 也被折叠，JVM 被整体提出） |
| `dead-eq-char`（`(= \a \a)` 死值） | 5.96 / −0.01 ns | 2.32 / −2.67 ns | `ARTIFACT?`（M15 曾据此写"char 常量比较慢 11.7x"） |
| `dead-first-list` | 8.62 / +2.65 ns | 6.49 / +1.50 ns | cleat `ok` / JVM `ARTIFACT?` → 同一形状两侧含义不同，倍数无意义 |
| `loop-sub`（`(- acc i)`，无提示） | 0.8 ns（F-empty 0.95） | 1.3 ns（F-empty 1.45） | 两侧都 `SUSPECT`：减法的净成本落在环底以内，本来就不该报倍数 |

对照 M15 记录的旧数字：JVM `mcar 读 1.3 ns`（旧 micro 表）与本机 `mcar-read-opaque 3.0 ns`、`nth-list 0.7 ns`（旧 shapes 表）
与本环 `nth-list net 23.6 ns` —— 旧值就是被 LICM / 标量替换消掉后的残值，自检现在能说出来。

### 5.3 双态项（takl）的区间断言

规则（已落到 `triage149.py` / `summarize149.py` / `compare.py`）：一格内 `max/min > R7_DUAL_RATIO`（默认 1.5）
判为双态，**报区间不报最小**；跨态相除得到的倍数一律作废。

- cleat 侧 takl：192.168.3.111 的 M15 全量三针 17.859 / 17.872 / 17.209，极差 **1.04** → 单态，可用最小值。
- JVM 侧 takl：同一批 34.773 / 37.293 / 35.015（极差 1.07，**批内看不出来**），但 cleat-119 同机同源码的最小值是
  14.20 s，M15 记录的单复跑是 34.46 / 37.35 / **12.05** → 双态（快态 ~12 s，慢态 34 ~ 37 s），
  根因在 `mas` / `shorterp` 的分层编译落点，不是负载。
- **断言**：takl 的 cleat/JVM 倍数只能写成区间 —— cleat 17.21 ~ 17.87 对 JVM 12.05 ~ 37.35，
  即 **0.46x ~ 1.43x**；批内极差 1.07 不违反双态判定（双态是"态间"差异，不是"轮间"差异），
  所以判定要靠**跨批次 / 跨窗口的最小值差**（12.05 vs 34.77 = 2.9x），不能只看一轮的极差。
- 影响：含 takl 的 56 项几何平均在快态 / 慢态之间差约 0.03x（M15 记录 1.61x → 1.64x）。整体重跑时
  takl 必须**同时报两态**并把几何平均按"剔除 takl 的 55 项"另列一行，否则里程碑间的倍数不可比。

## 6. 整体性能测试前，测量 / 验证口径上还剩的坑

1. **并行 worktree 让 Host 全套失去意义**（本轮 144 / 147 / 148 实测，四类互不相干的假失败）：
   - `tests/clojure-test-suite` 是 submodule，worktree 不自动 checkout →
     `ClojureTestSuiteMatchesItsBaseline` 必报 `DirectoryNotFoundException`（与代码无关）。
   - `ClojureBootstrapTests` 用固定目录 `/tmp/cleat-clojure-bootstrap`、`build.cs selfhost` 默认
     `/tmp/cleat-selfhost` → 两个 worktree 并发自举互相覆盖 / 删除 dll，报
     `module not found`、`runtime hook 'load-source' is not registered`、`stage2 and stage3 differ`。
     修法：`-- selfhost <每任务独立目录>`（本轮已这样跑通），测试侧长期要改成 per-test 临时目录。
   - `PackageTests.Nrepl` / `BothFromPackageAssemblies` 并发时 nREPL transport 端口与捕获的 stderr 串台，
     出现假的 `InvalidCastException: String -> Number`（栈在 `Numbers.ops` / `Num.SlowDivide`，
     实为别的进程的输入）。串行复跑即通过。
   - 1 分钟负载 5 ~ 14 时 3 分钟级的项直接超时。
   - **纪律**：worktree 阶段只跑 targeted（`--filter` + Core 全套 + conformance/cts 三套读数），
     Host 全套与 gate 只在合并后的 main 上**串行跑一次**。
2. **本机的百分比不可信**（§2：等过 load 之后项内极差仍 17 ~ 43%），只能判方向；
   111（4 vCPU、安静、极差 < 3%）才是判定机器。跨机比较还要先对齐 .NET runtime（本机 10.0.12 / 111 是 10.0.0）。
3. **开关贡献不可加**（§3 的 host / infer 悬崖）：矩阵只能回答"关掉会不会整体崩"，
   不能把 `x0.856` 直接读成"该开关贡献 14%"。
4. `CLEAT_NO_OPT` 的两坑（§1）：`=1` 不是全关；判据是子串匹配，新开关的名字不能是已有开关名的子串
   （`nthfast` / `crossnstype` / `gcprofile` / `accinline` 目前与 23 个既有名字互不包含）。
5. 死值探针伪影（§5.1 / §5.2）：探针必须参与数据依赖，否则 JVM 侧被 LICM + 标量替换消掉，
   倍数没有意义 —— M15 的 "char 常量比较慢 11.7x" 就是这么来的。
6. **新增开关必须进 `m16full.py` 的 `DEFAULT_NO_OPT`**，否则三臂对照的"关侧"不是真正的 M15 行为，
   A/B 的关侧会残留新优化（本轮 150 的 runner 已经踩过一次：名单是三样 charbox,strseq,intern，
   合并 147 / 144 / 148 后是五样）。
7. 双态项（§5.3）：takl 报区间；整体重跑时按 55 项另列几何平均。

## 7. 待实施的修法（本任务不改产品代码）

| # | 待办 | 归属 | 判据 / 风险 |
|---|---|---|---|
| 1 | 111 上 bisect 6ed0da4..65d407d（dynamic + mbrot，5 步，3 轮取最小 + 同轮配对） | cleat-150 之后 | 只在 111 上做；找到 commit 后单开关 A/B 复认 |
| 2 | 候选修复面：`sigspec` 负表 / LRU、`tiered` / `imagetier` 落点（§3 里关掉更快的四项） | 新任务（M17） | 修复不得撤销 ctak / fibc / destruc 的收益 |
| 3 | `m16full.py` 的 `DEFAULT_NO_OPT` 补 `nthfast` / `crossnstype` / `gcprofile`（合并后按需补 `accinline`） | cleat-150 | 名单与 §1 表同源，逐条带出处 |
| 4 | 闭包提升（`closurlift`）与变长参数数组（`varargs`）：cleat-147 本轮只落 `nth`，两条量化探针留在 `bench/r7rs/src/cleat147_alloc_probe.cljc` | cleat-147 后续 / M17 | peval 闭包占分配 17%，需同窗口 alloc.cs 证据 |
| 5 | `CLEAT_GC_PROFILE=batch` 在 111 上做 array1 / peval / cpstak 同窗口 A/B（三变量前缀见 cleat-148 任务书） | cleat-148 后续 | 收益若 < 3% 只记录，不接进默认 |
| 6 | 把 `selfhost` / bootstrap 测试的固定 `/tmp` 目录改为 per-test 临时目录 | 工程性小任务 | 并行 worktree 与 CI 都会踩 |
| 7 | 145（小访问器跨编译单元内联）依赖 144 的跨 ns 类型决议落地后才可开工 | M16 | 内联决定跨自举阶段必须稳定（种子不动点） |
| 8 | **退化路径的 200x 悬崖**：`CLEAT_NO_OPT=host` 时 mbrot 0.7 s → 147 ~ 158 s（§3 补记），关掉静态决议后每次宿主调用落到名字决议 + 装箱链并反复触发签名特化 | M17（健壮性） | 不影响开侧性能，但让"用开关做对照"的实验一旦误设就完全失真 |
