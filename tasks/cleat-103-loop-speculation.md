# cleat-103 循环的 long / double 推测：快慢两份循环

- 里程碑：M10　依赖：cleat-102　状态：完成（2026-10-15）
- 设计依据：[20 §4](../designs/20-skeg-optimizations.md)；skeg iron-071 / 080B

## 工作内容
1. 判定：最内层 `label` 循环里类型未定的形参，回边实参在"这些形参都是 long（double）"的前提下一定是 long（double）。
2. 生成：入口按实参检查（`isinst` 或 Num 标签），快份原始局部、回边不检查；慢份 object / Num；溢出直接抛（与慢份一致）。
3. 开关 `CLEAT_NO_OPT` 含 `loopspec`；语义用例（入口是 long / double / Ratio / BigInt / 非数、NaN、多形参一起推测）；IL 快照。

## 验收标准
- `bench/untyped_loop` 的 double 项降到与有提示的循环同级；基线、门禁、全量不变；种子不动点。

## 结果（2026-10-15）

### 实现（codegen.clj：speculate-loops，开关 `CLEAT_NO_OPT=loopspec`）
- 时机：generate-unit 先照常推断一遍；`speculate-loops` 按这遍的结果挑候选；有循环被改写就清表、在改写后的形式上重推。
- 候选：最内层 `label`，体里没有 lambda / letrec / label / try / 赋值 / box / deftype / def，不超过 300 个节点；`body0` 是 let / begin 套着的一个 `jump`。
  形参的初值不是常量、静态类型不是 long / double / int（第一遍的 `sound-types`），第一遍没定成原始类型或 Num，且在循环体里是 Numbers 算术 / 比较的操作数。
- 猜测：先都猜 long；复制一份（绑定全部换新名），初值包 `(clj-call "Speculated" #f 'l64 t)`，用体外变量的已知类型（`sound-types` 作 seeds，
  对应原版 infer-var-types-into! 的 seeds 参数）试算推断；定不成的改猜 double，再不成就放弃。一个都不成就不改写。
- 生成：非平凡的初值先按顺序求值到临时变量（各求一次），然后 `(if (and (isinst T1 t1) …) 快份 (jump 原循环 t…))`；
  `Speculated` 的 expr-type 是猜的类型，生成代码时透明（原始用途由 cg-raw-default 取 unbox.any）。快份的形参由推断定成原始局部，回边不检查。
- 已经是 Num 的变量（有 double 字面量迹象，cleat-102）不推测：Num 的 double 路径已与有提示的循环同级（sum-double 9 ms 对 ^double 10 ms），
  再复制只增加代码。

### 测量（linux01，Release）
| | 关（`CLEAT_NO_OPT=loopspec`） | 开 | 有 `^long` / `^double` 提示 |
|---|---|---|---|
| sum-long（5M，初值来自无提示形参） | 13 ms | 2 ~ 3 ms | 1 ms |
| sum-double（5M，Num，不推测） | 9 | 9 | 10 |
| bench untyped_loop（3 轮 min） | 22 | 11 | — |

- 验收：untyped_loop 的 double 项在 cleat-102 后已与有提示的同级；本任务补的是 long 项（13 → 2 ~ 3 ms）。
- boot 镜像里被改写的循环有 8 处（dorun、drop 的 step、nthnext、nthrest、print-sequential、ArrayChunk、Vec、pprint 的 justify-clauses），
  boot.dll 2180096 → 2183680 字节；编译器包大 83 KB（主要是新代码本身）；种子 1.93 MB；selfhost 92 → 94 s。
- 语义：`tests/Cleat.Host.Tests/data/loopspec_semantics.clj`（入口 long / double / Ratio / BigInt / BigDecimal / Int32 / 字符串 / nil / 关键字、
  溢出、NaN / Inf、两个变量一起推测、会漂成 double 的变量、循环里装箱使用、初值是有副作用的调用、unchecked-math、嵌套循环）开 / 关输出逐字相同；
  `ClojureCompilerTests.LoopSpeculation` 另查 sum-long 的快份（`Numbers.add(long, long)`）与慢份，以及 `^long` 提示的循环不复制。
- 验证（linux01）：种子不动点（stage2 == stage3，从种子发射与 stage3 相同）；Core 117 / Host 257 通过（1 个按环境跳过）；
  语料门禁通过（522 行）；两套基线不变（cts 6866/31/6、test_clojure 3688/18/0）。bench 其余项在噪声内
  （protocol 的 min 在 25 / 46 间跳，开关下编出的代码相同；keyword_invoke 13 ~ 37）。
- 中途一次"不是不动点"是假象：rsync 把本地旧的 `src/boot/*.dll`（构建产物）带到 linux01，selfhost 的 stage1 是没有推测的旧编译器；先 build 再生成种子即可。

### 顺带发现
- `Int64/MaxValue` 一类的静态字段读在这里没有静态决议（运行时 `ClojureOps.CallStatic`），类型未知；推测因此退到 double。另记，不在本任务做。
- 循环体里的体外数值变量（`(< i n)` 的 `n`，Num 形参）没有一起推测：快份里它仍走 `Num.LessThan`。sum-long 剩下的 2 ~ 3 ms 对 1 ms 的差距就在这里，
  可以与 cleat-104 的类型化入口一起考虑。
