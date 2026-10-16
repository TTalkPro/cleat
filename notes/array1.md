# array1：GC 与 tier-0 的 JIT 临时变量（2026-10-16）

> r7rs 的 array1 是 M11 里唯一比开始时更慢的一项：JVM 1.26 s，cleat 107 时 16.98 s（13.5 倍），114 时 14.33 s，
> 116（eval 小函数内联）后 19.79 s，117 中途 18.93 s（15.0 倍）。linux01，cleat 各一次。

## 1. 基准做什么

```clojure
(defn create-x [n]                       ; 百万元素的对象数组，元素是装箱的 long
  (let [^objects result (make-vector n)]
    (loop [i 0] (if (>= i n) result (do (aset result i (num i)) (recur (+ i 1)))))))
(defn create-y [^objects x]              ; 再复制一份
  (let [n (alength x) ^objects result (make-vector n)]
    (loop [i (- n 1)] (if (< i 0) result (do (aset result i (aget x i)) (recur (- i 1)))))))
(defn my-try [n] (alength ^objects (create-y (create-x n))))
(defn go [m n] (loop [repeat m result ()] (if (> repeat 0) (recur (- repeat 1) (my-try n)) result)))
```

输入 500 × 1 000 000：每轮分配两个 8 MB 的数组（.NET 的大对象堆，LOH）和一百万个装箱 long（只有 -128 ~ 1023 走 LongBox 缓存）。

## 2. 主因：GC（与内联无关，一直存在）

本地 perf + libcoreclr 调试符号（dotnet-symbol 取 libcoreclr.so.dbg；perf 给的地址要加 0x1000 才对上符号）：

| 位置 | 占比 |
|---|---|
| GC 合计（mark_object_simple1、plan_phase、background_mark_phase、mark_object_simple、decide_on_compaction_space、mark_through_cards_for_uoh_objects…） | 约 67% |
| 写屏障 JIT_ByRefWriteBarrier | 3.7% |

LOH 上的数组引用 gen0 里新分配的装箱 long：每次 gen0 GC 都要沿卡表扫描大数组，装箱对象全部存活、被晋升；
JVM 的 G1 新生代大得多，年轻代回收次数少得多。

GC 参数试验（本地，单次，秒）：

| 配置 | array1 |
|---|---|
| 默认（工作站、并发） | 19.1 |
| `DOTNET_GCgen0size=0x4000000`（64 MB） | 17.7 |
| `DOTNET_GCgen0size=0x10000000`（256 MB） | 14.3 |
| `DOTNET_gcConcurrent=0` | 15.2 |
| `DOTNET_gcConcurrent=0` + gen0size 256 MB | 18.0 |
| `DOTNET_GCgen0MaxBudget=0x10000000` | 19.0 |

gen0size 对其余基准 ±5%（gcbench -20%，deriv、cpstak +7% ~ +10%），Server GC 让 deriv 慢一倍，都没有改默认值。

### 2.1 "JVM 的新生代大得多"是什么意思（已被修正）

> **更正（2026-10-16，[两机实测](gc-config-and-tier0-two-machines.md) §2）**：把 JVM 最大堆压到 512MB，array1 不变（GC 次数 145 → 671，
> 总时间持平）。JVM 的优势是**单次回收这个分配模式便宜**，不是新生代大；cleat 侧对应的是 workstation GC 每次回收的成本
> （大数组的卡表扫描、LOH 引用晋升），gen0 预算本身不是瓶颈。下面保留原分析作为记录。
分代 GC（JVM 与 .NET 相同的思路）：新对象先分配在年轻代（JVM 叫新生代 / eden，.NET 叫 gen0）；年轻代满了就做一次年轻代回收，
只找年轻代里还活着的对象，把它们复制 / 晋升到老年代，其余直接丢弃。一次年轻代回收的成本主要取决于**存活对象的多少**和
**老年代指向年轻代的引用要扫描多少**（卡表），与年轻代本身多大关系不大——所以年轻代越大，同样的分配量触发的回收越少，总成本越低。

两边的默认大小：
- JVM（默认 G1）：堆上限默认是物理内存的 1/4，新生代在堆的约 5% ~ 60% 之间自适应，常见是几百 MB 以上；
- .NET（默认工作站 GC）：gen0 预算按 CPU 缓存大小设定，通常几 MB 到几十 MB，分配满就回收一次。

放到 array1：每轮约 24 MB 的装箱 long 加两个 8 MB 数组。.NET 一轮里要做好几次 gen0 回收，每次都扫描大数组里指向新对象的引用，
而这些装箱对象都被数组引用着、全部存活，要复制晋升，之后还要在 gen1 / gen2 回收里处理（本地 100 轮约 270 次 gen0、70 多次 gen2）；
JVM 新生代大，一轮里回收一两次甚至不回收，扫描与复制次数少得多。

旁证：`DOTNET_GCgen0size=0x10000000`（256 MB）让 array1 19.1 → 14.3 s。JVM 在 linux01 上实际的新生代大小没有测过（只用默认参数加 `-Xss8m`），
要确认可加 `-Xlog:gc` 看回收次数与新生代大小。默认 gen0 不改的原因：其他基准有涨有跌（deriv、cpstak +7% ~ +10%），内存占用也明显上升。

## 3. cleat-116 的回退：内联后 go 停在 tier-0

> **更正（2026-10-16，[两机实测](gc-config-and-tier0-two-machines.md) §3）**：下面的本地复现（100 轮，1.53 倍）没有在 r7rs 尺度复现：
> 两台机器、两种 GC 配置下内联 / 分层开关对 array1 只有 0 ~ 11%，linux01 上 114 → 116 的 +38% 无法复现。
> OSR 调查降级为诊断记录（cleat-120 先复测确认）。

cleat-116 把 eval 的小函数内联进调用点：my-try 内联进 go。go 体里有循环，按 cleat-114 的规则分层编译；它只调用一次、
循环 500 次，**始终停在插桩的 tier-0**（OSR 的补丁点在，调低 `DOTNET_TC_OnStackReplacement_InitialCounter`、`DOTNET_OSR_HitLimit`、
`DOTNET_OSR_CounterBump` 也不切换，JIT 没给原因）。tier-0 把局部变量与 JIT 临时变量都报告为整个方法内活着：
调用 create-y 的实参经过 JIT 临时变量，上一轮的两个百万元素数组（连同其中一百万个装箱 long）要到下一轮覆盖同一临时变量才释放，
GC 的存活数据翻倍。

本地复现（100 轮 × 1 000 000，scratchpad/a1.clj，打印耗时与 GC 次数）：

| 配置 | 耗时 | gen0 / gen1 / gen2 次数 | go 的编译 |
|---|---|---|---|
| 默认（内联） | 5852 ms | 271 / 262 / 71 | Instrumented Tier0（不切换） |
| `CLEAT_NO_INLINE=1` | 3826 ms | 301 / 293 / 101 | Instrumented Tier0，my-try 单独 FullOpts |
| `DOTNET_TieredCompilation=0` | 3678 ms | 301 / 292 / 101 | FullOpts |
| `DOTNET_TieredPGO=0` | 6154 ms | 270 / 261 / 70 | Tier0 |

内联版 GC 次数更少，但每次更贵（存活数据多）；关掉分层编译就没有回退——确认是 tier-0 的活跃区间问题，不是内联本身的代码质量。

## 4. 可能的处理
1. 查清 go 为什么不触发 OSR（OSR 的限制条件：显式尾调用、localloc、补丁点处求值栈不空等；go 的 Lcil 里没有 `tail.`）。
   OSR 生效后，OSR 方法只报告自己用到的槽位，问题就消失。
2. 分层规则再收紧：只调用一次、循环体里调用长时间运行的函数的 fn 不分层（难以静态判断）。
3. 不把含循环的被调用者内联进分层方法的循环体（但 fft 的收益正来自内联进循环）。
4. GC 层面（主因）：减少装箱（`(num i)` 的值超出缓存范围，JVM 同样装箱，差在 GC 成本）；默认 GC 配置不动。
