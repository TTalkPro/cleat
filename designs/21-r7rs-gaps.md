# 21 r7rs 对比的慢区：M11 计划

> 依据 cleat-107（2026-10-15）：ecraven/r7rs-benchmarks 56 项的 Clojure 移植，linux01 上 cleat 对 JVM Clojure 几何平均 4.52 倍。
> 各项数字、子代理的最小复现见 [tasks/cleat-107](../tasks/cleat-107-r7rs-vs-jvm.md)。本文把慢区原因对应到任务，按影响面排序。

## §0 原则
- 每个任务先用最小复现确认原因（Lcil 转储 / 分配采样），改完在 linux01 上重测受影响的 r7rs 项（`bench/r7rs/run.py -r 3`）与 `bench/`，写进任务书。
- 语义不变：开 / 关对照（`CLEAT_NO_OPT` 的开关或改前构建）输出逐字相同；种子不动点、门禁、两套基线、全量不变。
- 宿主差异（.NET 异常的 throw / catch 走栈：ctak 94、fibc 23）不在 M11：只作逃逸的 try / throw 编成跳转在 Clojure 代码里罕见。

## §1 原因 → 任务

（cleat-108 之后按剖析与对照重排：cleat-107 里子代理的复现定位了现象，但 r7rs 热路径上的主因是装箱分配与跨函数调用，见 tasks/cleat-108 的"重新定位"。）

| 原因 | 依据 / 典型项 | 任务 |
|---|---|---|
| 分支汇合：`(recur (if c (inc n) n))` 的 n 推断为 long 却存成 object | e5 36 → 17 ms；r7rs 无变化（热路径上少见） | cleat-108（完成） |
| 小整数装箱每次分配（JVM 的 Long 缓存 -128 ~ 127 不分配） | nqueens(12) cleat 1289 MB / JVM 163 MB；所有通用数值代码 | cleat-109 |
| 经 Var 调用用户函数：castclass IFn、接口分派、invoke 转发（核心函数已有已知调用） | browse、takl、deriv、nqueens | cleat-110 |
| 运行时通用路径：`Util.equiv(Object, long)`（nqueens 的 equiv 占比高）、`empty?` 的 `counted?` 接口检查、`(byte x)` 与 `byte[]` 的 aset、符号相等 | nqueens、deriv、bv2string、peval | cleat-111 |
| 宿主调用 / deftype 原始字段的结果类型不进推断（`(object-array n)` 的局部、`^double` 字段的算术） | array1、scheme、matrix、mbrotZ 78 | cleat-112 |
| 浮点循环的装箱（数组取出的 double、generic 算术） | fft 16、ray 15、sumfp、simplex | cleat-113（先量化） |
| eval 代码由 RunAndCollect 发射：不分层、没有动态 PGO（JVM 内联缓存的对应物） | 编成包加载时同一份代码快 20% ~ 37% | cleat-114（几何平均 3.26 → 2.64） |
| 无类型算术的 Numbers.ops / combine / 虚调用、Var 读取的常量表与 castclass、symbol? 的类型检查 | cpstak、browse、scheme | cleat-115 |
| eval 的小函数没有接口文件、不内联；循环后内联的 double 装箱让 JIT 把 double 变量放在栈上；Cons 的 first / next 走 CastHelpers | fft、sumfp、ray、lattice、nboyer | cleat-116（与 115 一起：几何平均 2.64 → 2.29） |
| 镜像与包的分层编译、tier-0 生命周期（^:once、nth、转发方法、自递归规则）、`not` 的 :inline | 全部（惰性序列头不再被抓住） | cleat-117（192.168.3.111：对 C2 1.85x、对 GraalVM 1.99x） |
| 类型化调用点 | browse、destruc、deriv | cleat-118 |
| 全部重测（192.168.3.111，各 3 次：对 OpenJDK C2 1.88x、对 GraalVM 2.05x） | | cleat-119（完成） |

## §2 顺序
108 → 109 → 110 → 111 → 112 → 113 → 114 → 115 → 116 → 117 → 118。109（装箱缓存）与 110（已知调用）影响所有通用代码，先做；111 是 C# 侧快路径；112 / 113 是"类型交给推断"；114 改动最大，放最后。
剖析注意：`dotnet-trace` 的线程时间采样在安全点上取样，会把时间多算到运行时辅助函数（如 ChkCastInterface）上；排优先级以对照实验与分配量为准。
