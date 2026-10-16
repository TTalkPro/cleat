# cleat-123 有限标量替换：非逃逸 primitive record / deftype

- 里程碑：M12　依赖：cleat-119　状态：进行中（实现完成，等批量验证）
- 设计依据：[22 §4 D10](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 识别候选：局部创建的不可变 record / deftype，字段全为 primitive 或可拆箱值；不传给未知调用、不入集合、不被闭包捕获、不跨异常边界保留对象身份；后续使用只有字段读取、等值判断或构造另一候选。
2. 编译器把候选 lowering 成原始局部：字段读取变 `ldloc`，对象身份需要时在同一方法内物化。
3. 第一版不做完整逃逸分析、不改对象表示、不处理数组元素；不能证明时保持原生成代码。
4. 以 mbrotZ 驱动：每步复数对象构造应显著下降。
5. 前提：mbrotZ 的 Complex 传进 `c+` / `c*`，`c*` 因体积没有被内联，`z` 是 recur 的循环变量——先解决小 deftype 操作函数的内联，
   或把循环携带的候选对象拆成原始循环变量；同时评估 .NET 10 JIT 的对象栈上分配在调用全部内联后能否自动生效。

## 实现记录（2026-10-16）
- **循环变量**（codegen 的 `scalar-label`，与循环推测同一阶段）：最内层循环的循环变量 z，recur 时某些尾位置是 `(NewOf T a …)`、循环体里读它的字段，
  T 是 sealed、字段全是 long / double 且个数与构造实参一致 → 快份循环里 z 拆成每字段一个原始循环变量与对象槽 zobj
  （做法见 designs/22 D10）：进入时 `(isinst T z0)` 不成立走原来的循环；字段读 `(if zobj (zobj 的字段) 标量)`；别的用途 zobj 为空才构造一次；
  recur 的尾是构造则取新字段、zobj 置空，是 z 则不变，是别的（守卫失败的分支、未知对象）则带着本轮实参跳回原来的循环。
- **let 临时对象**（`scalarize-lets`，在快份里）：`(let ([t (NewOf T …)]) body)` 同样拆；初值的尾位置混着构造与别的值（内联的守卫）且 body 不大时，
  先把 let 推进初值的各个尾位置（body 复制、换新名）。
- **配套**：
  - 只有字段读、Numbers 运算与构造的子句内联上限放宽到 400 个序对（`c*` 之类；全局放宽会撑爆 JIT 的内联预算，fft 实测变慢）；
  - 有原始签名（`^double` / `^long`）的子句也内联：体外包上 `PrimArg*` / `PrimRet*` 转换（与 invokePrim 的桥相同），守卫失败的分支走 `PrimInvoke`，
    两支都是原始值；`ClojureStaticCalls` 加恒等重载 `PrimArgLong(long)` 等，类型已一致时不装箱；分析器对这类函数先内联、再退到 PrimInvoke；
  - 推断前折叠不回跳、入口实参都是变量的标签（内联子句外面的 arity 标签；体里有 lambda / reify 时不折叠，它们带显式的捕获变量表）；
  - `expr-type` 认不回跳的标签（值是体的值），`cg-raw` 穿过体类型已知的 let——原始值能穿过内联的体，不再装箱再拆箱；
  - 修 cleat-117 的形参置空：按存储位置判断（let 绑定到形参的变量复用形参的槽，名字不在循环体里不代表槽不再被用）。
- 开关：`CLEAT_NO_OPT=scalar`（标量替换）、`CLEAT_NO_OPT=collapse`（标签折叠）。
- 本机：mbrotZ 3.8 → **1.7 ~ 1.8 s**；微基准（2 次构造 / 轮的复数迭代，20 万次）819 MB / 274 ms → **0 MB / 134 ms**。
- 语义：`data/scalar_semantics.clj`（7 个循环发生变换：同一性与哈希、可变字段被方法修改、构造实参的转换与异常顺序、long 字段与溢出、
  defrecord、let 临时对象、循环中途重定义被内联的函数、原始签名函数的内联与 BigInt / 比值 / nil / 非数字）`CLEAT_NO_OPT=scalar` 开 / 关逐字相同；
  测试 `ClojureCompilerTests.DeftypeLoopVariablesAreScalarised`。门禁 OK，种子不动点（2073 KiB）。

## 验收标准
- mbrotZ 分配采样中复数对象分配显著下降、耗时前后对照（GraalVM 的部分逃逸分析消掉这些分配，8.5x 是对 GraalVM 的差距；对 C2 的差距待 cleat-119 实测）。
- 构造、字段读取、`=`、异常路径开 / 关输出逐字相同；基线、门禁、全量不变。
