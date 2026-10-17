# 22 GC 与性能后续：M11 之后的重构方向

> 状态：**提案**（2026-10-16）。来源：[r7rs-performance](../notes/r7rs-performance.md)、
> [array1](../notes/array1.md)、[escape-continuations-and-try](../notes/escape-continuations-and-try.md)
> [GC 配置两机实测](../notes/gc-config-and-tier0-two-machines.md) 与 cleat-107 ~ 117 的剖析。
> 本文处理 M11 收口后仍剩下的分配、GC 存活、异常与宿主差异；不承诺全部实现。
>
> **对照基准的 JVM**：cleat-107 ~ 117 在 linux01 上的"JVM"列是 **GraalVM CE 25.0.2**（顶层 JIT 是 Graal 编译器，带部分逃逸分析），
> 不是 HotSpot C2；本机非登录 shell 的 `java` 同为 GraalVM，本机登录 shell（Debian）与 192.168.3.111（Red Hat）是 OpenJDK 25.0.4（C2）。
> 分配密集项（mbrotZ、array1 一类）两种 JIT 可能差很多：此后的对比表必须注明 JVM，C2 作为普遍对照、GraalVM 作为上限。

## §0 已确认的事实

cleat-117 中途时，r7rs 56 项对 JVM Clojure 的几何平均已从 4.52x 降到 2.27x。剩余慢项可分成五类：

| 类别 | 典型项 | 直接原因 | 本文方向 |
|---|---|---|---|
| GC 存活数据与分代成本 | array1、gcbench、nboyer | 大数组引用年轻代装箱对象；tier-0 多根引用一轮；多次晋升与卡表扫描 | §1 |
| 通用对象 / 数值调用 | browse、cpstak、peval、destruc | `IFn.invoke`、cast、装箱、Numbers 分派、跨函数小调用 | §2 |
| 异常模拟逃逸续延 | ctak、fibc | CLR `throw/catch` 总是走完整异常机制 | §3 |
| 非逃逸对象分配 | mbrotZ | GraalVM 的部分逃逸分析消掉每步复数对象（C2 待实测），.NET 没有对应优化 | §4 |
| 宿主表示差异 | string | .NET UTF-16 字符串搬运量约为 JVM Latin-1 compact string 的两倍 | §5 |

其他已见热点：Linux 分配快速路径 `RhpNewFast` 经 `__tls_get_addr` 取线程分配上下文；大方法耗尽 JIT 内联预算；
`array1` 中 `JIT_ByRefWriteBarrier` 约 3.7%。这些先作为观察指标，不单独作为第一轮改动目标。

## §0.1 原则

1. **先减少分配与根引用，再调 GC**。实验已证明 Server GC、扩大 gen0 等只对单项有利，对全套有涨有跌，不作为默认配置。
2. **语义必须逐字不变**。每个优化有 `CLEAT_NO_OPT=...` 开关或改前构建对照；分配采样、GC 计数与 r7rs 数字一起看。
3. **不为基准改语义**。`array1` 的对象数组必须继续引用装箱 long；把它改成 primitive array 是另一个基准，不是当前问题的修复。
4. **优先修可泛化机制**：tier-0 生命周期、类型化调用、非逃逸对象降低；字符串与异常只在剖析确认后进入实现。
5. **验证固定**：种子不动点（改 `src/clojure` 时）、语料门禁、两套基线、Host / Core 全量、`bench/` 与受影响 r7rs 项。

## §0.2 实测更新（2026-10-16，两台机器）

linux01 已不可用，改用本地 8 线程 i9-13900H 与 david@192.168.3.111（4 vCPU）实测（数据见
[notes](../notes/gc-config-and-tier0-two-machines.md)，脚本 bench/r7rs/gc_bench.py）：

| 结论 | 数据 | 对本文的影响 |
|---|---|---|
| server + gen0 256MB 让 array1 两机一致 -65 ~ -68%，geomean 约 -2% | 5 项 × 4 配置矩阵 | §6"不做默认值"维持；只作为批处理配置（env 旋钮）记录 |
| deriv / cpstak 的 server GC 回归随核数放大（4 核 +7~15%，8 线程 +80~90%） | 同上 | 默认值风险确认；多核机器是尾部风险 |
| JVM 堆砍到 512MB，array1 不变、GC 次数 ×4.6 不掉速 | JVM 参数矩阵 | "JVM 靠堆大"假设关闭；优势是单次回收成本 |
| 内联 / tier-0 开关在 r7rs 尺度对 array1 只有 0 ~ 11%，server+gen0 下归零 | array1 开关对照 | D1 / D2 降级：cleat-120 先复测确认再诊断，不作为 array1 的主要杠杆 |

## §1 tier-0 生命周期与 OSR

### D1：`array1/go` 停在 tier-0（cleat-120 已确认并保守处理）

> 先在 192.168.3.111 上复测：回退不显著就只记录结论、关闭 cleat-120；显著才按下面的步骤排查。

`cleat-116` 把 `my-try` 内联进 `go` 后，`go` 只调用一次、循环 500 次，始终停在插桩 tier-0；JIT 调用实参的临时变量把上一轮两个
百万元素数组连同其中装箱 long 多抓一轮。GC 次数变少，但每次回收更贵。

必须先做诊断，而不是继续猜测：

1. 从 `TMP_DUMP_EVAL` / Lcil 转储抽出最小方法，保留 loop、调用、异常表与 local slot 形态；
2. 用 `DOTNET_TieredCompilation`、OSR 阈值、`DOTNET_JitDisasm` / JIT 日志确认 OSR 未触发的原因；
3. 逐项排除已知限制：显式 tail call、localloc、补丁点求值栈不空、异常区域、方法大小、循环结构、参数 slot 使用；
4. 向上追溯到 .NET JIT 的 OSR 资格判断；若为运行时限制，记录版本与最小复现。

复测确认回退显著。原因不是 OSR 本身不可用：不内联 `my-try` 后 `create_x` / `create_y` 到达 `Tier1-OSR`；内联却让外层
`go` 的实参临时变量把上一轮大数组多抓一轮。处理采用不内联循环体里的非 core 用户函数（子句仍含真实调用时），而不是让
`go` 直接优化编译；详见任务书 cleat-120。

### D2：保守分层规则（暂缓）

原稿的条件（"会被反复调用""循环可靠触发 OSR""被调用者足够小"）都无法静态判断。只有 D1 复测确认回退显著时才做，
并且只用可判断的规则，例如"循环体里有未内联的用户函数调用、函数本身不自递归时不分层"，再用 r7rs 全量确认没有回退。

### D3：局部变量清理只在 tier-0 生效（cleat-117 已实现）

`Locals.Take` / `ldloca` 可以缩短 tier-0 报告的活跃区间，但会暴露局部变量地址，可能阻止寄存器分配，并在大方法里耗尽 JIT 内联预算。
因此：

- tiered 方法：最后一次读取 object local / 参数可用 by-ref take；
- 优化方法：普通 `ldloc` 后置 null；
- 转发方法与桥方法不标 tiered；
- `^:once` 闭包字段在最后一次使用后清空；
- 循环实参读入后，清掉循环体不再使用的 object 形参；
- 序列遍历 runtime 辅助用 `ref` 传参并在慢路径入口取出，避免调用方整个遍历期间抓着序列头。

这项只减少“多活一轮”，不减少实际分配数，不能单独作为 array1 的完整解法。

### D4：镜像与包分层（cleat-117 已实现）

eval 已使用不可回收 ALC 与 `PersistedAssemblyBuilder` 获得分层编译；镜像 / 包可在字节稳定的前提下使用同一规则：

- codegen 给需要 PGO 的函数类方法加 `tiered`；
- 转发 / 桥方法保持 `AggressiveOptimization`；
- 入口方法不分层；
- 种子镜像 id 变化必须重生成并达到不动点。

预期收益以 cleat-117 实验为准（约 3% 与启动时间），不是解决 2x 差距的主杠杆；若与 tier-0 生命周期规则冲突，以正确性优先。

## §2 类型化调用与分配减少

### D5：类型化调用点必须保持原始值

只把 `IFn.invoke` 换成 `invokeStaticTyped` 主要减少调用开销，不一定减少 GC。要降低分配，实参和结果必须保持原始表示：

1. 调用点实参类型已知时直接传 `long` / `double`；
2. 返回值用 `Num` 或原始局部承接，不立即装箱；
3. `if` / `let` / loop 回边汇合继续保存原始类型；
4. 与 known-call、typed-entry、loop speculation、Num 双表示共用一套类型信息，不另建平行推断；
5. Var 重定义时保留通用入口回退，类型化入口只作为快路径。

候选：非递归纯数值函数、`let` 绑定且不被赋值的已知 lambda、同一编译单元内的直接调用。验收关注 browse、destruc、lattice、puzzle、takl，
并要求分配采样中对应装箱比例下降；若只有耗时下降、分配不变，记录为调用开销优化，不得归为 GC 优化。

### D6：运行时快路径保持“快路径可内联、慢路径不内联”

继续沿用 cleat-116 的形态：

- 精确类型检查放在小而可内联的方法里；
- 遍历、反射、慢分派放到 `AggressiveOptimization` 或不内联的辅助方法；
- 避免快路径分配实参数组、缓存 key、委托或包装对象；
- 大方法拆冷路径，防止 JIT 内联预算耗尽；
- double 装箱继续经不内联的 `LongBox.Double`，避免分配调用让循环 double 落到栈槽。

### D7：装箱缓存不盲目扩大

现有 LongBox 缓存只覆盖小整数。扩大缓存能减少分配，但增加启动驻留、数组扫描与缓存实现的复杂度；必须先用分配采样证明目标范围，
并与其他基准一起评估。默认不把“扩大缓存”当作独立优化任务。

## §3 逃逸路径

### D8：本方法内已知 `throw` → `leave`

当 throw 点与匹配 catch 在同一个方法、编译器能证明异常对象与判断条件、且中间没有可截获 handler 时，把该路径编成普通
CIL `leave`：

```cil
.try {
  ...
  result = value
  leave ESCAPED
}
catch ExceptionInfo e {
  // 保留：处理其他来源的异常
  ...
}
ESCAPED:
  ...
```

这不是删除 `try/catch`，也不是让异常仍走 catch；它把“必然被本层接住的异常”替换为等价控制流。其他异常继续走原 handler。
`leave` 离开受保护区域时照常执行 finally。

成立条件：

- throw 与 catch 同方法；
- 异常对象是已知对象；
- catch 的判断对本层恒真；
- 不穿过可能截获它的嵌套 try / filter / finally；
- catch 不观察异常对象状态、不打印、不 rethrow，只取回结果；
- 跳转目标符合 CIL 异常区域约束。

实现前先统计编译器中该模式的出现频率。它对 `fibc` / `ctak` 没有直接帮助，因为 continuation 被传到其他栈帧。
Clojure 核心库的提前退出都不用异常（`reduced` 是包装值、由 reduce 检查；`some` 等是循环），预计频率接近零——统计后很可能直接关闭。

### D9：非本地逃逸协议（高风险候选）

若要显著改善 `fibc` / `ctak`，需要 cooperative escape：

- 逃逸时返回特殊 marker，或使用线程局部 escape state；
- 可能被穿过的调用点在返回后检查 marker，并继续向外返回；
- 编译器标注“可能逃逸调用”，避免给所有调用加检查；
- 异步、动态绑定与多线程语义需要显式设计；
- r7rs 的 `call-with-escape` 是基准移植自带的工具函数，编译器无从知道它的 `k` 是逃逸续延：协议必须对应语言层的构造
  （例如 cleat 提供的内建 `call-with-escape` 形式），不能为识别基准里的写法而特判（§0.1 第 3 条）。

这不是 M11 后立即开工项。只有在 `throw/catch` 仍占主要时间、且本方法内 `leave` 无法覆盖时，才先做最小语义原型。

## §4 有限标量替换

### D10：非逃逸 primitive record/deftype 降低

针对 `mbrotZ` 类“每循环构造小对象、只读字段、对象本身不逃逸”的代码，实现受限标量替换：

1. 候选对象是局部创建的不可变 record / deftype；
2. 字段全部为 primitive 或可拆箱值；
3. 不作为对象传给未知调用、不存入集合、不被闭包捕获、不跨异常边界保留对象身份；
4. 后续使用只有字段读取、等值判断或构造另一个候选；
5. 逃逸时能在同一方法内物化对象，且语义保持不变。

编译器把候选 lowering 成局部变量，字段读取变成 `ldloc`，字段写只存在于物化前的局部构造过程。若不能证明，保持原生成代码。
第一版不做完整逃逸分析、不改对象表示、不处理数组元素。

mbrotZ 的前提：Complex 会传进 `c+` / `c*`（违反"不传给未知调用"），而 `c*` 因体积没有被 cleat-116 内联（放宽内联上限会耗尽 JIT 预算）；
`z` 还是 recur 的循环变量。所以本项要么先解决小 deftype 操作函数的内联，要么把"循环携带的候选对象拆成原始循环变量"纳入范围。
另需评估 .NET 10 JIT 自身的对象栈上分配：调用全部内联后它可能自动生效。

**实测（2026-10-16，C# 复现 mbrotZ 的循环）**：.NET 10 的 JIT 连只在一次迭代内使用的临时对象也没有栈上分配（2M 次调用分配 12 GB），
指望不上，标量替换必须由 cleat 做。每次 24 字节的分配约 5 ~ 6 ns（JVM 的 TLAB 约 1 ~ 2 ns），cpstak 里 `RhpNewFast` + `__tls_get_addr` 约 28%。

**精确保持对象同一性的做法**（内联的 `c+` / `c*` 带守卫，守卫失败的分支把对象交给未知函数，必须能"物化"）：
1. 循环变量 z（deftype T，字段不可变）拆成原始局部 `z.re`、`z.im` 与对象槽 `zobj`；进入时 `(isinst T z0)` 不成立就走原来的循环（推测的模式）；
   成立时字段从 z0 读出，`zobj = z0`（原来的对象）。
2. body 里 `(.-re z)` 读原始局部；z 被交给未知调用（守卫失败的分支）时：`zobj` 为空才 `zobj = new T(z.re, z.im)`，用 `zobj`——
   原程序里 z 在一轮内是同一个对象，这里也是；对象的哈希、`identical?` 都与原程序相同，只是分配推迟到真正需要时（deftype 的构造器没有副作用）。
3. recur 的新值是 `(new T a b)`：新的 `z.re` / `z.im` 取 a / b，`zobj` 置空（原程序每轮也是新对象）；新值来自守卫失败的分支（未知对象）时，
   带着本轮已算好的全部 recur 实参跳回原来的循环——在 recur 处切换是精确的，本轮的副作用都已发生。
4. let 绑定的临时对象（`(c* z z)` 的结果）同理：值在快路径是构造、在慢路径是未知对象时，第一版不拆（只拆循环变量已省一半分配）。

验收：mbrotZ 分配采样中复数对象分配显著下降；构造、字段读取、`=`、异常路径开 / 关输出相同。

## §5 字符串与宿主差异

### D11：局部减少 UTF-16 搬运

.NET `string` 是 UTF-16，无法直接得到 JVM Latin-1 compact string 的内存与搬运优势。短期只做局部优化：

- `str` 多参数时先算总长度、一次分配（string 基准的热点是 `(str "123" s "456" s "789")` 与 `subs`），不经 StringBuilder 反复扩容；
- 预分配 `StringBuilder` capacity；
- 用 `Span<char>` / `String.Create` 替代中间字符串；
- 减少重复 `Substring`、拼接与大小写转换中的临时字符串；
- 只在 `string` 基准剖析确认的具体热点上改 core 函数，不做泛化重写。

引入内部 Latin-1 字符串表示会影响 interop、identity、打印、正则与 Clojure 语义，暂不采纳。

## §6 不做或延后

| 方案 | 结论 |
|---|---|
| 默认 Server GC / 扩大 gen0 / 关并发 GC | 不做默认值；只保留为诊断配置 |
| 把 array1 改成 primitive array | 语义不同，不做 |
| 盲目扩大 LongBox 缓存 | 先量化，不做独立优化 |
| 完整逃逸分析 | 过大，先用 D10 的受限标量替换 |
| 编译器绕过数组写屏障 | 风险高、语义依赖 GC；待 OSR 与生命周期修复后重新评估 |
| 替换 .NET string 表示 | 影响 interop 与语义，暂不做 |
| 给所有调用加逃逸检查 | 会拖慢普通代码，只考虑标注后的 cooperative escape |

## §7 度量与验收

每个方向先做最小复现，再改实现。固定报告：

- 两台可用机器（本地 8 线程 i9-13900H、192.168.3.111 4 vCPU）的 r7rs 受影响项，3 轮 min；
- `bench/` 相关项；
- `dotnet-counters` 或现有工具的 allocation rate、gen0 / gen1 / gen2、LOH 字节数；
- `perf` / `dotnet-trace` 中的 GC mark / plan / relocate、`RhpNewFast`、write barrier；
- Lcil / IL 快照；
- 语义开关打开与关闭的输出逐字相同。

关键项：

| 项 | 主要观察 |
|---|---|
| array1 | OSR、tier 状态、存活数组轮数、gen0/gen2 次数、LOH 扫描 |
| gcbench | 分配率与各代回收 |
| nboyer | GC 占比与装箱分配 |
| browse / cpstak / peval / destruc | 调用分派、装箱、内联预算 |
| mbrotZ | 复数对象分配与标量替换 |
| string | UTF-16 搬运与临时字符串 |
| fibc / ctak | throw/catch 占比；只有非本地逃逸协议才预期显著改善 |

## §8 顺序

1. **cleat-117 收口与验证**：镜像 / 包分层与生命周期修复（M11）。
2. **cleat-118 类型化调用点的原始值贯通** → **cleat-119 M11 重测**（192.168.3.111，注明 JVM，C2 与 GraalVM 都测）。
3. M12 各项按真实依赖并列，除注明外只依赖 119：
   - **cleat-122 throw → leave**：只需一次统计，最先做，预计关闭；
   - **cleat-120 OSR 复测**：先确认回退是否显著；
   - **cleat-121 运行时快路径与内联预算**；
   - **cleat-123 有限标量替换**（先解决小 deftype 操作函数的内联）；
   - **cleat-124 字符串局部优化**；
   - **cleat-125 非本地逃逸协议**：依赖 122 的结论，且需要语言层构造。
4. **cleat-126 全量重测与收口**（依赖 120 ~ 125）。

每步完成后重测受影响基准；出现单项收益但整体回退时，保留诊断结论，不默认启用。
