# r7rs 56 项：cleat 与 JVM Clojure 的对比（2026-10-16）

> linux01（2026-10-16 起不可用，此表为历史数字，与新机器不可直接比较）；"JVM"列是 **GraalVM CE 25.0.2**（Graal JIT），不是 HotSpot C2。只计基准内部计时（`+!CSVLINE!+`），不含启动与编译。JVM 是 cleat-107 时各项的最小值（Clojure 1.12，`-Xss8m`）；
> cleat 三列各跑一次：107 = M11 开始前，116 = 已提交的 a74d2ec，当前 = cleat-117 中途（boot 镜像分层编译 + 惰性序列头修复 +
> rem / quot / longCast / nth 快路径，不含 `not` 内联，未提交）。倍数 = cleat / JVM，小于 1 表示比 JVM 快；按当前倍数排序。
> 移植与运行方式见 bench/r7rs/README.md 与 tasks/cleat-107。

| 基准 | JVM | cleat-107 | cleat-116 | 当前 | 107 倍数 | 当前倍数 |
|---|---|---|---|---|---|---|
| cat | 5.69 | 3.59 | 1.71 | 1.71 | 0.63x | **0.30x** |
| ack | 7.48 | 3.81 | 2.03 | 2.64 | 0.51x | **0.35x** |
| fib | 3.80 | 3.71 | 2.13 | 2.09 | 0.98x | **0.55x** |
| lattice | 10.12 | 14.68 | 8.44 | 8.61 | 1.45x | **0.85x** |
| mazefun | 5.52 | 9.14 | 4.86 | 5.01 | 1.66x | **0.91x** |
| pi | 0.94 | 0.88 | 0.87 | 0.87 | 0.94x | **0.93x** |
| nqueens | 14.25 | 60.48 | 14.95 | 14.94 | 4.24x | 1.05x |
| wc | 4.21 | 4.63 | 4.98 | 4.54 | 1.10x | 1.08x |
| ntakl | 11.92 | 59.08 | 12.68 | 13.30 | 4.96x | 1.12x |
| fibfp | 0.52 | 0.63 | 0.55 | 0.58 | 1.21x | 1.12x |
| sumfp | 0.22 | 1.87 | 0.24 | 0.24 | 8.65x | 1.12x |
| triangl | 2.41 | 17.92 | 3.73 | 2.81 | 7.44x | 1.17x |
| sum | 0.43 | 2.28 | 0.54 | 0.52 | 5.25x | 1.21x |
| read1 | 1.87 | 2.15 | 2.22 | 2.28 | 1.15x | 1.22x |
| takl | 11.93 | 64.16 | 15.11 | 15.60 | 5.38x | 1.31x |
| primes | 1.86 | 10.35 | 3.14 | 2.52 | 5.56x | 1.35x |
| divrec | 6.83 | 17.34 | 8.99 | 9.39 | 2.54x | 1.37x |
| equal | 1.53 | 4.63 | 2.16 | 2.23 | 3.02x | 1.46x |
| sum1 | 1.01 | 1.51 | 1.49 | 1.47 | 1.50x | 1.46x |
| mperm | 11.89 | 21.89 | 18.13 | 18.17 | 1.84x | 1.53x |
| chudnovsky | 0.32 | 0.54 | 0.48 | 0.49 | 1.69x | 1.54x |
| sboyer | 3.00 | 11.54 | 4.87 | 4.83 | 3.85x | 1.61x |
| mbrot | 0.30 | 0.75 | 0.46 | 0.49 | 2.49x | 1.62x |
| conform | 4.46 | 13.27 | 6.93 | 7.24 | 2.97x | 1.62x |
| paraffins | 6.00 | 9.93 | 8.96 | 9.93 | 1.65x | 1.65x |
| diviter | 2.81 | 10.15 | 5.70 | 5.26 | 3.62x | 1.87x |
| matrix | 2.34 | 10.02 | 4.37 | 4.46 | 4.28x | 1.90x |
| parsing | 3.33 | 13.94 | 7.18 | 6.93 | 4.19x | 2.08x |
| tail | 0.30 | 0.69 | 0.73 | 0.65 | 2.26x | 2.16x |
| graphs | 3.10 | 15.66 | 7.46 | 6.77 | 5.05x | 2.18x |
| slatex | 2.40 | 5.60 | 4.79 | 5.57 | 2.33x | 2.32x |
| pnpoly | 1.32 | 6.98 | 3.23 | 3.15 | 5.28x | 2.39x |
| tak | 1.19 | 6.30 | 2.99 | 3.05 | 5.27x | 2.55x |
| nucleic | 1.31 | 7.98 | 4.01 | 3.65 | 6.07x | 2.77x |
| dynamic | 3.39 | 15.78 | 8.93 | 9.47 | 4.65x | 2.79x |
| quicksort | 1.38 | 8.22 | 4.03 | 3.87 | 5.97x | 2.81x |
| destruc | 1.81 | 13.70 | 5.16 | 5.32 | 7.59x | 2.95x |
| nboyer | 2.94 | 14.07 | 8.75 | 8.75 | 4.79x | 2.98x |
| simplex | 1.98 | 16.03 | 6.42 | 6.04 | 8.08x | 3.04x |
| maze | 0.70 | 7.70 | 2.24 | 2.23 | 10.97x | 3.17x |
| deriv | 2.12 | 8.84 | 6.95 | 6.89 | 4.17x | 3.25x |
| scheme | 3.67 | 26.50 | 12.40 | 12.10 | 7.23x | 3.30x |
| earley | 2.70 | 11.43 | 9.52 | 9.64 | 4.24x | 3.58x |
| string | 0.42 | 1.52 | 1.53 | 1.52 | 3.65x | 3.66x |
| puzzle | 1.39 | 14.56 | 6.52 | 5.61 | 10.46x | 4.03x |
| fft | 0.36 | 5.84 | 1.45 | 1.48 | 16.02x | 4.05x |
| gcbench | 0.29 | 2.07 | 1.34 | 1.32 | 7.09x | 4.53x |
| peval | 3.51 | 27.88 | 15.69 | 16.00 | 7.93x | 4.55x |
| cpstak | 2.89 | 22.86 | 13.81 | 14.77 | 7.91x | 5.11x |
| bv2string | 0.37 | 10.11 | 2.33 | 1.99 | 27.13x | 5.33x |
| ray | 0.50 | 7.28 | 2.70 | 2.76 | 14.57x | 5.52x |
| browse | 0.88 | 12.77 | 7.10 | 7.40 | 14.55x | 8.44x |
| mbrotZ | 0.32 | 24.69 | 2.72 | 2.70 | 77.98x | 8.53x |
| array1 | 1.26 | 16.98 | 19.79 | 18.93 | 13.46x | 15.01x |
| fibc | 1.80 | 41.51 | 33.59 | 33.91 | 23.11x | 18.88x |
| ctak | 0.61 | 56.91 | 42.95 | 43.88 | 93.75x | 72.28x |
| **几何平均** | | | | | **4.52x** | **2.27x**（116：2.29x） |

几何平均的演进：107 4.52 → 109 3.74 → 110..113 3.26 → 114 2.64 → 115..116 2.29 → 117 中途 2.27。

## 剩下的慢项与原因
- **ctak / fibc**：call/cc 用异常模拟，.NET 的 throw / catch 走完整的栈展开（见 notes/escape-continuations-and-try.md）。
- **array1、gcbench、nboyer**：时间大量在 GC（array1 约 67%、nboyer 约 40%），差在单次回收成本（见 notes/gc-config-and-tier0-two-machines.md）；
  array1 的 tier-0 回退在两机复测里只有 0 ~ 11%。
- **string**：.NET 的 UTF-16 字符串，JVM 有 Latin-1 紧凑字符串，大字符串拼接的搬运字节量差一倍。
- **mbrotZ**：每步分配的复数对象被 GraalVM 的部分逃逸分析消掉（C2 上待实测），.NET 没有对应优化。
- **cpstak、browse、peval 等**：分配快速路径（`RhpNewFast` 经 `__tls_get_addr` 取线程分配上下文）与 GC、大方法里 JIT 内联预算耗尽。

## 本地的其他对照（单次，本地机器约比 linux01 慢 1.3 ~ 1.5 倍）
- boot 镜像分层编译（14 项）：最初的实验（所有方法分层）平均约快 3%、启动快约 1 s；收紧规则修完惰性序列头之后基本持平。
- `clojure.core/not` 加 `:inline` 后数值递归函数拿到类型化入口：tak 3.7 → 1.9 s（未在 linux01 验证）。
- sumfp：循环后内联的 double 装箱 → 不内联的辅助方法，0.91 → 0.24 s（C# 复现 911 → 226 ms）。

## 192.168.3.111 上的三方对比（2026-10-16，cleat-117 收口前的版本）

> 机器：4 vCPU / 15 GB 的虚拟机（linux01 不可用后的测试机）；.NET 运行时 10.0.0（本机是 10.0.12）。cleat 是 cleat-117 的工作区版本
> （镜像分层、惰性序列头修复、`not` 的 `:inline`、rem / quot / longCast / nth 快路径），各项一次；JVM 两种各一次：
> **C2** = 该机默认的 OpenJDK 25.0.4（Red Hat，HotSpot C2），**GraalVM** = 从本机复制过去的 GraalVM CE 25.0.2（cleat-107 ~ 117 的"JVM"列用的就是它）。
> 只计基准内部计时；按 cleat / C2 排序。与上面 linux01 的表不可直接比较（机器、JVM 都不同）。

| 基准 | C2 | GraalVM | cleat | cleat / C2 | cleat / GraalVM |
|---|---|---|---|---|---|
| sum | 6.55 | 0.64 | 0.71 | 0.11x | 1.11x |
| sumfp | 1.01 | 0.39 | 0.38 | 0.38x | 0.98x |
| ack | 5.02 | 6.85 | 1.92 | 0.38x | 0.28x |
| ntakl | 35.57 | 15.27 | 15.92 | 0.45x | 1.04x |
| lattice | 18.57 | 12.62 | 10.12 | 0.55x | 0.80x |
| fibfp | 0.97 | 1.12 | 0.67 | 0.69x | 0.60x |
| pi | 1.13 | 1.15 | 0.96 | 0.85x | 0.84x |
| fib | 2.89 | 6.02 | 2.74 | 0.95x | 0.45x |
| mazefun | 6.05 | 4.53 | 6.09 | 1.01x | 1.34x |
| conform | 9.16 | 6.82 | 9.52 | 1.04x | 1.40x |
| tak | 1.51 | 1.89 | 1.58 | 1.05x | 0.84x |
| cat | 2.80 | 2.82 | 2.96 | 1.06x | 1.05x |
| paraffins | 12.04 | 11.69 | 12.88 | 1.07x | 1.10x |
| sboyer | 5.52 | 4.91 | 5.93 | 1.07x | 1.21x |
| nqueens | 14.68 | 15.13 | 16.28 | 1.11x | 1.08x |
| takl | 14.02 | 16.44 | 16.70 | 1.19x | 1.02x |
| chudnovsky | 0.44 | 0.51 | 0.54 | 1.23x | 1.07x |
| mperm | 19.86 | 17.41 | 24.96 | 1.26x | 1.43x |
| quicksort | 3.48 | 1.76 | 4.56 | 1.31x | 2.60x |
| sum1 | 1.45 | 1.36 | 1.94 | 1.33x | 1.42x |
| triangl | 2.04 | 2.66 | 2.80 | 1.37x | 1.05x |
| diviter | 5.18 | 4.53 | 7.17 | 1.39x | 1.58x |
| graphs | 5.48 | 5.07 | 8.21 | 1.50x | 1.62x |
| mbrot | 0.34 | 0.35 | 0.54 | 1.59x | 1.55x |
| divrec | 6.29 | 5.57 | 10.66 | 1.70x | 1.91x |
| matrix | 3.24 | 3.50 | 5.54 | 1.71x | 1.58x |
| primes | 1.94 | 2.04 | 3.41 | 1.76x | 1.67x |
| slatex | 6.36 | 5.96 | 11.21 | 1.76x | 1.88x |
| read1 | 1.72 | 1.64 | 3.20 | 1.86x | 1.95x |
| simplex | 4.77 | 3.11 | 9.04 | 1.89x | 2.91x |
| tail | 0.51 | 0.56 | 0.97 | 1.89x | 1.74x |
| equal | 1.44 | 2.04 | 2.83 | 1.97x | 1.39x |
| nboyer | 5.70 | 5.26 | 11.29 | 1.98x | 2.15x |
| string | 0.88 | 0.91 | 1.92 | 2.18x | 2.11x |
| parsing | 4.60 | 3.92 | 10.96 | 2.38x | 2.80x |
| nucleic | 2.31 | 2.21 | 5.64 | 2.44x | 2.55x |
| destruc | 2.73 | 2.56 | 6.81 | 2.50x | 2.66x |
| maze | 1.11 | 1.25 | 2.85 | 2.56x | 2.29x |
| pnpoly | 1.46 | 1.90 | 3.76 | 2.57x | 1.98x |
| fft | 0.81 | 0.73 | 2.10 | 2.60x | 2.86x |
| deriv | 3.36 | 2.90 | 8.90 | 2.65x | 3.07x |
| earley | 4.54 | 4.84 | 12.95 | 2.85x | 2.67x |
| wc | 2.24 | 2.08 | 6.42 | 2.87x | 3.09x |
| ray | 1.27 | 1.05 | 3.86 | 3.05x | 3.69x |
| scheme | 4.86 | 5.06 | 16.30 | 3.35x | 3.22x |
| bv2string | 0.75 | 0.73 | 2.69 | 3.60x | 3.69x |
| puzzle | 1.94 | 2.10 | 7.13 | 3.68x | 3.39x |
| mbrotZ | 0.99 | 0.40 | 3.67 | 3.71x | 9.24x |
| cpstak | 4.47 | 4.55 | 16.84 | 3.76x | 3.70x |
| array1 | 6.61 | 7.19 | 25.38 | 3.84x | 3.53x |
| dynamic | 3.58 | 4.38 | 14.32 | 4.00x | 3.27x |
| gcbench | 0.42 | 0.51 | 1.89 | 4.47x | 3.68x |
| peval | 4.25 | 5.43 | 21.72 | 5.11x | 4.00x |
| browse | 1.30 | 1.61 | 8.44 | 6.48x | 5.25x |
| fibc | 1.33 | 2.19 | 43.24 | 32.54x | 19.74x |
| ctak | 0.86 | 1.09 | 48.00 | 56.01x | 44.02x |
| **几何平均（56 项）** | | | | **1.85x** | **1.99x** |

- GraalVM 对 C2 的几何平均是 0.93（GraalVM 快约 7%），但各项差别很大：mbrotZ 0.40（部分逃逸分析消掉复数分配）、quicksort 0.50、
  simplex 0.65、mazefun 0.75；反过来 fib 2.08、equal 1.42、ack 1.37、pnpoly 1.30、peval 1.28 是 C2 更快。
- 对 C2：sum、sumfp、ack、ntakl、lattice、fibfp、pi、fib 8 项 cleat 更快，另有 mazefun、conform、tak、cat、paraffins、sboyer、nqueens
  在 1.11 倍以内。最慢的仍是 fibc / ctak（异常）、browse、peval、gcbench、dynamic、array1、cpstak、mbrotZ。
- 这台机器上 JVM 的 array1 是 6.6 s（linux01 上是 1.26 s），cleat 只差约 1.3 倍：array1 在 JVM 上对机器很敏感，原因未查。

## M11 收尾（cleat-119，2026-10-16）：192.168.3.111，各 3 次取最小

> 版本：cleat-118（6ed0da4）；cleat、OpenJDK 25.0.4（C2）、GraalVM CE 25.0.2 各跑 56 项 × 3 次，表中为最小值（秒），按 cleat / C2 排序。
> 几何平均：**cleat / C2 1.88x、cleat / GraalVM 2.05x**（取中位数时 1.86x / 2.02x）；GraalVM / C2 0.92。上一节 117 的单次结果是 1.85x / 1.99x，
> 差别在单次与三次取最小的口径上（JVM 三次取最小受益更多）。

| 基准 | C2 | GraalVM | cleat | cleat / C2 | cleat / GraalVM |
|---|---|---|---|---|---|
| sum | 6.45 | 0.64 | 0.90 | 0.14x | 1.42x |
| ack | 5.03 | 4.51 | 1.86 | 0.37x | 0.41x |
| sumfp | 0.99 | 0.39 | 0.38 | 0.39x | 0.98x |
| lattice | 18.61 | 12.71 | 9.87 | 0.53x | 0.78x |
| fibfp | 0.96 | 1.01 | 0.61 | 0.64x | 0.60x |
| pi | 1.12 | 1.15 | 0.96 | 0.86x | 0.84x |
| fib | 2.89 | 5.96 | 2.60 | 0.90x | 0.44x |
| cat | 2.78 | 2.82 | 2.74 | 0.99x | 0.97x |
| conform | 8.82 | 6.93 | 8.92 | 1.01x | 1.29x |
| tak | 1.48 | 1.89 | 1.52 | 1.03x | 0.81x |
| sboyer | 5.65 | 4.80 | 5.89 | 1.04x | 1.23x |
| mazefun | 5.75 | 4.47 | 6.06 | 1.05x | 1.36x |
| paraffins | 11.84 | 11.50 | 12.80 | 1.08x | 1.11x |
| nqueens | 14.66 | 15.04 | 16.16 | 1.10x | 1.07x |
| chudnovsky | 0.46 | 0.48 | 0.51 | 1.11x | 1.07x |
| takl | 14.20 | 16.20 | 16.85 | 1.19x | 1.04x |
| mperm | 19.32 | 17.47 | 24.78 | 1.28x | 1.42x |
| sum1 | 1.47 | 1.32 | 1.94 | 1.32x | 1.47x |
| quicksort | 3.40 | 1.75 | 4.64 | 1.37x | 2.66x |
| triangl | 1.99 | 2.28 | 2.76 | 1.39x | 1.21x |
| ntakl | 11.23 | 12.71 | 15.68 | 1.40x | 1.23x |
| mbrot | 0.33 | 0.34 | 0.48 | 1.44x | 1.38x |
| graphs | 5.44 | 5.12 | 8.25 | 1.52x | 1.61x |
| diviter | 4.63 | 3.65 | 7.27 | 1.57x | 1.99x |
| matrix | 3.30 | 3.05 | 5.47 | 1.65x | 1.79x |
| divrec | 6.20 | 6.04 | 10.62 | 1.71x | 1.76x |
| primes | 1.98 | 1.87 | 3.41 | 1.72x | 1.82x |
| simplex | 4.76 | 3.10 | 8.17 | 1.72x | 2.63x |
| slatex | 5.61 | 6.02 | 10.07 | 1.80x | 1.67x |
| parsing | 4.72 | 3.90 | 8.80 | 1.87x | 2.26x |
| equal | 1.45 | 1.98 | 2.75 | 1.90x | 1.39x |
| read1 | 1.70 | 1.55 | 3.24 | 1.91x | 2.09x |
| tail | 0.51 | 0.54 | 0.98 | 1.94x | 1.81x |
| nboyer | 5.40 | 5.03 | 11.44 | 2.12x | 2.27x |
| string | 0.87 | 0.86 | 1.97 | 2.27x | 2.29x |
| pnpoly | 1.47 | 1.59 | 3.59 | 2.44x | 2.26x |
| destruc | 2.67 | 2.44 | 6.64 | 2.49x | 2.73x |
| nucleic | 2.30 | 2.17 | 5.72 | 2.49x | 2.63x |
| maze | 1.11 | 1.22 | 2.81 | 2.54x | 2.30x |
| deriv | 3.37 | 2.84 | 9.01 | 2.67x | 3.17x |
| fft | 0.79 | 0.69 | 2.11 | 2.67x | 3.04x |
| earley | 4.36 | 4.80 | 12.55 | 2.88x | 2.61x |
| wc | 2.22 | 2.07 | 6.44 | 2.89x | 3.11x |
| ray | 1.30 | 1.00 | 3.82 | 2.95x | 3.82x |
| scheme | 4.79 | 5.03 | 16.15 | 3.37x | 3.21x |
| mbrotZ | 1.00 | 0.40 | 3.61 | 3.60x | 9.03x |
| cpstak | 4.48 | 4.48 | 16.62 | 3.71x | 3.71x |
| bv2string | 0.71 | 0.70 | 2.70 | 3.79x | 3.84x |
| puzzle | 1.84 | 2.10 | 7.02 | 3.81x | 3.35x |
| array1 | 6.46 | 6.87 | 25.28 | 3.92x | 3.68x |
| dynamic | 3.58 | 4.31 | 14.47 | 4.05x | 3.35x |
| gcbench | 0.41 | 0.51 | 1.90 | 4.64x | 3.69x |
| peval | 4.28 | 5.30 | 21.70 | 5.07x | 4.09x |
| browse | 1.29 | 1.43 | 7.96 | 6.18x | 5.56x |
| fibc | 1.31 | 2.22 | 43.26 | 33.11x | 19.53x |
| ctak | 0.85 | 0.75 | 48.52 | 57.27x | 64.91x |
| **几何平均** | | | | **1.88x** | **2.05x** |

## cleat-123 vs cleat-127（2026-10-16，linux01 = 192.168.0.10）

linux01 重装上线后地址为 192.168.0.10（上表 linux01 数字为历史）。192.168.3.111 已离线。cleat-123（标量替换与内联配套，17964a8）对 cleat-127（1857d7d），
56 项各 3 轮取最小值、同一安静窗口先后跑；只计基准内部计时。几何平均 **0.930**（cleat-123 快 7%）。JVM 对照在本机的重测留到 cleat-126 / cleat-130。
第一轮曾在开机后另一个 JVM 应用启动的干扰窗口内测出 nucleic / mperm / maze / ntakl / lattice 约 2x 的回退假象，安静环境复跑消失——结论只信同窗口对照。

| 基准 | cleat-127 | cleat-123 | 倍数 |
|---|---|---|---|
| destruc | 5.66 | 2.37 | 0.42x |
| mbrotZ | 2.81 | 1.76 | 0.63x |
| browse | 7.05 | 4.80 | 0.68x |
| string | 1.80 | 1.31 | 0.73x |
| slatex | 6.02 | 4.50 | 0.75x |
| simplex | 6.96 | 5.35 | 0.77x |
| divrec | 11.67 | 9.16 | 0.78x |
| bv2string | 2.07 | 1.67 | 0.81x |
| dynamic | 9.80 | 8.30 | 0.85x |
| earley | 9.94 | 8.65 | 0.87x |
| sboyer | 4.87 | 4.34 | 0.89x |
| nboyer | 8.99 | 8.11 | 0.90x |
| peval | 16.87 | 15.38 | 0.91x |
| gcbench | 1.30 | 1.19 | 0.91x |
| equal | 2.29 | 2.12 | 0.93x |
| mperm | 19.50 | 18.20 | 0.93x |
| fibfp | 0.53 | 0.50 | 0.93x |
| tail | 0.67 | 0.63 | 0.94x |
| conform | 7.27 | 6.88 | 0.95x |
| matrix | 4.56 | 4.35 | 0.95x |
| cat | 1.77 | 1.70 | 0.96x |
| ack | 2.00 | 1.93 | 0.96x |
| tak | 1.25 | 1.20 | 0.96x |
| maze | 2.27 | 2.20 | 0.97x |
| fib | 2.17 | 2.11 | 0.97x |
| graphs | 6.78 | 6.57 | 0.97x |
| scheme | 12.32 | 11.96 | 0.97x |
| deriv | 6.86 | 6.67 | 0.97x |
| primes | 2.64 | 2.58 | 0.98x |
| cpstak | 13.42 | 13.10 | 0.98x |
| array1 | 20.03 | 19.56 | 0.98x |
| sumfp | 0.24 | 0.24 | 0.98x |
| sum | 0.55 | 0.54 | 0.99x |
| fft | 1.50 | 1.48 | 0.99x |
| sum1 | 1.45 | 1.43 | 0.99x |
| fibc | 34.30 | 34.00 | 0.99x |
| nqueens | 13.92 | 13.80 | 0.99x |
| lattice | 8.46 | 8.40 | 0.99x |
| takl | 13.74 | 13.67 | 1.00x |
| ntakl | 12.79 | 12.74 | 1.00x |
| pnpoly | 3.17 | 3.16 | 1.00x |
| quicksort | 3.74 | 3.74 | 1.00x |
| wc | 4.55 | 4.58 | 1.01x |
| ctak | 36.59 | 36.91 | 1.01x |
| paraffins | 9.24 | 9.33 | 1.01x |
| ray | 2.73 | 2.77 | 1.02x |
| triangl | 2.69 | 2.74 | 1.02x |
| nucleic | 3.63 | 3.70 | 1.02x |
| pi | 0.85 | 0.87 | 1.03x |
| mazefun | 5.07 | 5.20 | 1.03x |
| read1 | 2.24 | 2.31 | 1.03x |
| puzzle | 5.48 | 5.68 | 1.04x |
| parsing | 6.99 | 7.43 | 1.06x |
| diviter | 5.27 | 5.63 | 1.07x |
| chudnovsky | 0.45 | 0.49 | 1.07x |
| mbrot | 0.44 | 0.51 | 1.16x |
| **几何平均** | | | **0.930x** |

## cleat-120 array1 循环内联限制（2026-10-17，linux01）

`loopinl` 只阻止循环体里展开后仍调用用户函数的内联（core 助手与纯算术子句照旧）。array1 同窗口对照 14.3 s（开）
vs 19.6 s（`CLEAT_NO_OPT=loopinl` 关）；安静抽样中其他受影响基准基本持平。初始宽规则曾测出 1.026 几何平均，怀疑把
`clojure.core/apply` 一类助手也挡住；只应把耗时不可静态判断的用户函数留在循环调用点。跑 A/B 时还须避开开机后另一 JVM
应用等负载（同 cleat-123 教训），只采信同窗口安静环境结果。

## cleat-130 M13 数组提示与三方收尾（2026-10-17，linux01 = 192.168.0.10）

normal 与 nohint 各为 56 项 × 3 轮，`run130.py` 在同一基准 / 轮次窗口里交错跑 cleat、OpenJDK C2 与
GraalVM；取每项最小值。normal 完整 56 项，几何比 cleat / C2 = 1.672、cleat / GraalVM = 1.791、
GraalVM / C2 = 0.934。`fibc` / `ctak` 已改成 cleat 用 `escape*`、JVM 用 `r7rs.util/call-with-escape`，
这次几何平均因此覆盖 56 项，不再排除这两项。

nohint 由 `nohint.py` 生成：只移除 22 个文件里的元素数组提示，保留 `^long` / `^double` 等标量提示。
nohint 的 `maze` 触发一个新的 cleat 编译器问题：`print-hexmaze` 报 `codegen: unbound variable g$t$…$inl`；
关闭 `arrayspec` / `loopspec` / `infer` 任一项都能编译，所以问题在数组推测、循环推测和内联临时变量的交互。
nohint 几何比只含另外 55 项：cleat / C2 = 1.817、cleat / GraalVM = 1.936、GraalVM / C2 = 0.937。

数组提示本身的作用看 22 个改写文件中两变体都成功的 21 项：nohint / normal 的 cleat 几何比 **1.281**，
也就是提示平均省约 22% 时间。fft 7.58x、simplex 2.63x、triangl 2.48x、mbrot 1.65x、quicksort 与
conform 约 1.36x；array1 / matrix 基本持平，说明 array1 主要靠循环推测而不是元素数组提示。
同 22 项 JVM 对照 C2 = 0.997、GraalVM = 1.009，证实差异来自 cleat 编译器。全部 55 个可比项的
nohint / normal 几何比是 cleat 1.101、C2 1.004、GraalVM 1.008。

补充微基准：数组推测开启 / 关闭中位数为 long-sum 2.111 / 42.681 ms、long-fill 1.396 / 153.610 ms、
double-sum 3.204 / 8.820 ms、object-count 3.253 / 3.892 ms。eval-only 类型反馈两参候选
50M 次为 141.060 / 85.180 ms（约 1.12 ns / 次残差），常规文件路径仍是零触发；继续支持 cleat-129 关闭。

## cleat-131 nohint maze 的作用域修复（2026-10-17）

上面 nohint `maze` 的失败根因不是基准源，而是数组推测违反了自己的候选前提：`print-hexmaze`
里的内联结果留在循环体的 `let` 临时变量中，旧判断却把它当作循环外数组，先在循环入口生成
`isinst` 守卫，再引用还未绑定的 `g$t$…$inl`。cleat-131 现在收集循环体内的 `let` / label / 形参绑定；
候选命中这些名字时保留原循环。`arrayspec_semantics` 已补循环体内已知调用返回数组的用例，
nohint `maze` 也能用原始输入完整运行。

## M15 验证（cleat-141，2026-10-20）：192.168.3.111，各 3 次取最小

> 版本：cleat 65d407d（cleat-141 收口，M15）；OpenJDK 25.0.4（Red Hat，HotSpot C2）；56 项 × 3 次取最小（秒）。
> 只计基准内部计时。cleat 用 .NET SDK 10.0.100 的 Release 构建。

| 基准 | cleat | JVM (C2) | 倍数 |
|---|---:|---:|---:|
| sum | 0.721 | 6.559 | 0.11x |
| sumfp | 0.394 | 0.986 | 0.40x |
| ack | 1.844 | 5.126 | 0.36x |
| fibfp | 0.670 | 0.975 | 0.69x |
| lattice | 10.363 | 18.279 | 0.57x |
| takl | 17.209 | 34.773 | 0.50x |
| fib | 2.651 | 2.959 | 0.90x |
| pi | 1.103 | 1.150 | 0.96x |
| sboyer | 5.371 | 5.565 | 0.97x |
| tak | 1.519 | 1.513 | 1.00x |
| cat | 2.817 | 2.807 | 1.00x |
| mazefun | 6.057 | 5.957 | 1.02x |
| paraffins | 13.193 | 11.945 | 1.10x |
| conform | 9.898 | 9.139 | 1.08x |
| nqueens | 16.880 | 15.083 | 1.12x |
| ctak | 1.012 | 0.871 | 1.16x |
| destruc | 3.118 | 2.716 | 1.15x |
| chudnovsky | 0.584 | 0.475 | 1.23x |
| mperm | 25.316 | 19.983 | 1.27x |
| triangl | 2.576 | 2.036 | 1.27x |
| sum1 | 1.979 | 1.488 | 1.33x |
| quicksort | 4.753 | 3.476 | 1.37x |
| ntakl | 16.337 | 11.816 | 1.38x |
| fibc | 1.853 | 1.304 | 1.42x |
| graphs | 8.996 | 5.515 | 1.63x |
| matrix | 5.373 | 3.266 | 1.65x |
| diviter | 7.963 | 4.656 | 1.71x |
| simplex | 7.399 | 4.792 | 1.54x |
| tail | 0.892 | 0.511 | 1.75x |
| divrec | 11.486 | 6.361 | 1.81x |
| mbrot | 0.614 | 0.337 | 1.82x |
| primes | 3.779 | 1.997 | 1.89x |
| slatex | 10.705 | 5.787 | 1.85x |
| read1 | 3.290 | 1.660 | 1.98x |
| nboyer | 10.933 | 5.600 | 1.95x |
| equal | 3.032 | 1.503 | 2.02x |
| mbrotZ | 2.180 | 1.014 | 2.15x |
| pnpoly | 3.164 | 1.493 | 2.12x |
| string | 2.112 | 0.875 | 2.41x |
| nucleic | 5.898 | 2.367 | 2.49x |
| wc | 6.288 | 2.283 | 2.76x |
| maze | 3.131 | 1.121 | 2.79x |
| fft | 2.253 | 0.804 | 2.80x |
| ray | 3.771 | 1.291 | 2.92x |
| deriv | 9.887 | 3.350 | 2.95x |
| earley | 12.086 | 4.495 | 2.69x |
| parsing | 10.892 | 4.700 | 2.32x |
| puzzle | 7.158 | 1.959 | 3.65x |
| array1 | 24.054 | 6.758 | 3.56x |
| bv2string | 2.797 | 0.717 | 3.90x |
| scheme | 17.056 | 4.914 | 3.47x |
| cpstak | 18.299 | 4.509 | 4.06x |
| dynamic | 16.710 | 3.669 | 4.55x |
| browse | 5.761 | 1.310 | 4.40x |
| gcbench | 1.989 | 0.412 | 4.82x |
| peval | 21.428 | 4.323 | 4.96x |
| **几何平均（56 项）** | | | **1.61x** |

- 同机同 JVM 口径下从 cleat-119 的 1.88x 进步到 1.61x。最大的修复来自 M14/M15 的续延与异常路径：
  ctak 56.0x → 1.16x、fibc 32.5x → 1.42x；dynamic 4.0x → 4.6x、scheme 3.35x → 3.47x 略有回退待查。
- cleat 更快的有 12 项（sum 0.11x、sumfp、ack、lattice、takl、fib、fibfp、pi、sboyer，
  及约持平的 tak、cat、mazefun）。
- 最慢的仍是 peval、gcbench、dynamic、cpstak、browse、scheme、array1、puzzle、bv2string：
  解释/元编程、GC 压力与续延密集型负载是后续优化的主要空间。

## M15 差异归因（2026-10-20，192.168.3.111）

> 三组实验，脚本 `bench/r7rs/attr.py`（GC / JVM 内联 / takl 复核）与 `bench/r7rs/src/micro.cljc`
> （调用与分配路径微基准，同一份 cljc 跑两宿主：`dotnet …/cleat.dll -I src -m micro` 与
> `java -Xss8m -cp src:$(clojure -Spath) clojure.main -m micro`）。

### 1. 微基准：差异几乎全部在"分配"和"小访问器的内联"上

每格 ns/次，各 3 次取最小（循环 2×10^7）。

| 微基准 | cleat | JVM C2 | cleat / JVM |
|---|---:|---:|---:|
| mcar 读（defn 包装 + 接口调用） | 2.9 | 1.3 | 2.2x |
| mcdr 读 | 4.4 | 1.0 | 4.4x |
| 可变字段写 + 读（set-car!/car） | 14.1 | 4.8 | 2.9x |
| 两参普通函数调用（未提示装箱算术） | 3.9 | 4.4 | **0.9x** |
| 无提示循环里的减法 | 0.6 | 0.6 | 1.0x |
| 分配可变序对后读（不逃逸） | 13.6 | 1.1 | **12.4x** |
| 分配不可变 cons 后 first（不逃逸） | 13.4 | 1.2 | **11.2x** |
| 分配并写入对象数组（真逃逸） | 28.9 | 5.8 | 5.0x |
| 装箱整数写入对象数组 | 10.2 | 2.2 | 4.6x |

- 不逃逸的小对象在 JVM 上是 1.1 ns/次 — C2 的标量替换（escape analysis）把分配整个消掉；.NET 没有对应优化，
  13.6 ns 是一次真实堆分配 + 写屏障 + GC 摊销。r7rs 里 pair / cons 密集的项（peval、dynamic、browse、scheme、
  nboyer、takl、cpstak、lattice、paraffins）主要就吃在这个差别上。
- 真逃逸的分配仍是 5.0 倍（28.9 vs 5.8），装箱写数组 4.6 倍 — 这是分配路径本身（`RhpAllocate` 的线程上下文取值、
  `JIT_ByRefWriteBarrier`、随后的回收），与 §2 的 GC 配置对照一致。
- 反过来，普通函数调用与算术循环 cleat 已经不输甚至略赢 JVM（0.9x / 1.0x）：M12~M15 的 loopspec / rtspec /
  数值快路径到位了。这解释了 cleat 明显更快的 sum / sumfp / ack / fibfp / fib（C2 在这些形状上没能消掉装箱，
  sum 的 GraalVM 数字 0.64 s 对比 C2 的 6.56 s 就是同一件事）。

### 2. GC 只在一部分项上是主因

cleat 单次（秒），workstation 默认 对比 server + gen0 256MB；JVM 侧同项跑一次带 `-Xlog:gc` 统计 GC 时间占内部计时的比例。

| 基准 | cleat ws | cleat server+gen0 | Δ | JVM 总时长 | JVM GC 时间 | JVM GC 占比 |
|---|---:|---:|---:|---:|---:|---:|
| array1 | 24.04 | 8.95 | **-63%** | 6.56 | 3.44 | 52% |
| dynamic | 16.84 | 12.25 | -27% | 3.67 | 0.08 | 2% |
| fft | 2.24 | 1.70 | -24% | 0.81 | 0.04 | 5% |
| gcbench | 1.91 | 1.53 | -20% | 0.42 | 0.03 | 8% |
| nboyer | 10.53 | 8.64 | -18% | 5.79 | 0.44 | 8% |
| scheme | 18.11 | 17.05 | -6% | 4.90 | 0.05 | 1% |
| peval | 21.72 | 21.80 | 0% | 4.28 | 0.05 | 1% |
| cpstak | 18.19 | 18.24 | 0% | 4.62 | 0.07 | 2% |
| browse | 5.99 | 5.99 | 0% | 1.30 | 0.04 | 3% |
| puzzle | 7.15 | 7.26 | 0% | 2.04 | 0.02 | 1% |
| deriv | 9.94 | 10.39 | +5% | 3.39 | 0.07 | 2% |
| bv2string | 2.67 | 3.05 | +14% | 0.73 | 0.03 | 4% |

- array1 是唯一的 GC 主导项（连 JVM 自己也有 52% 时间在 GC）：500 × 两个 8MB 数组（LOH）+ 每轮一百万个装箱 long。
  改 GC 配置能到 8.9 s，但 JVM 仍 6.6 s — 剩下的差在分配本身（§1 的 4.6 ~ 5.0 倍）。与 notes/array1.md、
  notes/gc-config-and-tier0-two-machines.md 的结论一致（这两个旋钮仍不做默认值）。
- peval / cpstak / browse / puzzle / bv2string 对 GC 配置完全不动 → 它们的 3 ~ 5 倍差距是纯代码生成与调用/分配路径的成本，
  不是回收策略问题。
- JVM 侧这些项的 GC 占比只有 1 ~ 8%：JVM 的时间几乎全在计算本身，所以 cleat 与它的差距只能从单次分配与单次调用来解释。

### 3. 关掉 JVM 内联后，两边基本回到同一水平

JVM 各跑一次（秒），默认 对比 `-XX:+UnlockDiagnosticVMOptions -XX:-Inline`：

| 基准 | JVM 内联 | JVM 关内联 | cleat |
|---|---:|---:|---:|
| dynamic | 3.74 | 10.76 | 16.84 |
| browse | 1.31 | 7.00 | 5.99 |
| peval | 4.30 | 18.37 | 21.72 |
| array1 | 6.48 | 21.82 | 24.04 |
| scheme | 5.01 | 18.78 | 17.06 |
| takl | 14.47 | 63.15 | 17.21 |

- pair / 闭包密集型项在 JVM 关掉内联后掉到 cleat 的量级（browse、scheme、peval 与 cleat 已在 ±20% 内）：
  这些项 3 ~ 5 倍的差距基本等于 **C2 的内联收益**，不是 .NET 后端更差。cleat 的对应手段是把 `mcar` / `mcdr` /
  `set-mcar!` 这类小访问器在调用点内联（cleat-116 只对 eval 单元的小函数开了口子），以及 pair 的字段直读
  （deftype 字段绕过接口调用）。
- 例外是 dynamic（关内联 10.8 而 cleat 16.8）与 array1：dynamic 还吃 scheme-read 的字符串/符号处理，array1 还吃 GC。

### 4. takl 的 JVM 数字是双态，本表的 0.50x 不可信

- 本次批测 JVM takl 三次 34.77 / 37.29 / 35.02，单复跑三次 34.46 / 37.35 / **12.05**；cleat 三次稳定 17.21 ~ 17.71。
  cleat-119 同机同源码的 JVM 最小值是 14.20 — 也就是说 JVM 现在大部分时候落在慢态。takl 的 `mas` / `shorterp`
  在 C2 下有明显的取舍差异（快态约 12 s，慢态约 35 s），推测是热点方法的编译时机 / 去优化路径不同。
- 按快态算 takl 是 1.43x（cleat 更慢），几何平均从 1.61x 变成 **1.64x**。除 takl 外其余 55 项的 JVM 数字与
  cleat-119 都在 ±5% 内，说明本轮批测本身稳定。

### 5. cleat 侧相对 cleat-119 的变动（同机同 JVM，各自 3 次取最小）

- 大改进（M14/M15 的成果）：ctak 48.52 → 1.01、fibc 43.26 → 1.85（语言层 escape* 的 marker 协议替代异常，见 cleat-125）、
  destruc 6.64 → 3.12、mbrotZ 3.61 → 2.18、browse 7.96 → 5.76、sum 0.90 → 0.72。
- 需要复查的回退：parsing 8.80 → 10.89（+24%）、pi 0.96 → 1.10（+15%）、dynamic 14.47 → 16.71（+15%）、
  mbrot 0.48 → 0.61（+28%）、equal 2.75 → 3.03、deriv 9.01 → 9.89、cpstak 16.62 → 18.30（各 +8 ~ 10%）。
  这些项对 GC 配置不敏感（§2），更像 M14/M15 的入口 / 特化规则改动带进来的副作用，建议按 parsing、pi、mbrot 三项做 A/B。

### 6. 口径提醒

- 远程机只有 .NET 运行时 10.0.0（本地 10.0.12）：JIT / GC 的服务更新差集会体现在这些数字上，跨机对比时先对齐 runtime 版本。
- JVM 的 min-of-3 对双态项（takl）不可靠；要比较这类项应分别取快态或报告区间。

## M15 优化建议（2026-10-20，基于上面的归因与 `bench/r7rs/src/shapes2.cljc` 探针）

### 0. 单个操作的对照（ns/次，各 1×10^7，3 次取最小；`java -Xss8m` / `dotnet cleat.dll`）

| 形状 | cleat | JVM | cleat / JVM |
|---|---:|---:|---:|
| `(= \a \a)`（char 常量比较） | 8.2 | 0.7 | **11.7x** |
| `(nth (list 1 2 3) 1)` | 15.2 | 0.7 | **22x** |
| `(first "abc")` | 41.5 | 9.8 | 4.2x |
| `(seq "abc")` | 13.0 | 4.2 | 3.1x |
| `(nth "abc" 0)` | 4.3 | 0.9 | 4.8x |
| `(keyword (str 7))` | 52.5 | 18.5 | 2.8x |
| `(mcons 1 2)`（工厂函数建 pair） | 7.2 | 0.8 | 9.0x |
| `(first xs)` / `(count xs)` / `(= kw :abc)` | 6.1 / 1.4 / 1.6 | 2.3 / 0.8 / 0.5 | 2.7x / 1.8x / 3.2x |
| `@volatile` | 5.0 | 1.6 | 3.1x |
| `(= 1 1)` / 三参普通调用 / `volatile-set!` | 0.2 / 0.5 / 1.2 | 0.4 / 0.8 / 1.7 | **cleat 更快** |

（探针：`dotnet …/cleat.dll -I bench/r7rs/src -m shapes2`、`java -Xss8m -cp bench/r7rs/src:$(clojure -Spath) clojure.main -m shapes2`；
分配构成用 `dotnet run tools/alloc.cs -- --top 8 -I bench/r7rs/src -m r7rs.<名字>`，工作目录须是仓库根，否则找不到 boot 镜像。）

### 1. 优先级 P0：核心序列访问器与 `=` 的快路径（改动都在 analyzer / codegen / RT，成本最低、覆盖最广）

- **`nth` 对 seq**：15.2 ns vs JVM 0.7 ns，是表里最差的一项。`nth` 目前走通用的 seq 遍历 + not-found 处理；
  按 cleat-106（数组静态决议）与 `not` 的 `:inline` 同样的做法，给 2 / 3 元 `nth` 加 `:inline` 与具体类型（PersistentList /
  PersistentVector / String / chunked seq）的直取快路径。
- **`seq` / `first` 对 String**：每次调用新建 `clojure.lang.StringSeq`（探针里 457 MB / 1×10^7，占 browse 分配的一部分）。
  串是不可变的：按（串, offset）复用已建的 seq，或换成基于索引的 struct / deftype 序列，避免每次堆分配。
- **char / 其他基元常量**：`(= \a \a)` 8.2 ns、browse 里 `System.Char` 占分配 28%。已有 `CharBox`（RT.nth 用），
  但常量加载点仍在装箱 —— 把基元常量的装箱放进常量池 / 静态字段（与 `(:inline (unchecked-add …))` 的整型常量折叠同一路，
  `(= 1 1)` 已经是 0.2 ns），并给 `=` / `==` 加已知基元类型的内联比较。
- **`keyword` / `symbol` 的 intern**：52.5 ns 与 `clojure.lang.Symbol` 占探针分配 42%。命中已有实例时不应先建对象：
  intern 表按字符串比较直接返回，未命中才分配；`(str <long>)` 用专门的整数→串路径，避免中间 String + 哈希。
  命中 browse / scheme / dynamic / lattice / paraffins 的 `generate-symbol`。
- 收益面：这些形状出现在除纯算术外的几乎每一项里，是当前 1.61x 差距的最大来源。

### 2. 优先级 P1：pair / 闭包的分配与访问（对应 §1 的 9 ~ 12 倍）

- **打通跨 ns 的 deftype / definterface 生成类在编译期的决议**（实测：`(:import (r7rs.util MPair))`、`^IMPair` 提示、
  `(MPair. 1 2)` 在 cleat 上全部 "Unable to resolve classname"，JVM 正常）。不修这个，用户代码只能走工厂函数 +
  接口访问器（`mcons` / `mcar`），既多一层调用（7.2 vs 0.8 ns）又拿不到字段直读，也使第 3 条的内联无从下手。
  与 cleat-042 的内联接口文件、cleat-106 的类型决议是同一套机制的延伸。
- **工厂 / 构造路径**：确认 deftype 构造发的是 `newobj` + 字段直存，而不是经辅助方法 / 参数数组
  （alloc.cs 里 `System.Object[]` 在 peval 占 4.0%、browse 占 1.8%，需要确认其中多少是调用参数数组）。
- **小访问器（mcar / mcdr / set-mcar! / first / rest）的调用点内联跨编译单元**：现在内联接口文件只在 eval 单元注册
  （`register-eval-inlines!`，镜像 / 包构建不启用），所以 r7rs 用到的普通 ns 里的访问器都不会被内联；
  `clojure.core/not` 加 `:inline` 让 tak 从 3.7 → 1.9 s，说明这一层的收益在 r7rs 尺度上是被验证过的。
  先给 `restrict-loop-inlines?` 的保守条件做一次开关对照，再决定是否放开。
- **每调用重建的闭包**：peval 的 `ref_count$rc$fn__20` 占该项分配 17%（1.02 GB）。捕获变量全为静态 / 未被改写时
  把闭包提升成单例（lambda 提升 + 常量闭包缓存），能同时削掉分配与随后的回收。
- 长期：不逃逸对象的标量提升 / 栈分配（.NET 没有 C2 的 escape analysis）——这是拿回 §1 那 12 倍的唯一路子，
  受益项是 mbrotZ、destruc、sum、以及所有 pair 密集项；难度也最大，建议单独开任务做可行性 spike。

### 3. 优先级 P2：GC 与部署形态

- array1 / gcbench / nboyer / fft / dynamic 在 server GC + gen0 256MB 下 -18 ~ -63%（§2 的表）。照 designs/22 §6 的结论
  **不改默认值**，但值得把它做成可声明的 profile（例如 `cleat.edn` 的 `{:gc {:mode :server :gen0-budget "256MB"}}`
  或 `--gc-profile batch`），让批处理 / 服务化部署一行开启，基准脚本也统一用它。
- 分配速率本身：browse 1.05 GB/s、array1 0.68 GB/s。除减少分配（上面两条）之外，大数组（LOH）的初始化写入
  走无屏障路径（数组填充时用 `spans` / 直接指针而非常规 `stelem.ref`）值得评估，array1 的 Object[] 占它分配的 40%。
- 先对齐 runtime 版本（远端只有 10.0.0）再谈跨轮数字比较。

### 4. 优先级 P3：先还的债

- parsing +24%、mbrot +28%、pi / dynamic +15%、equal / deriv / cpstak +8 ~ 10%（相对 cleat-118，§5）：
  这些项对 GC 配置不敏感，怀疑 M14/M15 的入口选择与特化规则（cleat-138 的 RuntimeSigTable 兼容条目 / 负表 / LRU）
  带进来的副作用。建议对 parsing、pi、mbrot 三项做 `CLEAT_NO_OPT` 开关矩阵 + `git bisect 6ed0da4..65d407d`。
- 测量脚本：双态项（takl）要报告区间而不是 min-of-3；把 `micro.cljc` / `shapes2.cljc` 的对照纳入每次里程碑收尾。

### 5. 复核修正：`shapes3.cljc` 推翻了上面两条论据（同日）

同一 ns 内"直调 vs 经 defn 包装 vs 直接调 RT"的对照（ns/次，3 次取最小）：

| 形状 | cleat | JVM | 说明 |
|---|---:|---:|---|
| 接口方法直调 `.getCar` | 1.3 | 0.7 ~ 1.9 | |
| 同一方法经 defn 包装 | 3.1 ~ 3.5 | 0.6 ~ 0.7 | **包装在 cleat 上多 2 ns，在 JVM 上被内联掉** |
| deftype 直接 `new`（死结果） | 5.1 ~ 5.6 | 0.4 ~ 0.7 | JVM 侧是 EA + LICM 的产物，不可比 |
| deftype 经 defn 包装 `new` | 5.1 ~ 5.5 | 0.6 ~ 0.7 | 同上 |
| `(nth xs 1)` | 15.7 ~ 16.9 | 0.7 ~ 0.8 | JVM 侧被提出循环（LICM） |
| `(. clojure.lang.RT (nth xs 1))` | 12.4 ~ 16.9 | 0.6 ~ 0.8 | **与通用调用同价 → 不是分派问题** |
| `(first xs)` / RT 直调 | 1.5 ~ 1.6 / 0.6 ~ 0.7 | 2.0 ~ 2.2 / 1.8 ~ 1.9 | cleat 略快 |
| `(seq "abc")` / RT 直调 | 25.3 ~ 27.4 / 12.3 ~ 13.3 | 7.8 / 0.5 ~ 0.6 | |

**要撤回的**：

- §0 表里 `nth` 的 22x、char 常量比较的 11.7x、`first` / `seq` on String 的 3 ~ 4x 这些"倍数"不成立：
  JVM 那侧的结果是死值 / 循环不变量，C2 做了 LICM + 标量替换，等于把整个调用提出循环。
  cleat 的数字才是每次调用的真实成本。这类探针只能论证"cleat 这一项的绝对成本是多少"，不能论证倍数。
- "分配慢 9 ~ 12 倍"同样撤回：cleat 建一个两字段 deftype 是 5.1 ~ 5.6 ns，与 micro.cljc 里 JVM **真逃逸**
  分配（5.8 ns）同量级；§1 表里 12x 的差来自 JVM 把不逃逸的分配整个消掉，不是 .NET 的分配路径慢。
- §1 里 `nth` / `first` 加 `:inline` 的建议收益有限：RT 直调与通用调用同价（16.9 vs 15.2），成本在本体
  （`RT.nth` 对非 Indexed 走 `NthFromRef` → `NthFrom` 的类型判断链，那个 ref 转发是 cleat-117 为绕开
  tier-0 的形参活性问题加的），不在调用点。

**仍然成立、且有真实基准证据的**：

- char 常量在每次求值时装箱：browse 的真实分配里 `System.Char` 占 28%（1.84 GB），来源是 `my-match` 里
  `(= (nth (name …) 0) \?)` / `\*` 的字面量。`CharBox` 只覆盖了 `RT.nth` 的返回路径，没覆盖常量加载。
- `first` / `seq` 对 String 每次新建 `StringSeq`：cleat 侧绝对成本 18 ~ 27 ns（`RT.seqFrom` 对 string 直接
  `StringSeq.create`）。给 CharSequence 加直读快路径是语义保持的改动，受益 slatex / string / wc / read1 /
  nucleic 与 dynamic 的 scheme-read。
- `keyword` / `symbol` intern：cleat 侧 52.5 ns，探针里 `clojure.lang.Symbol` 占分配 42%；browse 的
  `generate-symbol`（`(keyword (str @current-gensym))`）是真实热路径。注意 JVM 的 intern 也先建 Symbol，
  所以这条按"绝对成本 + 分配量"论证，不按倍数。
- 包装调用比直调贵约 2 ns（3.4 vs 1.3），JVM 侧为 0：从 §1 P1 提到 P0 —— pair 密集项每轮有几次到十几次
  访问器调用，累计是几十 % 的时间，修它需要打通跨编译单元的小函数内联（配合跨 ns 的 deftype 类决议）。
- GC 配置（array1 -63% 等）、takl 双态、parsing / mbrot / pi 的回退排查：不受这次复核影响。

**修正后的优先级**：P0 = char 常量装箱 + String 的 seq / first 直读 + 访问器内联（含跨 ns deftype 决议）+
keyword intern；P1 = `RT.nth` 非 Indexed 路径的类型判断链与 `NthFromRef` 转发、每调用重建的闭包提升
（peval 17%）、变长调用的参数数组；P2 = GC profile 与 LOH 初始化；P3 = 回归排查 + 测量口径
（"探针必须让结果参与数据依赖，否则 JVM 侧数字不可用"这条要写进 run.py / 探针的注释）。
