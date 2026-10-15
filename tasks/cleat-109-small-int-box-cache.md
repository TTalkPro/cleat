# cleat-109 小整数装箱缓存：Int64 的装箱走预先分配的缓存（codegen 的 box、Num.Box、运行时 Numbers.num 等）

- 里程碑：M11　依赖：cleat-108　状态：未开始
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 依据：nqueens(12) 一次 cleat 分配 1289 MB、JVM 163 MB——JVM 的 `Long.valueOf` 缓存 -128 ~ 127，小整数装箱不分配；cleat 每次装箱 24 字节。
2. `Cleat.Core.Runtime` 加缓存（范围待量化，至少 -128 ~ 127，可放宽到 -128 ~ 1023：Clojure 不保证数的 identical?，与 JVM 不同的只是 128 以上也相同）；
   codegen 的 `box System.Int64` 改成调用它（JIT 内联：一次范围比较加数组读取），`Num.Box`、ClojureCLR 运行时里返回装箱 long 的路径（`Numbers.num(long)`、
   LongOps 的结果、`RT.box` 一类）同样改走缓存。
3. 量化分配与时间（nqueens、takl、primes、quicksort、`bench/`）；语义（`identical?` 对小整数为真，与 JVM 相同；其余不变）。

## 验收标准
- r7rs 受影响项与 `bench/` 前后数字及分配量；种子不动点。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
