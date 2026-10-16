# GC 配置与 tier-0 对照：两台机器的实测（2026-10-16）

> 回答两个问题：r7rs 的 GC 相关慢项（array1、gcbench、nboyer、deriv、cpstak）在 cleat 的 GC 配置下怎么走；
> cleat-116 的内联 / tier-0 回退在 r7rs 尺度上到底有多大。linux01 已不可用，改用两台机器：
> 本地 8 线程 i9-13900H / 23GB，david@192.168.3.111（4 vCPU / 15GB）。每项 3 次取最小，只计基准内部计时。
> 脚本 bench/r7rs/gc_bench.py（`RUN_JVM=0` 只跑 cleat 侧），结果日志未入库。

## 1. cleat 的 GC 配置矩阵

| 配置 | 机器 | array1 | gcbench | nboyer | deriv | cpstak |
|---|---|---|---|---|---|---|
| workstation（默认） | 本地 | 35.53 | 2.20 | 16.33 | **13.40** | **24.43** |
| ws + gen0 256MB | 本地 | 20.52 | 2.12 | 17.74 | 14.98 | 26.65 |
| server | 本地 | 21.15 | 2.26 | 12.08 | 24.37 | 44.01 |
| server + gen0 256MB | 本地 | **11.20** | **1.98** | 13.50 | 16.50 | 26.86 |
| workstation | VM | 25.02 | 1.94 | 11.43 | **8.96** | **16.60** |
| ws + gen0 256MB | VM | 16.04 | 1.38 | 11.49 | 9.94 | 17.48 |
| server | VM | 12.88 | 1.48 | **8.52** | 9.77 | 17.77 |
| server + gen0 256MB | VM | **8.31** | **1.39** | 10.25 | 10.25 | 17.75 |

- **server + gen0 256MB 在 array1 上两机一致 -65 ~ -68%**（本地 35.5 → 11.2，VM 25.0 → 8.3），gcbench、nboyer 同向受益。
- **deriv / cpstak 的回归随核数放大**：VM（4 核）上 server 只有 +7 ~ 15%；本地（8 线程，混合 P/E 核）server 单独
  +80 ~ 90%（deriv 24.4 vs 13.4，复现了 array1.md 里"Server GC 让 deriv 慢一倍"的本地结论），叠加 gen0 256MB 后收敛到 +10 ~ 23%。
- 按 56 项折算几何平均：server + gen0 约 **-2.1%（本地）/ -2.4%（VM）**。
- server GC 的 gen0 预算按核数 × 堆数增长（32 核机器上 256MB × 32 = 8GB 量级保留）；cleat 是部署目标未知的 CLI 工具，
  **这两个旋钮不做默认值**，只作为批处理配置与诊断手段（designs/22 §6 的结论由本表支撑）。

## 2. JVM 参数与堆：优势不在"堆大"

| 配置 | array1 | gcbench | nboyer | deriv | cpstak | (array1 GC 次数) |
|---|---|---|---|---|---|---|
| notes（`-Xss8m`） | 6.63 | 0.40 | 5.51 | 3.32 | 4.47 | 145 |
| 全默认 | 6.52 | 0.41 | 5.67 | 3.34 | 4.48 | 151 |
| `-Xss8m -Xmx1g` | 6.88 | 0.35 | 5.36 | 3.20 | 4.33 | 470 |
| `-Xss8m -Xmx512m` | 6.83 | 0.32 | 6.39 | 3.20 | 4.24 | 671 |

- `-Xss8m` 与全默认差在噪声内（它只影响栈深，r7rs-performance.md 用它只是对齐 .NET 主线程栈）。
- **把 JVM 最大堆砍到 512MB，array1 纹丝不动**（6.83 vs 6.52），GC 次数 145 → 671（4.6 倍）总时间持平，gcbench 反而更快。
- 结论：JVM 的优势是**单次回收这个分配模式便宜**（16MB humongous 数组不走卡表扫描、幸存者装箱对象少），不是堆预算大。
  array1.md §2"G1 新生代大得多，年轻代回收次数少得多"应照此修正；cleat 侧对应的问题是 workstation GC 每次回收的成本
  （卡表扫描大数组、LOH 引用晋升），gen0 预算本身不是瓶颈。

## 3. 内联 / tier-0 回退在 r7rs 尺度上很小

VM 上 array1 的开关对照（单次）：

| 配置 | workstation | server + gen0 256MB |
|---|---|---|
| 默认（内联） | 25.3 | 13.0 |
| `CLEAT_NO_INLINE=1` | 23.1（-9%） | 13.2（无差） |
| `DOTNET_TieredCompilation=0` | 25.2（无差） | 13.0（无差） |
| `DOTNET_TC_CallCountingDelayMs=0` | 25.6（无差） | 13.6（无差） |

- array1.md §3 记录的本地复现（100 轮 scratchpad，内联 5852 vs 不内联 3826 = 1.53x）**没有在 r7rs 尺度复现**：
  两台机器、两种 GC 配置下，内联 / 分层开关对 array1 的影响只有 0 ~ 11%，`CallCountingDelayMs` 假设不成立，
  linux01 上 114 → 116 的 +38% 无法在现有机器复现。
- 结论：cleat-116 回退的 OSR 调查降级为诊断记录（cleat-120 先复测确认，再决定是否排查）；它不是 array1 的主要杠杆，
  GC 配置（上面的矩阵）才是。

## 4. 对优化的含义

1. GC 敏感负载的批处理配置：`DOTNET_gcServer=1 DOTNET_GCgen0size=0x10000000`（预期 array1 -65%、gcbench -30%、
   nboyer -20%，deriv / cpstak +10 ~ 23%，随核数变差）；默认值不改。
2. array1 在批处理配置下 8.3 vs JVM 6.6 ≈ 1.25x，剩余是 LOH 卡表扫描与写屏障的宿主机制差异，接受。
3. 长尾优化继续走 designs/22 §2（类型化调用、分配快路径）；tier-0 / OSR 与"JVM 堆大"两个假设关闭。
