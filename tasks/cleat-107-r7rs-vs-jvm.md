# cleat-107 r7rs-benchmarks 的 Clojure 移植：cleat 对 JVM Clojure

- 里程碑：M10 之后（测量）　依赖：cleat-105　状态：完成（2026-10-15）
- 来源：skeg iron-060 用的 ecraven/r7rs-benchmarks（57 项）；运行方式照研究仓库 `research/scheme/src/bench/skeg-vs-chez/tools/run.py`

## 目标
M10 优化之后，用 skeg 对比 Chez 的同一组基准量出 cleat 与 JVM Clojure 的差距：把各项移植成 Clojure（`.cljc`，同一份源码两边运行），
在 linux01 上各跑若干次取最小值，得到每项的 cleat / JVM 倍数与几何平均，找出慢区的原因，决定后续任务（包括 designs/20 §6 的"类型化调用点"候选）。

## 做法
- `bench/r7rs/src/r7rs/<名字>.cljc`：每项一个命名空间，`-main` 按原版顺序从标准输入读 inputs，调用 `r7rs.common/run-r7rs-benchmark`（原版 common.scm 的 hide 与计时，
  输出同样的 `+!CSVLINE!+` 行，只计程序内部时间）；宿主差异集中在 `r7rs.util`（读者条件 `:clj` / `:cljr`）。
- 移植保持算法与数据结构的形状：不可变表 → Clojure 表，`set-car!` 一类 → `util` 的可变序对，`vector-set!` → 对象数组，只作逃逸用的 call/cc → 异常（`call-with-escape`）。
  不加原版没有的类型提示（原版用 fl / fx 专用运算处才加）。
- `bench/r7rs/run.py <cleat|clojure> [-r 次数] [名字…]`：工作目录是 r7rs-benchmarks 仓库，cleat 用仓库的 Release 构建（`-I bench/r7rs/src -m r7rs.<名字>`），
  JVM 用 `bench/r7rs/deps.edn`（Clojure 1.12.4）的类路径 `java -cp … clojure.main -m`，默认 JVM 选项；`compare.py` 汇总倍数与几何平均。
- JVM 加 `-Xss8m`：与 .NET 主线程的栈相同（Linux 上 8 MB 的 ulimit）；equal 有 5000 层非尾递归，JVM 默认 1 MB 的栈溢出。ack 另在 JVM 侧开一个大栈的线程（`#?(:clj …)`）。
- 逃逸续延（`util/call-with-escape`）抛预先分配的同一个异常：JVM 构造异常时记录调用栈，每次新建时 ctak 在 JVM 上超过 900 s、fibc 628 s，测到的只是这个开销；改后 0.8 s / 2.5 s。
- 不移植：compiler（11198 行的 Scheme 编译器，移植量与收益不成比例）。其余 56 项全部移植（6 个子代理分组完成，各自在两边核对结果）。
- 读取器差异：Scheme 的 `#t` / `#f`、`#(…)` 向量、`#\ ` 字符、`.5` 这类数在 Clojure 读不了——inputs 里有这些的（pnpoly、simplex、maze、read1、dynamic、equal）在计时外转换，
  dynamic 的数据文件原版在计时内读，移植自带一个小的 Scheme reader（同样在计时内）。
- JVM 上的反射：`(aset ^objects a i 原始数)` 在 JVM 上找不到重载、退回反射（simplex 39.7 s、triangl 160 s、maze 73 s），移植里用私有的 `vector-set!` 或 `(num x)` 装箱避开；
  JVM 侧 `*warn-on-reflection*` 下全部无警告。两边用的是同一份源码。

## 验收
- 移植的每项在两边都输出正确结果；linux01 上每项每边至少 3 次，表格与几何平均写进本任务书；慢区（倍数最大的若干项）给出原因与对应的后续任务。

## 结果（2026-10-15，linux01：Debian 13、16 核；cleat f8d9001 的 Release 构建、.NET 10.0.401；GraalVM CE 25.0.2、Clojure 1.12.4）

每项两边交替各跑 3 次（`bench/r7rs/run.py`），取最小值；只计程序内部时间。56 项全部两边结果正确。

| 基准 | cleat (s) | JVM (s) | cleat / JVM | 备注 |
|---|---:|---:|---:|---|
| ack | 3.810 | 7.481 | 0.509 |  |
| array1 | 16.985 | 1.261 | 13.465 |  |
| browse | 12.766 | 0.877 | 14.554 |  |
| bv2string | 10.112 | 0.373 | 27.131 |  |
| cat | 3.592 | 5.692 | 0.631 |  |
| chudnovsky | 0.539 | 0.318 | 1.694 |  |
| conform | 13.273 | 4.463 | 2.974 |  |
| cpstak | 22.858 | 2.892 | 7.905 |  |
| ctak | 56.914 | 0.607 | 93.750 |  |
| deriv | 8.843 | 2.123 | 4.165 |  |
| destruc | 13.699 | 1.806 | 7.586 |  |
| diviter | 10.152 | 2.808 | 3.616 |  |
| divrec | 17.340 | 6.830 | 2.539 |  |
| dynamic | 15.778 | 3.392 | 4.651 |  |
| earley | 11.432 | 2.696 | 4.241 |  |
| equal | 4.630 | 1.533 | 3.020 |  |
| fft | 5.845 | 0.365 | 16.024 |  |
| fib | 3.714 | 3.803 | 0.977 |  |
| fibc | 41.511 | 1.796 | 23.113 |  |
| fibfp | 0.627 | 0.519 | 1.207 |  |
| gcbench | 2.067 | 0.292 | 7.085 |  |
| graphs | 15.657 | 3.101 | 5.048 |  |
| lattice | 14.675 | 10.118 | 1.450 |  |
| matrix | 10.018 | 2.342 | 4.278 |  |
| maze | 7.701 | 0.702 | 10.971 |  |
| mazefun | 9.142 | 5.522 | 1.655 |  |
| mbrot | 0.746 | 0.300 | 2.486 |  |
| mbrotZ | 24.695 | 0.317 | 77.982 |  |
| mperm | 21.886 | 11.893 | 1.840 |  |
| nboyer | 14.068 | 2.939 | 4.787 |  |
| nqueens | 60.480 | 14.253 | 4.243 |  |
| ntakl | 59.080 | 11.922 | 4.956 |  |
| nucleic | 7.982 | 1.315 | 6.071 |  |
| paraffins | 9.929 | 6.002 | 1.654 |  |
| parsing | 13.945 | 3.330 | 4.188 |  |
| peval | 27.881 | 3.514 | 7.933 |  |
| pi | 0.879 | 0.937 | 0.938 |  |
| pnpoly | 6.978 | 1.320 | 5.284 |  |
| primes | 10.347 | 1.862 | 5.556 |  |
| puzzle | 14.562 | 1.392 | 10.462 |  |
| quicksort | 8.219 | 1.377 | 5.970 |  |
| ray | 7.279 | 0.500 | 14.570 |  |
| read1 | 2.146 | 1.866 | 1.150 |  |
| sboyer | 11.536 | 2.996 | 3.851 |  |
| scheme | 26.503 | 3.666 | 7.230 |  |
| simplex | 16.031 | 1.985 | 8.077 |  |
| slatex | 5.603 | 2.401 | 2.333 |  |
| string | 1.515 | 0.415 | 3.650 |  |
| sum | 2.280 | 0.434 | 5.250 |  |
| sum1 | 1.514 | 1.008 | 1.503 |  |
| sumfp | 1.871 | 0.216 | 8.653 |  |
| tail | 0.686 | 0.303 | 2.263 |  |
| tak | 6.296 | 1.194 | 5.271 |  |
| takl | 64.161 | 11.931 | 5.378 |  |
| triangl | 17.917 | 2.408 | 7.442 |  |
| wc | 4.630 | 4.207 | 1.101 |  |

几何平均（56 项）：cleat / JVM = **4.52**


### 分布
- 持平或更快（4 项）：ack 0.51（JVM 的深递归在大栈线程里）、cat 0.63、pi 0.94、fib 0.98（cleat-104 的类型化入口）；read1、wc、fibfp 1.1 ~ 1.2。
- 2 ~ 8 倍（大多数）：表结构与符号（nboyer、sboyer、deriv、earley、graphs、parsing、matrix、nqueens、takl、ntakl……）在 4 ~ 5 倍，可变序对与对象数组（destruc、gcbench、peval、scheme、triangl、simplex）在 7 ~ 8 倍。
- 10 倍以上：ctak 94、mbrotZ 78、bv2string 27、fibc 23、fft 16、ray 15、browse 15、array1 13、maze 11、puzzle 10。

### 慢区的原因（子代理的最小复现与本任务的确认）
1. **逃逸用的异常**（ctak 94、fibc 23，以及 puzzle、maze、quicksort 的一部分）：.NET 的 throw / catch 要走栈（每次微秒级），JVM 抛预先分配的异常很便宜。
   宿主差异；能做的是把只作逃逸的 call/cc 模式（`(try … (throw 预分配))`）在编译期认出来编成跳转——Clojure 代码里罕见，暂不做。
2. **deftype 的 `^double` 字段参与算术时装箱**（mbrotZ 78）：`(+ (.-re a) (.-re b))` 每次约 20 ns（JVM 不到 3 ns，且逃逸分析去掉了分配）。
   字段读取的结果类型没交给推断（`GetInstanceField` 的原始类型），算术走 Object。
3. **浮点循环装箱**（fft 16、ray 15、sumfp 8.7、simplex 8、mbrot 2.5、nucleic 6）：同类原因——从对象数组 / 向量取出的 double 与 generic 算术；Num 只覆盖了形参与循环变量。
4. **分支汇合处的原始变量退回 object**：`(loop [n 0] … (recur (if c (inc n) n)))` 的 n 推断为 long，但 if 两支里的 `n` 被算作装箱用途（推断只把回边实参本身当原始用途，
   不穿过 if / let / begin 的尾位置），装箱用途多于原始用途就存成 object，每轮装箱。影响面最广（计数器、累加器、quicksort 的扫描循环每轮 15 ns 对 1.2 ns）。
5. **`(= x 0)` 等 Object 与 long 的比较**：编成 `Util.equiv(Object, Int64)`，ClojureCLR 运行时的通用路径约 23 ns（JVM 3 ns）；`zero?` 11 ns 对 1 ns。maze、primes、diviter 等受影响。
6. **`(object-array n)` 的返回类型不传给无提示的局部**：其后 `aset` 走运行时决议（每次约 400 ns / 1M 对 27 ns）。scheme 加 `^objects` 前 74.8 s、后 52.8 s；array1 的 13 倍同源。
7. **可变序对的访问**（browse 15、destruc、lattice、puzzle）：`mcdr` 经函数再接口调用，每次约 12 ns（JVM 1.5 ns：内联掉）。小函数跨调用的装箱与虚调用（cleat-105 的量化）。
8. **字节**（bv2string 27）：`(byte x)` 每次约 77 ns、`aset` 到 `byte[]` 约 108 ns（JVM 合计 4 ns）——转换与字节数组的 aset 走了慢路径。
9. **符号 `=`** 约 27 ns（JVM 8 ns）：peval、dynamic、scheme 里按符号的 assq / memq。

### 后续
按影响面排：4（推断的原始用途穿过 if / let 尾位置）→ 5、8（运行时的 equiv / byte 快路径，C# 侧）→ 6、2（宿主调用 / 字段读与 object-array 的结果类型交给推断）
→ 3（浮点：从数组取出的 double）→ 7 与 designs/20 §6 的类型化调用点 → 9。1（逃逸异常）是宿主差异，暂不做。拆成任务前先与用户确认方向。
