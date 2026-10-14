# cleat-084 cleat.nanopass pass 层

- 里程碑：M8（阶段 A）　依赖：cleat-083　状态：未开始
- 设计依据：[19 D2](../designs/19-clojure-self-hosting.md)、[07 D2、D4](../designs/07-nanopass-port.md)、[16](../designs/16-nanopass-host-survey.md)

## 目标
Clojure 写的 `define-pass` / `nanopass-case` / 输出模板，第一层覆盖编译器用到的全部特性，第二层给用户。

## 工作内容
第一层（阻塞 cleat-086 起的任务）：
1. `define-pass`：签名 `[L NT]` 与 `*`、额外形参、`definitions`、多个处理器、处理器额外形参、处理器之后的体、`-> * [v]` 返回值的 pass（多返回值用向量）。
2. 子句：模式匹配（一层省略号、`e* ... e`）、`guard`、`else`、普通 catamorphism `~[e]` / `~[e*] ...`。
3. 自动补全处理器与子句（L → L′ 的复制、只给出部分子句的处理器）。
4. `nanopass-case`（宏展开期一次性生成，不在执行时构造闭包集合）、`with-output-language` 与 `(out …)` 模板（`~e`、`~@e*`、`~e ...`、`[~x* ~e*] ...`、`~(expr) ...` 的对应写法、按字段切换上下文）。
5. 性能：处理器按标签 `case` 分派；在一个合成的大 IR 上测遍历耗时，与 Scheme 版同规模遍历对比并记录。

第二层（不阻塞阶段 B / C，取代 cleat-056）：多层省略号、显式 catamorphism `~[f :> x -> y z]`、`in-context`、`define-parser`、`language->s-expression`、`trace-define-pass`；
上游 `tests/compiler.ss` / `tests.ss` 翻译为 `tests/nanopass/` 的验收。

## 验收标准
- 第一层：把 `closures.sls` 的 `attach`、`front.sls` 的 `check-forward` 与 `convert-assignments`、`loops.sls` 的 `lower-loops` 用 cleat.nanopass 改写作试点，在快照 IR 上输出与 Scheme 版相同。
- 第二层：上游测试全部通过；宏展开结果确定。
