# cleat-102 Num：不装箱的未定类型数，嵌套算术与 let 变量的双局部表示

- 里程碑：M10　依赖：cleat-101　状态：未开始
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
