# cleat-108 推断的原始用途穿过分支汇合：if / let / begin 尾位置里的引用算原始用途（cleat-107 原因 4）

- 里程碑：M11　依赖：cleat-107　状态：未开始
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 复现：`(loop [i 0 n 0] … (recur (inc i) (if c (inc n) n)))` 的 n 推断为 l64 却存成 object（Lcil 转储），每轮装箱。
2. infer-walk 的 Raw：穿过 if（两支）、let / begin（体）的尾位置，引用算原始用途；codegen 的原始发射要能处理这些位置（cg-raw 对 if / let / begin）。
3. 语义用例开 / 关对照（`CLEAT_NO_OPT` 新开关 `joinraw`）。

## 验收标准
- quicksort、primes、diviter 等受影响项与 bench/ 前后数字；e5 一类循环接近有提示的。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
