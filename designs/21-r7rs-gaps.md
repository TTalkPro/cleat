# 21 r7rs 对比的慢区：M11 计划

> 依据 cleat-107（2026-10-15）：ecraven/r7rs-benchmarks 56 项的 Clojure 移植，linux01 上 cleat 对 JVM Clojure 几何平均 4.52 倍。
> 各项数字、子代理的最小复现见 [tasks/cleat-107](../tasks/cleat-107-r7rs-vs-jvm.md)。本文把慢区原因对应到任务，按影响面排序。

## §0 原则
- 每个任务先用最小复现确认原因（Lcil 转储 / 分配采样），改完在 linux01 上重测受影响的 r7rs 项（`bench/r7rs/run.py -r 3`）与 `bench/`，写进任务书。
- 语义不变：开 / 关对照（`CLEAT_NO_OPT` 的开关或改前构建）输出逐字相同；种子不动点、门禁、两套基线、全量不变。
- 宿主差异（.NET 异常的 throw / catch 走栈：ctak 94、fibc 23）不在 M11：只作逃逸的 try / throw 编成跳转在 Clojure 代码里罕见。

## §1 原因 → 任务

| 原因（cleat-107 编号） | 典型项 | 任务 |
|---|---|---|
| 4 分支汇合：`(recur (if c (inc n) n))` 的 n 推断为 long，但 if 两支里的引用算装箱用途，装箱用途多于原始用途就存成 object | quicksort、primes、diviter、计数器 / 累加器循环 | cleat-108 |
| 5 `(= x 0)` → `Util.equiv(Object, long)` 走通用路径（23 ns 对 3 ns），`zero?` 等 11 ns；8 `(byte x)` 77 ns、`byte[]` 的 aset 108 ns；9 符号 `=` 27 ns | maze、bv2string、peval、dynamic、scheme | cleat-109（C# 运行时快路径） |
| 6 `(object-array n)` 等宿主调用的结果类型不交给无提示局部，其后 aset 走运行时决议；2 deftype 原始字段读取的类型不进推断，算术装箱 | array1、scheme、matrix、mbrotZ 78 | cleat-110 |
| 3 从对象数组 / 向量取出的 double、浮点循环的装箱（Num 只覆盖形参与循环变量） | fft 16、ray 15、sumfp 8.7、simplex 8、nucleic 6 | cleat-111（先量化） |
| 7 小函数跨调用的装箱与虚调用（可变序对访问 12 ns 对 1.5 ns）；designs/20 §6 的类型化调用点 | browse 15、destruc、lattice、puzzle、takl | cleat-112 |
| 全部完成后重测 56 项 | | cleat-113 |

## §2 顺序
108 → 109 → 110 → 111 → 112 → 113。108 影响面最广且只动推断；109 是 C# 侧、不动编译器；110 / 111 都是"类型交给推断"；112 改动最大，放最后。
