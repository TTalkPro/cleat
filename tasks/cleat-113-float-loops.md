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

## 进展（2026-10-18：持久向量尾部 nth）
- pnpoly / ray / nucleic 的热访问主要是 `PersistentVector.nth`。原来即使访问尾部也要走 `ArrayFor`，其中按树高循环找节点；AOT 后这个循环较难内联，也挡住 `nth` 本身的内联。
- `nth(int)` 先处理 `0 <= i < count` 且已在尾部的访问，直接读 `_tail[i & 0x1f]`；越界仍交给 `ArrayFor`，异常类型与消息不变。曾试过再加 `AggressiveInlining`，pnpoly 没有额外收益，已回退。
- linux01 本地 A/B（3 轮取 min，只计内部时间）：pnpoly 3.1432 → 2.6294 s（-16.3%）；simplex 5.2842 → 5.1656 s、nucleic 3.5223 → 3.4614 s、ray 2.6678 → 2.6473 s、fft 1.5068 → 1.4706 s，轻度改善或在噪声内；sumfp 0.2363 → 0.2380 s，噪声内。记录在 `bench/r7rs/results/cleat113-{base,patchA,patchB}.jsonl`（B 是已回退的内联实验）。
- Release 构建通过；本地全量 391 项 390 通过 / 1 跳过 / 0 失败。数组与装箱语义数据开 / 关输出逐字相同。
- 剩余热点：fft 的 `vector-ref` / `vector-set!` 虽然函数体已是 `object[]` 访问，但热循环里的调用与索引装箱仍值得做运行模式的 Lcil / JIT 对照；后续优先区分包程序集和 eval 加载两种模式的差异，再做对象数组元素到 double 推断的泛化机制。
