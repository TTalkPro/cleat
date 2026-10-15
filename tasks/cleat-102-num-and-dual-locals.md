# cleat-102 Num：不装箱的未定类型数，嵌套算术与 let 变量的双局部表示

- 里程碑：M10　依赖：cleat-101　状态：完成
- 设计依据：[20 §3](../designs/20-skeg-optimizations.md)；skeg iron-080 / 082 / 083（`Num.cs` 直接搬，语义按 Clojure 改）

## 工作内容
1. `Cleat.Core.Runtime.Num`（C#）：从 skeg 的 `Num.cs` 改——精确整数 64 位、溢出走 `Numbers` 慢路径（抛同样的异常）、`*unchecked-math*` 版本、
   `=` 不按 double 比、`< <= > >= ==` 按 Numbers 的规则、装箱走 `Box` 缓存；显式静态构造函数并在加载镜像时初始化。
2. 推断：`infer-var-types-into!` 的 `:num` 候选（不精确迹象的不动点，原始用途不少于装箱用途）。
3. 生成：`cg-raw :num`、`numbers-arith-op` 的 Num 路径、`label` 的 Num 形参。
4. 嵌套算术双局部 `(double, object)`（082）与 let 变量双表示（083b）。
5. 开关 `CLEAT_NO_OPT` 含 `num` / `dual`；测试：Num 语义用例开 / 关逐字相同（`tests/Cleat.Host.Tests` 新增）、IL 快照。

## 验收标准
- `bench/nested_arith` 从约 354 ms 降到 50 ms 以内；分配采样显示热循环零分配；两套基线、门禁、全量不变；种子不动点。

## 结论（2026-10-15）

完成 Num（1 ～ 3）；双局部变量（4）不需要（下）。开关 `CLEAT_NO_OPT=num`。

### 做了什么
- **`Cleat.Core.Runtime.Num`**（从 skeg 的 `Num.cs` 改）：`{long Bits; object Tag}`，Tag 为 null 是 long、`FlTag` 是 double 的位、其余是装箱对象本身（BigInt / Ratio /
  BigDecimal / 非数；null 记 `NullTag`）。快路径：long/long（64 位有溢出检查）、double/double、long 与 double 混合（long 转 double，同 Numbers 的传染）；
  其余装箱后调 `Numbers` 的 object 重载，结果与异常完全相同。与 skeg 的三处语义差别：long 溢出走慢路径抛 ArithmeticException（不升 BigInt）；
  `Equiv` 是 `==`（Numbers.equiv），`=`（Util.equiv）不经 Num；`Divide` 除数为零（long 0 或 ±0.0）交给 Numbers——object 路径先查 isZero 再分派，
  `(/ 1.0 0)` 抛 Divide by zero（JVM 同），只有静态决议到 divide(double, double) 的才得 ##Inf（第一版快路径算出 ##Inf，对照用例抓住的）。
  `Startup` 先跑 Num 的类构造函数，JIT 把 `FlTag` 当常量。
- **推断**（`infer-var-types-into!` 的 Num 阶段，在原有 decide! 之后）：
  - fn 形参：有原始用途（Numbers 算术 / 比较的操作数）就是 `:num`——方法入口 `FromObject` 一次存进 Num 局部（`:numarg`），原始用途读它，
    **装箱用途仍读原实参**（不另花钱，也保住对象同一性）；
  - 形参的副本（fn 的 arity 循环把形参复制进标签变量、`(let [x p] …)`）：全部定义都是对同一个 Num 形参的引用时做**别名**，共用形参的位置，
    jump / let 不再求值与写回；
  - let 变量与 label 形参：没定成 `:fx / :fl / :l64` 的，全部定义都是数值类型的表达式（含 Num 变量、Num 形参）且原始用途不少于装箱用途——只增不减的不动点。
  - 这是表示，不是类型事实：不进 `var-facts`（skeg 的教训：`(symbol? x)` 不能因此折叠）。
- **生成**：`cg-raw :num`（常量直接构造；Num 变量 ldloc；推断为 double / long 的子表达式——宿主调用、原始算术、if / let / begin——走原始路径再
  `FromDouble` / `FromInt64`；有 Num 操作数的算术递归；其余 `FromObject`）；`Numbers` 的 `+ - * / inc dec` 与 `< <= > >= ==` 有 Num 操作数时调
  `Num.Add` 等（`-` 单参数的取负不走）；`expr-type` 把这种算术记为 `:num`；原始 double / long 目标遇到含 Num 操作数的算术时整棵按 Num 算再
  `Num.ToDouble` / `ToInt64`（推断按 `add(double, object) → double` 把循环变量定成 double 的情形）。

### 两处返工（都是每轮多一次分配，把 Num 的收益吃光；分配采样抓出来的）
1. 形参副本进了 Num 局部后，装箱用途每轮 `Num.Box` 新建 Double（mbrot 52 → 100 ms），还丢了 `(= a a)` 对 NaN 的同一性相等——改成别名。
2. `(- (* zr zr) (* zi zi))` 这类 double 子表达式进 Num 时先 `box` 再 `FromObject`——改成原始路径 + `FromDouble`。

### 为什么不做 skeg 的双局部变量（082 / 083）
skeg 的 `(double, object)` 双局部是给"内联了类型检查的通用算术"用的：它的 codegen 对 object 操作数内联 `isinst double` 分支，内层结果才需要一对
局部变量承接。cleat 不内联通用算术（调 `Numbers.add(object, object)`），嵌套算术的操作数要么已有原始类型，要么是 Num 形参 / 变量——Num 本身
就是"值 + 标签"的一对，起同样的作用，而且形参入口只转换一次。nested_arith 311 → 22 ms 已经达到目标，所以不再加第二套表示。

### 测量（本机，Release）
| | 改前 | 改后 |
|---|---|---|
| nested_arith（无提示形参的嵌套算术，5M） | 311 ms | 22 ms |
| untyped_loop（无提示初值的 long / double 循环） | 49 | 34 |
| param-double 微基准（5M） | 42 | 11 |
| mbrot | 52 | 62（等于关掉 Num；它的循环变量本来就是 double） |
| nested_arith 的分配（5 轮） | 2.3 GB Double | 热循环零分配 |

- 语义：`tests/Cleat.Host.Tests/data/num_semantics.clj`（无提示形参进入的循环、溢出、`+'`、混合、Ratio / BigInt / BigDecimal、NaN、`==` 与 `=`、
  非数与 nil 报错、除零、unchecked-math、形参既原始又装箱使用、NaN 同一性相等、mbrot 形状）在开 / 关两种设置下输出逐字相同；
  `ClojureCompilerTests.NumUnboxedArithmetic` 另检查编出的嵌套算术循环体没有 Double 装箱。
- 第一次全量周期抓出一个真错误：`(/ a b)` 两边都推断成 long 时，原始 long 路径去找不存在的 `Numbers.divide(long, long) → long`
  （Lcil 报 return type mismatch，3 个测试命名空间加载失败）。divide 两个 long 可能是 Ratio，只走 Num 路径；`numbers-arith-args` 只收有 long 重载的运算。
- 修正后：两套基线不变（test_clojure 3688/18/0、cts 6866/31/6）、语料门禁通过、种子不动点、Core 117 / Host 256 通过。
- bench（5 轮 min，对 M10 起点）：nested_arith 311 → 22、untyped_loop 49 → 25、ctor 84、property 9、reduce_vec 6，其余在噪声内（mbrot 53、fib 7）。

### 顺带发现（array1，记入 designs/20）
skeg 的 array1 移植到 Clojure 后：`long-array` 版 10.7 s（JVM 0.25 s）——`aset` / `aget` 全走 Reflector，因为引用类型提示（`^longs`）在分析器之后丢了，
`RT.aset` 的 14 个重载选不出；决议器还把数组类型排除在可引用类型之外。作为 cleat-101 的延续另立任务修。
