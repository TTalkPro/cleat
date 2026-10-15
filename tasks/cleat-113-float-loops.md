# cleat-113 浮点循环：从对象数组 / 向量取出的 double 与 Num 的覆盖范围（cleat-107 原因 3，先量化）

- 里程碑：M11　依赖：cleat-112　状态：进行中（2026-10-16 部分完成）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 对 fft、ray、sumfp、simplex 做 Lcil 转储与分配采样，找出每轮的装箱点。
2. 按量化结果决定：数组元素取出后的 Num 表示、let 变量的 double 推测、generic 算术里 double 快路径等。
3. 不值得的记录数字。

## 验收标准
- fft、ray、sumfp、simplex、nucleic 前后数字。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。

## 进展（2026-10-16）
- sumfp 的 perf / JIT 代码：循环变量是 Num，`(< i 0.0)` 等每轮仍走 Numbers.ops / combine / lt，且有分配——Num 并不如 cleat-102 量的那样与原始 double 同级。
  循环推测（cleat-103）不再排除 Num 变量（只排除已定成原始类型的）：sumfp 1.87 → 1.08 s。
- sum 的快份里 `Numbers.add(long, long)` 没被 JIT 内联：long / double 的 add / minus / inc / dec / 比较加 AggressiveInlining；`multiply(long, long)` 改用
  `Math.BigMul` 取高 64 位判溢出（原来每次乘法一次除法），结果相同（含 MinValue * -1、恰为 MinValue 的乘积）。sum 2.81 → 0.74 s（JVM 0.43）。
- 未做：fft（13 倍）、ray（11 倍）、simplex、nucleic、pnpoly——从对象数组 / 向量取出的 double 与 generic 算术，下一步。
- 语义：`data/loopspec_semantics.clj` 加了 Num 变量的推测用例（开 / 关逐字相同）；`data/boxcache_semantics.clj` 加了乘法溢出的边界（与 JVM 逐项一致）。
- 验证（linux01，与 110 ~ 113 一起，一次完整流程）：种子不动点；Core 117 / Host 263 通过（1 个按环境跳过）；语料门禁 521 行；两套基线不变；
  bench 在噪声内（lazy_chain 48 → 16 ms）。r7rs 56 项 cleat 侧（各一次，对 JVM）：几何平均 3.74（109 后）→ 3.51（110 ~ 112 第一部分）→ **3.26**（含 112 第二部分与 113）。
- 这几个任务的改动在 codegen.clj、Util.cs 等文件里交织，按任务拆分要多跑几轮验证，所以一起验证、一个提交（与"每个任务一个提交"不同，有意为之）。
