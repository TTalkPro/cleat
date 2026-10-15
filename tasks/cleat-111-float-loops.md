# cleat-111 浮点循环：从对象数组 / 向量取出的 double 与 Num 的覆盖范围（cleat-107 原因 3，先量化）

- 里程碑：M11　依赖：cleat-110　状态：未开始
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 对 fft、ray、sumfp、simplex 做 Lcil 转储与分配采样，找出每轮的装箱点。
2. 按量化结果决定：数组元素取出后的 Num 表示、let 变量的 double 推测、generic 算术里 double 快路径等。
3. 不值得的记录数字。

## 验收标准
- fft、ray、sumfp、simplex、nucleic 前后数字。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
