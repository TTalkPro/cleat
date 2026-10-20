# 28 热路径优化：M15 归因复核后的修正优先级

> 状态：**进行中**（2026-10-20，M16）——§2.1 / §2.2 / §2.4 已实现并在 192.168.3.111 上完成同窗口开 / 关验证
> （[notes 的 M16 一节](../notes/r7rs-performance.md)：wc -39%、browse -4%、read1 -2.5%；lattice 因 strseq
> 分支位置回退 +6.3%，已按 §6 的教训修正）。§2.3 / §3 / §4 / §5 未开始。来源：[notes/r7rs-performance.md](../notes/r7rs-performance.md) 的
> "M15 差异归因"与"M15 优化建议 §5 复核修正"、探针 `bench/r7rs/src/{micro,shapes2,shapes3}.cljc`、
> 归因脚本 `bench/r7rs/attr.py`。承接 [21](21-r7rs-gaps.md)、[22](22-gc-and-performance-followups.md)
> 与 [27](27-jit-style-runtime-specialization.md)；任务 cleat-142 ~ 150。
>
> 本文的第一职责是**修正 M15 优化建议里被微探针伪影污染的论据**（§1），然后给出复核后仍然成立、
> 且每条都有真实基准分配画像或开关对照支撑的优先级（§2 ~ §5）。

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
- 测量环境：192.168.3.111 只有 .NET runtime 10.0.0（本地 10.0.12），跨轮比较前先对齐版本。

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

## §7 任务拆分与依赖

| 任务 | 内容 | 依赖 | 设计 |
---|---|---|---|
| cleat-142 | char 常量装箱缓存 | 无 | §2.1 |
| cleat-143 | String 的 seq / first 直读快路径 | 无 | §2.2 |
| cleat-144 | 跨 ns deftype / definterface 编译期决议（兼容性修复） | 无 | §2.3 |
| cleat-145 | 小访问器跨编译单元内联（含镜像 / 包构建） | cleat-144 | §2.3 |
| cleat-146 | keyword / symbol intern 与 `(str <long>)` 快路径 | 无 | §2.4 |
| cleat-147 | `RT.nth` 非 Indexed 路径、闭包提升、变长参数数组 | 无（建议排在 142 ~ 146 后） | §3 |
| cleat-148 | GC profile 可声明化与 LOH 初始化评估 | 无 | §4 |
| cleat-149 | parsing / mbrot / pi 回归排查与测量口径修正 | 无（宜早做，避免污染后续 A/B） | §5 |
| cleat-150 | M16 收口：r7rs 56 项全量重跑与三方对照 | cleat-142 ~ 149 | §6 |

关键路径：149（先还债、稳口径）→ 142 / 143 / 144 / 146 / 148 并列 → 145 → 147 → 150。
