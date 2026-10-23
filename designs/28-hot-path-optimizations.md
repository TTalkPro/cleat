# 28 热路径优化：M15 归因复核后的修正优先级

> 状态：**进行中**（2026-10-20 起，M16）——§2.1 / §2.2 / §2.4 已实现并在 192.168.3.111 上完成同窗口开 / 关验证
> （[notes 的 M16 一节](../notes/r7rs-performance.md)：wc -39%、browse -4%、read1 -2.5%；lattice 因 strseq
> 分支位置回退 +6.3%，已按 §6 的教训修正）。§3 的第 1 条（RT.nth 快路径 nthfast）与 §4（GC profile 声明化 /
> LOH 判定不做）、§5（回归排查与口径修正）已完成；**§2.3 已于 2026-10-21 完成**（cleat-145 落地，
> 开关 `accinline` 但**默认关闭**（opt-in：`CLEAT_OPT_ACCINLINE=1`），见 [tasks/cleat-145](../tasks/cleat-145-accessor-inlining.md) 与
> [notes M16 §8](../notes/r7rs-performance.md)）；**还剩 §3 的第 2 / 3 条**（闭包提升、变长参数数组：
> 147 量不出每次迭代成本，按 designs/28 §3 交 M17），
> 以及 §6 之后的收口重跑 cleat-150。来源：[notes/r7rs-performance.md](../notes/r7rs-performance.md) 的
> "M15 差异归因"与"M15 优化建议 §5 复核修正"、探针 `bench/r7rs/src/{micro,shapes2,shapes3}.cljc`、
> 归因脚本 `bench/r7rs/attr.py`。承接 [21](21-r7rs-gaps.md)、[22](22-gc-and-performance-followups.md)
> 与 [27](27-jit-style-runtime-specialization.md)；任务 cleat-142 ~ 150。
>
> 本文的第一职责是**修正 M15 优化建议里被微探针伪影污染的论据**（§1），然后给出复核后仍然成立、
> 且每条都有真实基准分配画像或开关对照支撑的优先级（§2 ~ §5）。
>
> **测量机变更（2026-10-20 下午）**：192.168.3.111 离线（ping 100% 丢包、ssh 不通），本文剩下的
> 同窗口开 / 关 A/B 与 §5 的全量重跑改在本机（i9-13900H / 8 线程 / .NET 10.0.12 /
> OpenJDK 25.0.4 HotSpot）执行，口径见 [notes 的 M16 §5](../notes/r7rs-performance.md)：
> 判定只认**同轮配对比值**（每格先等安静窗口，至少 5 轮），本机噪声地板 17 ~ 43%，
> 任何 <10% 的单臂最小值差只报方向、不报量级；跨机绝对值不等（111 的 runtime 是 10.0.0）。

## §0 基线与归因结论

2026-10-20 在 david@192.168.3.111（Fedora 43，4 vCPU，OpenJDK 25.0.4 C2，.NET runtime 10.0.0）上，
cleat 65d407d（cleat-141）对 JVM Clojure 的 r7rs 56 项 × 3 轮取最小：**几何平均 1.61x**
（takl 按 JVM 快态 12.05 s 计为 1.64x）。三组归因实验（attr.py）：

1. **GC 配置**：array1 / gcbench / nboyer / fft / dynamic 在 server + gen0 256MB 下 -18 ~ -63%；
   peval / cpstak / browse / puzzle 完全不动 → 后者的 3 ~ 5x 差距是代码生成与调用/分配路径成本。
2. **JVM 关内联**（`-XX:-Inline`）：browse / scheme / peval / array1 掉到 cleat 的 ±20% 量级 →
   差距的主体是 **C2 把小访问器与包装调用内联掉**，cleat 的对应手段是跨编译单元内联（§2.3）。
3. **cleat 侧回退**：parsing +24%、mbrot +28%、pi / dynamic +15%、equal / deriv / cpstak +8 ~ 10%
   （相对 cleat-118，同机同 JVM），且对 GC 配置不敏感 → 疑似 M14/M15 入口选择与特化规则的副作用（§5.1）。

## §1 复核修正：哪些论据不成立

`shapes3.cljc` 在同一 ns 内做"接口直调 vs 经 defn 包装 vs RT 直调"的三方对照后，M15 优化建议里
两条量化论据被推翻：

1. **"nth 慢 22x、char 常量比较慢 11.7x、first / seq on String 慢 3 ~ 4x"不成立。**
   JVM 探针的结果是死值 / 循环不变量，C2 做了 LICM + 标量替换，把整个调用提出了循环；
   0.7 ns 不是操作的成本。cleat 侧的数字才是每次调用的真实成本。
   **方法论教训：探针必须让结果参与数据依赖（写进 sink / 条件分支），否则 JVM 侧数字不可用。**
2. **".NET 分配慢 9 ~ 12 倍"不成立。** cleat 建两字段 deftype 是 5.1 ~ 5.6 ns，与 micro.cljc 里
   JVM **真逃逸**分配（5.8 ns）同量级；12x 的差来自 JVM 把不逃逸的分配整个消掉（EA），
   不是分配路径慢。因此"标量替换 / 栈分配"不是 M16 的 P0，长期项另行 spike（§2.6）。
3. **给 `nth` / `first` 加 `:inline` 收益有限。** RT 直调与通用调用同价（16.9 vs 15.2 ns），
   成本在本体：`RT.nth` 对非 Indexed 走 `NthFromRef` → `NthFrom` 的类型判断链
   （ref 转发是 cleat-117 为绕开 tier-0 形参活性问题加的）。降级为 P1（§3.1）。

复核后新发现的量化事实：

- **defn 包装比接口直调贵约 2 ns**（cleat 3.4 vs 1.3；JVM 两侧都被内联为 0）。pair 密集项每轮
  几次到十几次访问器调用，累计是几十 % 的时间 → 访问器内联从 P1 提为 P0（§2.3）。
- **跨 ns 的 deftype 生成类在编译期决议不了**（实测：`(:import (r7rs.util MPair))`、`^IMPair` 提示、
  `(MPair. 1 2)` 全部 "Unable to resolve classname"，JVM 正常）。这是**兼容性缺口**，不只是性能问题；
  也是访问器内联与字段直读的前置（§2.3）。

## §2 P0：有真实基准证据的热路径

### 2.1 char 常量装箱（cleat-142）

- 证据：browse 的真实分配里 `System.Char` 占 28%（1.84 GB），来源是 `my-match` 里
  `(= (nth (name …) 0) \?)` / `\*` 的字符字面量；`(= \a \a)` 探针 8.2 ns。
- 现状：codegen.clj 的常量发射对 char 走 `ldc.i4 + box char-type`，**每次求值都装箱**；
  `CharBox` 只覆盖了 `RT.nth` 的返回路径。
- 方向：char 常量的装箱缓存（对齐 JVM `Char.valueOf` 的 0 ~ 255 缓存范围，或按 `CharBox` 现有协议扩展），
  常量发射点改为读缓存；`= ` / `==` 对已知 char 操作数发原始比较。

### 2.2 String 的 seq / first 直读（cleat-143）

- 证据：`(seq "abc")` cleat 侧绝对成本 25 ~ 27 ns，每次 `RT.seqFrom` 对 string 直接 `StringSeq.create`
  新建堆对象；受益 slatex / string / wc / read1 / nucleic 与 dynamic 的 scheme-read。
- 方向：给 CharSequence 加语义保持的直读快路径——`first` / `nth` 作用于 String 时不建 `StringSeq`
  直接取字符；`seq` 的 `StringSeq` 按（串, offset）不可变性复用或改索引式表示。
  语义红线：`seq` 返回对象的 identity / `=` 行为与 JVM Clojure 一致。

### 2.3 跨 ns deftype 决议 + 访问器跨编译单元内联（cleat-144 → 145）

- 证据：§1 的包装成本 2 ns 与 `-XX:-Inline` 对照；`register-eval-inlines!` 只在 eval 单元注册内联
  接口文件（driver.clj：`(:boot (st))` / `(:package (st))` 不启用），所以 r7rs 普通 ns 的访问器
  都不会被内联；`not` 的 `:inline` 曾让 tak 3.7 → 1.9 s，说明这层收益在 r7rs 尺度被验证过。
- 两步：
  1. **cleat-144（先做，正确性）**：跨编译单元的 deftype / definterface 生成类在编译期可决议
     （TypeResolver / 接口文件补生成类映射），修复 `:import` 与类型提示的兼容缺口。
  2. **cleat-145**：小访问器（deftype 字段读、mcar / mcdr 一类）在调用点内联，扩展到镜像 / 包构建；
     内联决定必须跨自举阶段稳定（不动点），并对照 `restrict-loop-inlines?`（cleat-120 的循环内联限制）
     避免重新引入 array1 回退。
     **落地范围（2026-10-21）**：只做 **eval 单元**与**包构建**两条路径（开关 `accinline`）；
     "扩展到镜像"这半段**明确不做** —— 镜像构建仍 `inline-calls? = false`，内联决定不进种子，
     否则 `BuildFromTheSeedReproducesTheSeed` 的逐字节一致口径会被打破（cleat-144 同口径）。
     `restrict-loop-inlines?` 的协调结论：访问器特化只发生在被内联的包装函数子句内部，不新增循环体内的
     用户函数内联，也没有为此新增豁免评估；array1 是否在开侧回退以 [notes M16 §8](../notes/r7rs-performance.md) 的
     同轮配对比值为准。直接 `.getA` 的调用点（无 defn 包装、不走 `.inl`）不在本任务，交 cleat-153。
     **口径（111 的 A/B 之后）：代码全落地但开关默认关（opt-in `CLEAT_OPT_ACCINLINE=1`）** —— 10 项 × 5 轮的
     同轮配对里开侧整体只是中性（中位几何平均 x1.002）、没有任何一项达到可声明的收益，browse 反而 5/5 同向
     慢 5.8%；机制对得上：这批基准的热访问器多是**直接 `.getA` 裸调用点**，本任务只在"包装函数被内联"时生效，
     碰不到那一整批，生效的点只多付一次守卫分支。**没有收益就不进默认路径**，等 cleat-153 把裸调用点接上再重开。

### 2.4 keyword / symbol intern 与 `(str <long>)`（cleat-146）

- 证据：`(keyword (str 7))` cleat 侧 52.5 ns；探针里 `clojure.lang.Symbol` 占分配 42%；
  browse 的 `generate-symbol` 是真实热路径。**按绝对成本 + 分配量论证，不按倍数**
  （JVM 的 intern 也先建 Symbol）。
- 方向：intern 查找按字符串比较先行、命中不新建对象；`(str <long>)` 走整数→串专用路径，
  避免中间 String 拼接与重复哈希。

### 2.5 复核后仍不做默认值的

- GC 旋钮维持 [22 §6](22-gc-and-performance-followups.md) 的结论：只做可声明 profile（§4），不改默认。
- `nth` / `first` 的 `:inline` 元数据：§1.3 说明收益在调用点之外，不单独立项。

### 2.6 长期项（不在 M16 范围）

- 不逃逸对象的标量提升 / 栈分配（.NET 无 C2 的 EA）：cleat-123 的有限标量替换已覆盖局部形状，
  全面方案需要单独可行性 spike，不进本轮任务。

## §3 P1：调用与对象路径（cleat-147）

1. **`RT.nth` 非 Indexed 路径**：`NthFromRef` → `NthFrom` 的类型判断链与 cleat-117 的 ref 转发；
   在不重新引入 tier-0 活性问题的前提下缩短链条（如对已知 seq 类型直接分派）。
2. **每调用重建的闭包提升**：peval 的 `ref_count$rc$fn__20` 占该项分配 17%（1.02 GB）；
   捕获变量全为静态 / 未被改写时把闭包提升为单例（cleat-105 量化后未实现，此处有真实分配证据）。
3. **变长调用的参数数组**：alloc.cs 里 `System.Object[]` 在 peval 占 4.0%、browse 占 1.8%，
   先量化其中多少来自 RestFn / invoke 的参数数组，再决定复用或直发已知元数。

## §4 P2：GC profile 与 LOH（cleat-148）

- server GC + gen0 256MB 对 array1 -63%、gcbench / nboyer / fft / dynamic -18 ~ -27%，
  但 deriv / cpstak 有回归（22 §0.2）→ 做成**可声明的批处理 profile**
  （`cleat.edn` 的 `{:gc …}` 或 `--gc-profile batch` / env 旋钮），基准脚本统一用它；不改默认值。
- LOH 大数组初始化写入评估无屏障路径（`spans` / 直接指针替代常规 `stelem.ref`）：
  array1 的 `Object[]` 占其分配 40%。
- 测量环境：192.168.3.111 只有 .NET runtime 10.0.0（本地 10.0.12），跨轮比较前先对齐版本；
  111 离线后统一在本机 10.0.12 上测，与 111 的历史数字只比方向不比绝对值。

## §5 P3：回归排查与测量口径（cleat-149）

1. **回归**：parsing / mbrot / pi（+24% / +28% / +15%，对 GC 不敏感）做 `CLEAT_NO_OPT` 开关矩阵
   （sigspec / typed / loopinl / …）+ `git bisect 6ed0da4..65d407d`；重点怀疑 cleat-138 的
   RuntimeSigTable 兼容条目 / 负表 / LRU 与入口选择规则。
2. **口径**：双态项（takl：JVM 快态 ~12 s / 慢态 34 ~ 37 s）报告区间而不是 min-of-3；
   `micro.cljc` / `shapes2.cljc` / `shapes3.cljc` 对照纳入每次里程碑收尾；
   "探针结果必须参与数据依赖"写进 run.py / attr.py 与探针文件的注释。

## §6 语义与验证纪律

沿用 22 §0.1：每个优化有 `CLEAT_NO_OPT=...` 开关或改前构建对照；语义逐字不变；不为基准改语义。

**142 / 143 / 146 验证后新增的纪律**（M16 §2 的实际教训）：在 `RT.first` / `next` / `more` / `seq`
这类**多类型判断链**里加快的路径分支时，分支**位置对其它类型是纯成本**——string 分支放在 `Cons` /
`ASeq` 之前让 lattice 这类纯列表负载回退 6.3%，而受益项（wc / browse / read1）与位置无关。
所以：快路径分支一律排在既有热分支之后；A/B 名单必须同时含受益项与**不受利的列表 / 序列密集项**；
差异 < 3% 记为噪声，回退用"只关一个开关"的隔离定位（本轮就是靠它指认到 strseq）。
每项验证固定为：Release 构建 0 warning、语料门禁（gate 521 分类）、CTS 与 test-clojure 基线、
Core / Host 全量测试、改 `src/clojure` 时种子不动点（stage2 == stage3）、
受影响 r7rs 项的同窗口 A/B；里程碑收尾跑全量 56 项 × 3（cleat-150）。

**145 验证后新增的纪律**（M16 §8 的实际教训）：
1. **编译期"顺手多载入一个程序集"是行为改变，不是零成本。** `inline-target` 里无条件调用
   `PackageIndex/PrepareNamespaceInterface` 让 eval / 加载期也按需载入依赖程序集，接口文件一登记，
   本单元就直接链接依赖包的函数，第一次用到就跑那个包入口开头的镜像核对（`BootInterface.Check`），
   主机镜像与包内镜像不是同一份时抛 StaleModuleException，打断一条与内联完全无关的加载。
   判据收窄成"正在编包才问"（驱动线程局部状态里绑着 `:package`）。所以：新增的编译期按需载入一律按
   **构建阶段门控**，并且跑一遍 neorepl / tools.build 那类**跨进程热替换**用例。
2. **Bootstrap / NuGet 那批用例是筛法敏感的 flaky。** 同一个 main 构建，跑整个夹具时 4 条只输 Sqlite 那条，
   换 `--filter` 只挑其中两条时 **main 也失败**（dotnet 的本地工具解析缓存按 包+版本 记安装路径，
   cleat-078 已记过同一个坑）。判"我改坏了没有"必须**用同一种筛法做 main 对照**，不能只看一次结果。
   补一条同类的：改了 `src/clojure` 重生成种子后 **boot image id 换代**，`/tmp/cleat-nuget-*` 那类
   旧缓存里留着上一代镜像编出的包程序集，会把 Bootstrap 用例跑成 StaleModuleException ——
   跑全套之前先把这些缓存目录移走，别把环境问题记成产品回退。
3. **特化 / 内联时不要复用通用宿主调用发射器。** 145 的读侧最初直接发通用的 `GetInstanceField`，
   而通用发射器要为 object 接收者补 `ldstr + ClojureStaticCalls.Instance`（= NotNil 检查）、
   结果为 `System.Object` 时还要再过一道 `RetObject`；守卫成立时接收者必然非 nil，这两笔纯属白付，
   合起来**比原来的接口调用更贵**（browse x1.538 / destruc x1.215 的可声明回退就是这么来的）。
   改成专用节点 `GetInstanceFieldDirect`（`castclass` + `ldfld`，不发 NotNil / RetObject）后回到地板内。
   纪律：新增的守卫内特化一律**自带发射器**，通用发射器的空值检查与返回值协议是**默认行为**的税，
   要省就显式加参数（本轮是 `cg-static-host-call!` 的第 4 参 `guarded?`，默认 `false` 保持原行为）；
   并且 A/B 的取证要落到 **IL 序列**，只看输出相同看不出这类纯成本回退。

**151 验证后改写的判定门槛（2026-10-22）**："`< 3%` 记为噪声"这条口头规则作废，换成**按项门槛**
（唯一出处是 `bench/r7rs/floor.py` 生成的 `results/floor-<host>.json`，`m16sum.py` 用 `R7_FLOOR` 读，
没有门槛表时回落旧行为）。理由：151 的本机地板标定实测"同一构建同一项连跑 9 轮的轮内 max/min"从
pi 的 5% 到 takl 的 46% 不等（wc 1.416 / browse 1.244 / lattice 1.143 / slatex 1.186 / peval 1.205 /
mbrot 1.107 / parsing 1.300 / array1 1.299 / takl 1.456），单一的 3% 门槛在 half 的项上是假的"可判定"。
现在每项有 `min_declared_ratio_delta`（最小可声明量级）与 `min_same_direction`（最少同向轮数），
低于门槛的配对格一律输出"不可判定"；检出 5% / 10% 差异各需要多少同轮配对由符号检验近似给出
（floor.py 的 stdout 表与门槛 json 的 `recommendations`），如本机 wc 需要 156 / 42 对、
takl 175 / 44 对——**本机 5 轮配对的表只能声明个位百分比以上的差异**，这是 149 §2 那句话的成表版。
双态项（takl 等）照样按区间报告。跑分优先在 111（地板 < 3%，R7_PAIR_MAXLOAD=1.5 口径不变）。

## §7 任务拆分与依赖

| 任务 | 内容 | 依赖 | 设计 |
---|---|---|---|
| cleat-142 | char 常量装箱缓存 | 无 | §2.1 |
| cleat-143 | String 的 seq / first 直读快路径 | 无 | §2.2 |
| cleat-144 | 跨 ns deftype / definterface 编译期决议（兼容性修复） | 无 | §2.3 |
| cleat-145 | 小访问器跨编译单元内联（含镜像 / 包构建） | cleat-144 | §2.3 |
| cleat-146 | keyword / symbol intern 与 `(str <long>)` 快路径 | 无 | §2.4 |
| cleat-147 | `RT.nth` 非 Indexed 路径、闭包提升、变长参数数组 | 无（建议排在 142 ~ 146 后） | §3；第 2 / 3 条已完成"量化 + 决策"（闭包上限 ~17 ns/iter、变长参数数组 +57~63 ns/iter，均记量化上限不进 M17 实现，notes M17 §3） |
| cleat-148 | GC profile 可声明化与 LOH 初始化评估 | 无 | §4 |
| cleat-149 | parsing / mbrot / pi 回归排查与测量口径修正 | 无（宜早做，避免污染后续 A/B） | §5 |
| cleat-150 | M16 收口：r7rs 56 项全量重跑与三方对照 | cleat-142 ~ 149 | §6 |

关键路径：149（先还债、稳口径）→ 142 / 143 / 144 / 146 / 148 并列 → 145 → 147 → 150。

**cleat-150 已收口（2026-10-22）**：对 JVM x1.594（M15 1.61x → 可声明改善）、开关贡献整体 x0.981；
array1 的 +2.9% 归因到 intern、lattice 的 +2.5% 归因到 strseq / crossnstype，均记账保留默认开。
全表与分配画像见 [notes M16 收口节](../notes/r7rs-performance.md)。M17（§7.1）成为现行。

### 7.1 遗到 M17 的三项（2026-10-21 登记，任务书见 tasks/cleat-151 ~ 153）

| 任务 | 内容 | 依赖 | 出处 |
|---|---|---|---|
| cleat-151 | 本机噪声地板标定（把"< 3% 记为噪声"换成分项门槛）+ 56 项三臂全量重跑 | cleat-150 | §6、notes M16 §5 ~ §6 |
| cleat-152 | M14 / M15 回退（mbrot x0.904 / pi x0.937 / dynamic x0.829）的 commit 级 bisect 与机制归因 | cleat-151 | §5、cleat-149 §1 / §5。**已完成（2026-10-22）**：机制定位到 cleat-133 sigspec（同构建单开关 dynamic x1.144 5/5、pi x1.126 5/5）；旧 first bad=13b55d8 但新 10% 逐格门槛补跑边界为 undecidable，照实记账；保留默认开（notes M17 §5） |
| cleat-153 | 接口调用点的运行时类型反馈（145 之后剩下的那 1.3 ~ 4.4 ns/次，含直接 `.getA` 一类的裸调用点） | cleat-145（**已落地 2026-10-21，依赖已就位；守卫形态可复用**） | §2.3、§3、notes M15 归因 §1。**已完成（2026-10-22）：量化 + 记账收口，单态上限 ~1.6 ns/次、双态代价同量级，反馈计数 / 查表后预期净收益在零附近，不落默认实现（notes M17 §4）** |

另外两项仍挂在原任务书里，M17 一并处理：**147 的第 2 / 3 条**（每调用重建的闭包提升、变长参数数组，§3.2 / §3.3，
按"量不出就不改"的纪律先用同 n 相减或 `GC.GetAllocatedBytesForCurrentThread` 重做量化）与
**149 §3 记下的退化路径健壮性缺陷**（`CLEAT_NO_OPT=host` 让 mbrot 从 0.7 s 变 147 ~ 158 s、`infer` 14x：
单开关关闭的成本非线性，说明退路本身没有被维护过）。

还有一处口径欠账（2026-10-21 登记，留给下一个动 `src/clojure` 的任务顺手做，见 cleat-153 第 6 条）：
`driver.clj` 的 `register-eval-accessors!` docstring 仍写着"CLEAT_NO_OPT=accinline 时保持空表"，
而 145 收口时把 `accinline` 改成了**默认关的 opt-in**（要 `CLEAT_OPT_ACCINLINE=1`）。
docstring 是编译进种子的字符串，单独改它也要重走一次不动点 + 全套验证 —— 为一个注释开一次不值得，合并做。

### 7.2 M18：把量化结论落成实现（2026-10-22 登记，任务书见 tasks/cleat-154 ~ 157）

| 任务 | 内容 | 依赖 | 出处 / 裁决口径 |
|---|---|---|---|
| cleat-154 | 变长调用已知元数直发；合并 `driver.clj` 的 accinline docstring 修正 | 无，M18 首项 | 147 量化：rest0 / rest2 相对 fixed +63.293 / +57.380 ns/iter、+120 / +112 B/iter。**已完成（2026-10-23）：111 A/B 不达标，src 已回退（cleat-154 任务书收口节）——恰好必需元数的 RestFn 入口本就是小 int switch 零分配，直达入口期望净收益趋零（探针 req0 x1.026 / req2 x1.015，真实负载五项全不可判定）；成本只在越过必需元数的数组路径，动它须整体改 RestFn 协议（小数组复用已被口径否决），M18 内不重开；docstring 欠账重新挂回 §7.1** |
| cleat-155 | 无捕获 / 静态捕获闭包提升；`volatile!`、循环变量等可变捕获不提升 | 无；建议排在 154 后 | 147 量化：无捕获 +17.290 ns/24 B，静态捕获再 +12.613 ns/+32 B，volatile! 再 +16.936 ns/+56 B。**已完成（2026-10-23）：语义否决并回退——同一 fn 形式每次求值必须新对象，conformance 的 identity / set / map / distinct / loop 五条把单例守卫直接挡下；24 ~ 32 B/iter 是身份语义的必需分配，M18 不重开** |
| cleat-156 | sigspec 对 dynamic / pi 的候选、安装、key 与逐出规则调优，保住全局收益 | cleat-155 | 152 归因：同构建只关 sigspec 时 dynamic x1.144（5/5）、pi x1.126（5/5）；全局收益仍保留。**已完成（2026-10-23）**：定位 `CLEAT_RTSPEC_ALL` 判断实际常开、纯数值 R-fail 落 O 只留 miss；修正热区判断 + 建表前拒绝该形状。本机 5 轮 dynamic x0.863、pi x0.964、parsing x0.799、mbrot x0.923；本机 56 项 1 轮预检 x0.984、无 >10% 回退；全套门禁过。111 离线，经用户确认改用本机性能口径收口。**不新增开关** |
| cleat-157 | 接口调用点类型反馈原型：计数后净值先过淘汰条件，再谈安装与失效 | cleat-156 | 153 量化：单态上限 ~1.6 ns，双态同量级代价。**已完成（2026-10-23）：本机 5 轮淘汰——单态上限仅 1.3 ns/次，调用点状态递增 +5.5 ns/次，单态守卫 + 计数 +4.6 ns/次，双态守卫 + 计数 +5.6 ns/次；145 理想直读臂 5/5 为负，且任意接收者无法直接复用 RuntimeSigKey。停手记账，不落实现、不新增开关（notes M18 §1）** |

M18 的开关纪律：**已经量化有用的实现不再新建默认关 / opt-in 开关**，直接全面启用；
性能 A/B 用改前 / 改后构建同轮配对。既有开关是已完成优化的测量与归因仪器，本批不物理删除，
否则会破坏 56 项 on/off 与单开关归因的可复现性。
