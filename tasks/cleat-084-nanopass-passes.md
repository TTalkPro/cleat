# cleat-084 cleat.nanopass pass 层

- 里程碑：M8（阶段 A）　依赖：cleat-083　状态：完成（第一层；第二层拆到 cleat-095）
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

## 结论（2026-10-14）

第一层完成；第二层（显式 catamorphism、多层省略号的单项、`define-parser`、trace、上游测试）不在关键路径上，拆到 [cleat-095](cleat-095-nanopass-user-layer.md)。

**表面**（`src/clojure/cleat/nanopass.clj` 的文件头有完整说明，展开在 `src/clojure/cleat/nanopass/pass.clj`）：

```clojure
(define-pass convert-assignments L1 [ir an] -> L2 []
  (definitions (def label-params (System.Collections.Hashtable.)) (defn boxed? [x] …))
  (Expr [ir] -> Expr []
    [~x (if (boxed? x) (out (unbox ~x)) x)]
    [(set! ~x ~[e]) (if (boxed? x) (out (set-box! ~x ~e)) (out (set! ~x ~e)))]
    [(let ([~x* ~[e*]] ...) ~[body]) (out (let ([~x* ~(map … x* e*)] ...) ~body))])
  (Clause [ir] -> Clause [] …))
```

- 处理器 `(名字 输入非终结符 [ir 额外形参 …] -> 输出非终结符 [] 子句 …)`（同名可省输入非终结符），子句是向量 `[模式 (guard …)? 体 …]` / `[else …]`；
  `definitions` 段 `(def …)` / `(defn …)`；处理器与 definitions 的函数在一个 `letfn` 里互相可见；pass 的体缺省调用入口处理器；入口检查输入语言。
- 模式：`~x` 绑定、`~[x]` catamorphism（含 `maybe` 字段：无值原样）、裸符号与常量是字面量、嵌套的产生式、省略号区段的单项 / 整段接入（`~x ...`、`~@x`、子模式整段接入）；
  按标签 `case` 分派，测试失败的子句回退到下一个。guard 在 catamorphism 之前求值。
- 模板 `(out …)`：宏遍历代码找出 `out`，按当前输出非终结符编成构造函数调用；非终结符字段位置的 `~e` 里的 `out` 以该字段的非终结符为上下文（上游按字段切换 quasiquote）；
  `in-context` 换上下文；`~@e` / `~e ...` 接入（可多段混合单项）；终结符位置的列表是引用的数据。
- 自动补全：缺的产生式按输出语言同样模式的产生式复制（字段经对应处理器；同语言之外的产生式在展开期报错）；经非终结符交替到达的产生式交给该非终结符的处理器；
  没声明却被用到的处理器自动生成（形参只有 ir）；catamorphism / 复制子句按名字传额外形参，作用域里没有时展开期报错。
- `nanopass-case [L NT]`：展开为内联代码（不建闭包，iron-034 的教训），有 `~[x]` 时才包一个递归的本地函数。`with-output-language [L NT]`。
- 生成代码不含 gensym（临时名按展开内计数），省略号字段一律是实现了的向量、构造参数从左到右求值（惰性序列会改变处理器副作用的顺序）。

**试点**（验收）：`src/clojure/cleat/compiler/` 下按 Scheme 版逐函数移植，作为 cleat-087 的起点——
`closures.clj`（`uncover-free` 用 `nanopass-case`、`attach` 用 `define-pass`）、`loops.clj`（`lower-loops` 全部，含 guard 的多子句、`with-output-language`、模板接入）、
`front.clj`（`analyze`、`check-forward`、`convert-assignments`）。`tests/scheme/compiler/snapshots.sps` 多写两个阶段：`.p0`（parse-core 的输出）与 `.l1`（convert-assignments 的输入），
各 29 个。`cleat.compiler.pilot-test`：`uncover-free` 的 `.l2 → .l3` 全部相同；`lower-loops` 的 `.p0 → .l0` 在临时变量名（`名字%N`）规范化后全部相同；
`convert-assignments` 的 `.l1 → .l2` 全部相同；`check-forward` 单元测试。

**发现**：IronScheme 构造 nanopass 记录时实参不是从左到右求值（`lower-loops` 的 D6 里 `(fresh "rest")` 先于 `(map … (fresh "arg"))`），Clojure 版从左到右，
临时变量编号因此可能不同——G1 本来就在规范化之后比较（19 D5），测试辅助 `cleat.test.scheme-data/canonical-temps` 按首次出现重新编号。

**性能**（约 239 万个节点的同形 L2 树，同一台机器）：
| 项 | Scheme | Clojure |
|---|---|---|
| 构造整棵树（带检查的构造函数） | 1.17 s | 0.87 s |
| `uncover-free`（`nanopass-case` 遍历 + 集合运算 + `attach` 复制） | 1.8 s | 3.2 s |
| 只计数的遍历：`nanopass-case` / 手写 `instance?` + 字段访问 | — | 0.69 s / 0.59 s |

框架本身的分派开销约为手写代码的 1.18 倍；`uncover-free` 慢在遍历体里的序列操作（`reduce` / `map` / 集合运算）与 Clojure 代码在本运行时上的一般开销，
不在 nanopass。顺带的优化：整段接入直接绑定字段值（不复制）、`r/->vec`、一层省略号字段的构造检查用 `r/check-all`（不建闭包）、试点的集合运算用循环。
G3（cleat-090）会在整条编译链上量。

**测试**：`tests/nanopass/cleat/nanopass_pass_test.clj`（复制与自动处理器、显式子句、经交替委托、额外形参、作用域报错、各种模式与回退、`nanopass-case` 的 catamorphism、
值 pass 与子模式整段接入、模板、构造检查、求值顺序、展开确定性），连同 cleat-083 的两个与试点共 4 个命名空间 342 条断言，`NanopassTests` 4/4。
