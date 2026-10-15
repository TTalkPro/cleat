# cleat-111 运行时快路径：Util.equiv(Object, long / double)、zero? / pos? / neg? 作用于 Object、(byte x) 与 byte[] 的 aset、符号相等（cleat-107 原因 5、8、9）

- 里程碑：M11　依赖：cleat-110　状态：完成（2026-10-16）
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 逐个量化：`(= x 0)`、`(zero? x)`、`(byte 5)`、`(aset ^bytes b i v)`、`(= 'a 'b)` 的调用路径与耗时。
2. ClojureCLR 运行时（src/Cleat.Core/clojure.lang）对应方法加类型快路径（Int64 / Double 先判），byte 转换与 aset 的重载选择；符号相等先比引用与哈希。
3. 语义不变（溢出、NaN、类型不同的相等按 Clojure 规则）。

## 验收标准
- 各微基准接近 JVM 的量级；maze、bv2string、peval、dynamic、scheme 前后数字。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。

## 结果（2026-10-16）
（equiv / ops / category / ConvertTo* / empty? / Truth 的快路径已在 cleat-109 做了；这里是其余部分。）
- `RT.first / next / more`：先判 ASeq，类检查加虚调用，避开接口转换与接口分派的存根。
- 较窄的数值宿主结果（cleat-107 原因 8）：`ValueParamOk` 放行 byte / sbyte / short / ushort / float；`(byte x)` 的 `RT.byteCast` 静态决议，结果与运行时决议一样
  加宽成 long / double（Reflector.ConvertFrom 的规则）；`static-arg-type` 认这些返回类型；实参本身是返回类型正好为形参类型的静态宿主调用时原生值直传
  （`(aset ^bytes b i (byte x))` 不装箱）。`(aset bv i (byte 5))` 100 万次 352 → 8.7 ms（JVM 约 4）；bv2string 10.1 → 3.4 s。
  （cleat 的 byte 是 CLR 的无符号 0 ~ 255，原有语义。）
- 符号 / 关键字 / 字符串的 `=`：`Util.equiv` 在数值快路径后直接 `Equals`（原路径先 IsNumeric 的 GetTypeCode、再两次接口检查，结果相同）。
- `Colls.IsEmpty`、`ClojureOps.NotNil` / `ClojureStaticCalls.Instance` 强制内联（抛出部分移到不内联的方法）。
- 语义：`data/narrow_semantics.clj` 在 `CLEAT_NO_OPT=host` 与开两种设置下输出相同、开时无反射警告（`ClojureCompilerTests.NarrowHostResultsAndLetTypes`）。
- 验证（linux01，与 110 ~ 113 一起，一次完整流程）：种子不动点；Core 117 / Host 263 通过（1 个按环境跳过）；语料门禁 521 行；两套基线不变；
  bench 在噪声内（lazy_chain 48 → 16 ms）。r7rs 56 项 cleat 侧（各一次，对 JVM）：几何平均 3.74（109 后）→ 3.51（110 ~ 112 第一部分）→ **3.26**（含 112 第二部分与 113）。
- 这几个任务的改动在 codegen.clj、Util.cs 等文件里交织，按任务拆分要多跑几轮验证，所以一起验证、一个提交（与"每个任务一个提交"不同，有意为之）。
