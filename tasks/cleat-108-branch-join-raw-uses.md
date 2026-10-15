# cleat-108 推断的原始用途穿过分支汇合：if / let / begin 尾位置里的引用算原始用途（cleat-107 原因 4）

- 里程碑：M11　依赖：cleat-107　状态：完成（2026-10-15）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 复现：`(loop [i 0 n 0] … (recur (inc i) (if c (inc n) n)))` 的 n 推断为 l64 却存成 object（Lcil 转储），每轮装箱。
2. infer-walk 的 Raw：穿过 if（两支）、let / begin（体）的尾位置，引用算原始用途；codegen 的原始发射要能处理这些位置（cg-raw 对 if / let / begin）。
3. 语义用例开 / 关对照（`CLEAT_NO_OPT` 新开关 `joinraw`）。

## 验收标准
- quicksort、primes、diviter 等受影响项与 bench/ 前后数字；e5 一类循环接近有提示的。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。

## 结果（2026-10-15）
- 实现：infer-walk 的 Raw 穿过 if（两支）与 begin（最后一项）；cg-raw 对这两种节点直接原始发射（`cg-raw-join!`，各支原始求值、汇合处不装箱，局部变量清除按分支同 cg* 的 if）。
  let 不穿过（原始发射要处理绑定，留待需要时）。开关 `CLEAT_NO_OPT=joinraw`。
- e5 一类（`(recur (inc i) (if c (inc n) n))`，1000 万轮）36 → 17 ms；循环体里只剩返回值处一次装箱。
- 语义：`tests/Cleat.Host.Tests/data/joinraw_semantics.clj` 开 / 关逐字相同；`ClojureCompilerTests.JoinRawUses` 另查 Lcil。
- 验证（linux01）：种子不动点；Core 117 / Host 259 通过（1 个按环境跳过）；门禁 522；两套基线不变；bench 在噪声内。
- **r7rs 没有变化**（56 项 cleat 侧各跑一次，几何平均 4.52 → 4.59，在一次运行的噪声内）：这个模式在 r7rs 的热路径上不常见；cleat-107 里把 quicksort
  归到这一点是错的——它的热循环经 `less?` 闭包调用、比较 aget 出来的对象。

## 重新定位（剖析与对照，2026-10-15）
- `dotnet-trace` 的线程时间采样（EventPipe，在安全点上采样）把大量时间归到 `CastHelpers.ChkCastInterface`（browse 17%）；对照实验（同一调用经 Var 与直接 call invokeStatic）只差约 6%，
  采样在运行时辅助函数上有偏差，不能直接按它排优先级。
- 分配量才是大头：nqueens(12) 一次 cleat 1580 ms、分配 1289 MB（103 次 gen0），JVM 365 ms、163 MB。nqueens 的数都是小整数，JVM 的 `Long.valueOf` 缓存 -128 ~ 127，
  装箱不分配；cleat 每次装箱分配 24 字节。M11 计划据此调整（designs/21 §1）：先做小整数装箱缓存。
