# cleat-079 类型格与收窄、拆箱循环、simplify-if 与 commonize（移植 skeg iron-071 / 073）

- 里程碑：后续　依赖：cleat-078　状态：完成
- 来源：skeg 提交 fdfb5c1、12dfdaf（iron-071 / 073 的部分；12dfdaf 里 iron-074 的 TranscodedReader 已在 cleat-075 移植）；
  `~/workspace/skeg/Skeg/tasks/iron-071-cptypes-lattice.md`、`iron-073-simplify-if-commonize.md`

## 背景

cleat-075 背景里定下的移植顺序的最后一步。

- iron-071：① fixnum 装箱缓存扩到 [-1024, 16384)（`Values.cs`）；② 拆箱循环：最内层 label 循环的形参推测为 fixnum，循环体生成快慢两份，
  回边上的通用算术按 int64 算、在 fixnum 范围内就回快循环，溢出或入口不是 fixnum 时装箱转慢的一份（`InlineArith.Result(long)`）；
  ③ 类型格（`fx < int < real < num`、`fl < real`、`pair / null < list`、`bool char symbol vector`）与收窄：谓词与已执行的操作在代码生成期间
  给变量加事实（`var-facts`），消费在内联算术、`car` / `cdr`、`vector-ref` / `vector-set!`、结果已知的类型谓词与测试上。
- iron-073：front 的 `simplify-if`（测试规范化、外层已知真假的测试在分支里折叠、常量分支）；codegen 的 commonize（结构相同的闭合 lambda
  共用一个过程）。

## 做法

三方合并（基 e34373d，theirs 12dfdaf）。6 个文件里 5 个无冲突，`codegen.sls` 10 处冲突——cleat-042 为 Clojure 的 long / double
在同一块推断上加过一套（`l64`、Numbers 的 long 运算、静态决议的宿主调用的返回类型、原始签名形参的 `fixed-types`、`sound-types`、内联子句
形参 `$inl` 不推断为 double），skeg 把这块改写成了类型格。以 skeg 的结构为底，把 cleat 的逐项合回：

- `expr-type`：`Int64` 常量是 `l64`（格里的一个独立元素，与其余都不相交），两种 `clj-call`（PrimInvoke、Numbers / 宿主调用）照 cleat；
  变量不在 env 里时查 `var-facts`。`meet-type` 现在就是 skeg 的 `type-join`（fx 与 fl 合流为 real；拆箱只认 fx / fl / l64，效果不变）。
- 推断（`infer-var-types-into!`，skeg 拆出 seeds / out 供拆箱循环复用）：`meet-defs` 保留 `$inl` 的处理；`fixed-types` 照旧先放进
  env（推测模式下不写全局表）；`decide!` 照 cleat 写 `sound-types`（fx / fl / l64，不论是否拆箱），fx / fl / l64 按用途决定拆箱，其余类型
  记进 `var-facts`——`l64` 不进 `var-facts`（Clojure 的 long 只在拆箱时才当作已知，与 cleat-042 相同，Clojure 代码的生成不变）。
  合并工具把 cleat 的旧不动点循环与 skeg 的 `propagate!` 拼在了一起，这一段手工重写。
- `if`：结果已知的类型谓词只生成一支（skeg）；测试照旧用 cleat 的 `truthy-bool-call`，分支照旧按引用数调整局部变量清除（cleat-042），
  两支各带收窄事实。只生成一支时没生成的那支的引用不再扣减，清除只会更保守。`let`：skeg 的逐个绑定带事实，`binding-code` 按 cleat 的条件；
  `label`：cleat 的循环内钉住外部变量（不清除）套进 skeg 的结构。
- 拆箱循环不在局部变量清除（`clearing?`）生效的方法里做（循环体生成两份，引用计数按一份算）；skeg 的 `mctx-async` 条件去掉（元层没有
  async，Clojure 的 `^:async` fn 不会有推测需要的 Scheme 通用算术）。
- `procedure-class!`：先走 cleat 的 Clojure fn 分支，其余按 skeg 加 commonize。commonize 的 `alpha-normalize` 按 cleat 的节点认绑定
  （`try-handle` 而不是 skeg 的 `try-catch x …`——cleat 的 `try-catch` 第一个子项是体，体恰为变量时会被误当绑定名，两个不同的过程可能
  得到同一个键），含 Clojure 节点或嵌套 Clojure fn（名字是记录）的不合并。
- 新增的两个遍历——codegen 的 `collect-assigned!`（收窄只对不被赋值的变量做）与 front `simplify-if` 的 `assigned-vars`——skeg 的版本对
  不认识的节点 `[else (void)]`；cleat 的 L1 / L3 每个复合节点（含 `clj-…`、`try-catch`、`try-handle`、`escape-frame`）都要走到，漏掉
  Clojure 节点里的 `set!` 会把被赋值的变量当作不变量收窄或折叠。
- 测量开关照 skeg：`CLEAT_NO_071` 为 1 或含 `spec` / `narrow`，`CLEAT_NO_073` 为 1 或含 `if` / `commonize`。

## 测试

- 快照：新增 `commonize`、`narrow-cond`、`narrow-effect`、`narrow-predicate`、`sig-lattice`、`simplify-if`、`spec-loop`；`vector-ops`、
  `sig-typed-loop`、`local-escape`、`let-lambda-labels` 随之变化。与 skeg 12dfdaf 的快照逐个比对，全部逐字相同（多一个 `(runtime scheme)`）；
  快照 116 个全过；`front-seed` 39/39。
- base-library 224、r6rs-edges 151；Chez mats 18 个文件全部 `fail 0`（收窄与推测循环改变了生成的代码，这是正确性的主要对拍）。
- boot 源码没变，种子不变（重新生成逐字相同）。Scheme 镜像 2387456 → 2404864 字节（+0.7%；skeg：iron-071 +2.1%，commonize 后 +0.8%）。
- 全量 632/632（Scheme 255、Core 129、Host 248）。

## 结论（2026-10-14）

完成。

- **微基准**（`micro-ops.sps`，对 cleat-078 的构建交替三轮取最小值）：基准循环的计数器被推测拆箱，循环开销几乎消失，很多项降到 0 ~ 1 ns
  （`= fixnum`、`vector-ref`、`eq?`、`fl+`、`string->symbol` …），这主要是循环开销没了，不代表操作本身同样变快；`remainder` 15 → 11、
  `quotient` 11 → 6、`mod` 31 → 21、`memq` 9 → 4、`string=?` 9 → 5、`vector-fill!` 281 → 163。
  慢的几项：`cons+car` 9 → 16（计数器成了原始 int32，`(cons i i)` 每次重新装箱、超出缓存就分配；`CLEAT_NO_071=1` 时回到 10）、
  `exact-integer-sqrt`（2000 位）33.6 → 38.9 µs（关掉开关时 31.8 ~ 34.5，同属循环本身的副作用）、`bignum +` 190 → 226、
  `string->utf8` 63 → 73（关掉开关时 209 ~ 250 / 72 ~ 80，波动大，都是分配的操作，同 cleat-078 里 `vector-fill!` 的堆状态影响）。
  skeg 的全量跑分（57 个 r7rs 程序）几何平均 0.904，没有对应的回退。
- **Clojure 基准**两边一致：Clojure 代码的推断只多了格里的 bool / pair 等（不影响它用到的 l64 / fl），l64 不进 `var-facts`，拆箱循环要
  Scheme 的通用算术，commonize 不合并 Clojure fn，Clojure 代码的生成没有变。
