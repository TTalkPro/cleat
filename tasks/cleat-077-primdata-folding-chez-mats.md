# cleat-077 原语元数据与查表折叠、Chez mats 对拍（移植 skeg iron-069 / 072）

- 里程碑：后续　依赖：cleat-076　状态：完成
- 来源：skeg 提交 0d56486（`~/workspace/skeg/Skeg/tasks/iron-069-prim-metadata.md`、`iron-072-chez-algo-data-mats.md`）

## 背景

cleat-075 背景里定下的移植顺序的第三步。iron-069 给原语表加类型签名与 Chez 式的标志（`mifoldable` / `discard` /
`unrestricted` / `safeongoodargs`），编译器侧新增 boot 过程的元数据表 `primdata.sls`，`cp0-lite` 的常量折叠改为查表；它是
iron-070（内联后暴露常量参数）与 iron-071（签名作为类型来源）的前提。iron-072 把 Chez Scheme 的 18 个 mats 文件纳入作对拍，
修了对拍发现的一批 R6RS 边界语义缺陷，并按 R6RS 11.11 改正 `char-alphabetic?` 等（Unicode 派生属性，不只看一般类别）。
两者在 skeg 是同一个提交。

## 做法

三方合并（基 2468e57，theirs 0d56486，cleat-075 / 076 的做法），31 个文件里 29 个无冲突。冲突与改写：

- `boot/numbers.sls`：skeg 早先把 `generic-expt` 与 flonum 打印拆成了小函数（"拍平嵌套"的重构，cleat 没有），iron-072 在拆开的
  形状上修 `expt`、`number->string`。这两段整体取 skeg 的版本（同源，结果即目标语义）。
- `Cleat.PrimGen/Program.cs`：表的头注释用 skeg 的；cleat 多一张 kernel 表（Cleat.Core 的 `Primitives.Table`，`inline` 标志），
  它的重载也写第 7 个字段（签名，`#f`）。`primitives.ss` 由 PrimGen 重新生成（表版本 2）。
- `tests/chez-mats/run.sps`：skeg 用 `(skeg threading)` 的 `make-thread`，cleat 没有这个库，改用 `$start-thread`（后台线程、已启动）；
  用法注释与 `mkknown.py` 的命令改为 stage0 + 镜像；原因分类 `skeg-lax` / `skeg-bug` 改为 `cleat-lax` / `cleat-bug`。
- `tools/gen-unicode-props.cs`：命名空间与输出路径改为 `Cleat.Scheme`。
- `ChezMatsTests`（`tests/Cleat.Scheme.Tests/`）：照 `R6RSConformanceTests` 的写法，18 个文件各一个用例，不并行。
- `NOTICE`：Chez mats（Apache-2.0）、`UnicodeProperties.cs`（UCD，Unicode License v3）、从 skeg 移植的代码（BSD 3-Clause）三条。

## 测试

- `tests/scheme/base/r6rs-edges.sps` 加入 iron-069 / 072 的各组（div / mod 的零符号、remainder / modulo、fxmod、eqv?、多参数比较、
  nan? 系、expt / lcm / bitwise、string->number / number->string、Unicode 属性、string-titlecase、枚举、bytevector、bignum 与 flonum
  混合的 div 系、尾数宽度、filter 系的参数检查），共 146 项全过。
- 快照：新增 `cp0-fold`、`sig-typed-loop`，与 skeg 的逐字相同（多一个 `(runtime scheme)`）；旧快照 64 个无变化。
- Chez mats：先用 skeg 的 `known.ss` 跑，18 个文件的 pass / known / skip 与 skeg 的表逐项相同；再清空 `known.ss` 全跑（`-v`），
  每个文件的失败数恰好等于原来登记的条数，`mkknown.py` 生成的 307 条（chez-ext 251、hang 27、cleat-lax 24、cleat-bug 5）与 skeg 的
  逐条对应、没有规则覆盖不到的子句；换上后 18 个文件全部 `fail 0`。
- 种子重生成，不动点。
- 全量 632/632（Scheme 255，含 18 个 ChezMats 用例；Core 129；Host 248）。

### 与 skeg 的差别：div / mod 的 fixnum 快路径

iron-072 给 `div` 加了 nan / 无穷（`nan*?` / `infinite*?`）与 bignum-flonum 混合的检查，`mod` 又经 `div`：两个 fixnum 时
`div` 47 → 70 ns、`mod` 89 → 131 ns（skeg 至今也是这样）。cleat 照 iron-061 / 064 的写法给两者加了入口快路径：两个 fixnum、除数
不是 0 / -1 时截断除法再修正符号（|x2| ≥ 2 时商的修正不会溢出；除数 -1 时 .NET 的 % 对最小 fixnum 抛溢出，走原实现）。
之后 `div` 40 ns、`mod` 39 ns，比移植前还快。r6rs-edges 加了一组性质检查（17 个 fixnum 样本两两组合：x1 = q·x2 + m、
0 ≤ m < |x2|、与 `div-and-mod` 一致；最小 fixnum ÷ -1、除零），共 151 项。

### 微基准（对 cleat-076 的构建）

- `micro-char.sps`（ns/次，含约 37 ns 的未知过程调用开销）：`char-foldcase` 78 → 40、`char-ci=?` 124 → 62、`char-upper-case?` 46 → 24、
  `char-upcase` / `char-downcase` 49 / 45 → 35 / 36；`char-alphabetic?` 41 → 46（多查 Other_Alphabetic 区间表，R6RS 11.11 的正确性代价，
  skeg 同样 +5 ns）。
- `micro-ops.sps`：常量参数的项被查表折叠后变快（`expt fixnum` 64 → 11、`exact flonum` 14 → 5、`char->integer`、`string->symbol`、
  `length` …——基准里的参数是常量，这是 iron-069 的效果，不代表运行时的同等提速）；其余在噪声内。
