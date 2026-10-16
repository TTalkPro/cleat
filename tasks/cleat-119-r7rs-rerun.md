# cleat-119 M11 收尾：r7rs 56 项与 bench/ 全部重测，更新 cleat-107 的表与 designs/21

- 里程碑：M11　依赖：cleat-118　状态：完成（2026-10-16）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. linux01 已不可用：在 david@192.168.3.111（4 vCPU / 15 GB，dotnet 在 ~/.dotnet，`ssh` 后 `export PATH=$HOME/.dotnet:$PATH`）上两边各 3 次，compare.py。
   JVM 两种都测并在表头注明：OpenJDK 25.0.4（HotSpot C2，该机默认 `java`）与 GraalVM CE（cleat-107 ~ 117 的"JVM"列是 GraalVM）。
   116 之前 linux01 上的数字标为"历史，不可直接比较"。
2. 更新 designs/21 §1 的倍数与剩余慢区。

## 结果（2026-10-16，david@192.168.3.111，cleat-118 = 6ed0da4）
- r7rs 56 项，cleat / OpenJDK C2 / GraalVM 各 3 次（表见 [notes/r7rs-performance.md](../notes/r7rs-performance.md) 末节）：
  几何平均（最小值）**cleat / C2 1.88x、cleat / GraalVM 2.05x**；中位数 1.86x / 2.02x；GraalVM / C2 0.92。
- 对 C2 比 JVM 快的 8 项：sum、ack、sumfp、lattice、fibfp、pi、fib、cat。
- M11 的演进（linux01、对 GraalVM、cleat 单次）：107 4.52x → 109 3.74 → 113 3.26 → 114 2.64 → 116 2.29 → 117 中途 2.27；
  换到 192.168.3.111 后：117 对 C2 1.85x / 对 GraalVM 1.99x（单次）、119 1.88x / 2.05x（三次取最小）。两台机器、两种口径的数字不可直接比较。
- 剩余慢项（对 C2 ≥ 3x）与原因见 designs/22：fibc / ctak（异常）、browse、peval、gcbench、dynamic、cpstak、array1、mbrotZ、puzzle、bv2string、scheme、ray。

## 验收标准
- 几何平均与各项数字写进任务书。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
