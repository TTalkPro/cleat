# 16 nanopass 宿主语言调研:SBCL/CLOS、Erlang 与 Clojure 的可行性

状态:调研结论。回答"离开 Scheme 之后,nanopass 风格的多 pass 编译器能否在 SBCL(Common Lisp)、Erlang、Clojure 上实现同等功能,难度何在"。证据基础为 nanopass-framework-scheme 仓库源码取证。本文是 [07](07-nanopass-port.md)(移植到 Clojure 的决策)的**可行性背书与难度量化**:07 回答"怎么移",本文回答"为什么这条路相对其他宿主不亏,以及真正的硬骨头是哪一块"。

> 结论先行:**三个宿主都能实现,但"能实现"与"不亏本"是两回事**。SBCL/CLOS 赢在多分派的声明式表达(上下文维度提升为 method 特化);Erlang 赢在模式匹配的原生度(函数子句即 syntax-case 子句)与 TCO/并行;Clojure 赢在宏系统(syntax-quote 之外另有 defmulti/spec)与不可变数据默认。三者共同失去的是 nanopass 最值钱的一半——**ellipsis 对偶引擎与编译期文法校验必须自造**。Clojure 路线的难度排序:ellipsis 引擎(唯一硬骨头)> 编译期校验的工程质量 > 托管运行时无可靠 TCO > API 风格适配。07 号文的 D1(重写而非机械翻译)、D2(显式模板绕开 reader 期反引号)与本文调研结论一一对应。

## 1. 取证:nanopass 到底依赖 Scheme 的什么

对 nanopass-framework-scheme 仓库全量检索 `define-record-type|syntax-case|with-syntax|quasisyntax|define-language|define-pass`,共 **636 处匹配,分布 32 个文件**,集中在:

| 依赖特性 | 出现位置 | 性质 |
|---|---|---|
| `syntax-case` + ellipsis 模板 | pass.ss / meta-syntax-dispatch.ss / nano-syntax-dispatch.ss 等,600+ 处 | 核心,不可替代性最高 |
| 卫生模式分发(matcher) | nano-syntax-dispatch.ss | 算法可移植,输出为 syntax object |
| R6RS `define-record-type` | records.ss | 易替代(defstruct/deftype/Erlang record) |
| 模块系统(library) | 全部文件 | 易替代 |
| 尾调用保证 | 隐式依赖 | Scheme 保证;SBCL 不保证;BEAM 保证;CLR 不保证 |

**pass 的本质**(pass.ss:388-459 `make-processor-clauses`):每个 clause 左值在宏展开期被 meta-parser 解析为 nano-meta AST,生成按"输入 term 的产生式 alternative(record tag)+ 嵌套模式 + guard"选择执行体的 matcher;未覆盖的 alternative 自动生成递归穿通的 system clauses(pass.ss:461 起)。即:

> processor = 以 (节点类型, 结构模式, guard) 为分发键的转换函数集合 + 手动穿线的上下文参数。

真实例证(tests/compiler.ss:567 `convert-closure`):processor `Expr` 携带额外形参 `direct`,两条 `(app ...)` clause 属同一 alternative,靠子节点模式 + `(guard (assq x direct))` 区分。

## 2. SBCL/CLOS 路线

### 2.1 多分派为何能表达 pass

- **单分派 = processor 按 alternative 分发**:generic function 按首参的类选 method,与 nanopass 按 record tag 选 clause 同构;default method 恰好扮演自动生成的 system clauses。
- **多分派 = 上下文维度声明式化**:可同时特化第二参 `(defmethod process-expr ((n app-node) (ctx direct-context)))`,分发键从 (节点类型) 升级为 (节点类型 × 上下文),nanopass 里靠 guard 与手动穿线表达的上下文分支被提升为 method 特化。
- **红利**:`call-next-method` + 类继承 ⇒ 增量 pass;`:around`/`:before` ⇒ 横切关注点;新增节点与新增 pass 两维开放(visitor 模式在"新增节点"维封闭)。

### 2.2 边界

1. 嵌套模式分发不掉(同一 alternative 内的子结构区分仍需方法体内 trivia/optima 补一层);
2. 编译期完备性检查丢失(运行时 `no-applicable-method`);
3. TCO 不保证,需 optimize 设置或改写;
4. ellipsis 模板要自己造——600+ 处 syntax-case 依赖意味着完整移植 ≈ 重写。

## 3. Erlang 路线

### 3.1 映射与代码对照

| nanopass 成分 | Erlang 对应 | 保真度 |
|---|---|---|
| processor clause `[pattern guard rhs]` | 函数子句 + `when` guard | ✅ 几乎一一对应 |
| 上下文穿线 | 函数参数累积器 | ✅ 手感相同 |
| ellipsis `,e1 ...` | ❌ `lists:map` / 列表推导 | ⚠️ 噪音变多 |
| 自动 system clauses | 手写兜底 / mapfold 式通用遍历 | ⚠️ |
| 编译期文法校验 | ❌ 运行时 `function_clause`(Dialyzer 部分挽回) | ❌ 最大损失 |
| define-pass 宏 | parse transform + merl | ⚠️ 能做但痛苦 |
| TCO | ✅ BEAM 语言保证 | ✅ |

`convert-closure` 的 Erlang 形态与 Scheme 原版**结构完全同构**(guard 里的 `assq` 非白名单 BIF,需挪进函数体 `case`;`,e1 ...` 变列表推导)。函数头模式匹配就是 Erlang 程序员的 syntax-case。

### 3.2 生态证据

`erlang/otp` 的 `lib/compiler` 自身即多 pass 架构(`v3_core`、`v3_kernel`、`beam_ssa_*` 系列,每 pass 是对 term 的多子句函数变换);LFE 编译器同为多 pass 纯 Erlang 实现。多 pass 编译器是 BEAM 生态的主流实践。红利:TCO 语言保证、编译单元级并行、热代码加载。

## 4. Clojure 路线(本文重点)

### 4.1 能力映射

| nanopass 成分 | Clojure 对应 | 评估 |
|---|---|---|
| `syntax-case` 宏系统 | `defmacro` + syntax-quote(自动 gensym) | ✅ 非 Scheme Lisp 中最强,但见 §4.2 修正 |
| quasisyntax 模板 | ~~syntax-quote~~ **显式 `(out ...)` 形式**(07 D2) | ⚠️ 反引号不可用,见下 |
| ellipsis(含嵌套、cata 记号) | core.match 只有 `& rest`;需自造 | ❌ **最难的一块** |
| record term | `deftype`(07 D6) | ✅ |
| 编译期文法校验 | 宏展开期跑校验 + 带源位置 throw;无相位分离,宏可直接调普通函数 | ✅ 比 R6RS 更易做 |
| 上下文维度分发 | `defmulti`(dispatch fn 任意,天然支持 节点×上下文);07 实际选 `case` on tag | ✅ |
| 多值返回 | vector + 解构(07 D4) | ⚠️ 手动打包 |
| TCO | ❌ CLR 无可靠尾调用(`tail.` 前缀不可靠,同 JVM) | ❌ 见 §4.3 |

### 4.2 对"syntax-quote ≈ quasisyntax"的重要修正

直觉上 syntax-quote 是 quasisyntax 之外最接近的模板系统,但 **Clojure 的反引号在 reader 期就展开成 `(seq (concat …))` 并全限定符号命名空间,宏永远看不到模板原貌**(07 §0"Clojure 侧的事实")。所以"宏 DSL 直接用反引号写模板"这条路在 Clojure 里**不存在**——这不是表达能力问题,是 reader 与宏的分工问题。07 D2 的显式 `(out tpl)` 形式(宏自己遍历处理器体识别模板)正是对这个事实的正确回应,代价是表面语法离上游更远,换来的是上下文切换(按字段非终结符)逻辑全在宏自己手里,反而比上游的嵌套 `let-syntax` 重绑 quasiquote 更直白。

同理,`,` 是空白、`:` 不是独立 token、无点对,决定了 `~[f :> x -> y z]` 这类表面改造(07 D2 表)不是审美选择,是 reader 约束的必然结果。

### 4.3 难度排序

1. **ellipsis 对偶引擎(唯一硬骨头)**。匹配侧 `((,x ,y) ...)` 绑定任意长度、模板侧 `(,x ...)` 回放、嵌套 ellipsis、cata 记号四种写法(`~[e]`、`~[f :> x -> y z]`、`~[-> y]`),全部要自造展开逻辑。core.match 表达力不够。这是纯工程量,无理论障碍——07 D1 估计全库约 2000 行,大头在这里。
2. **编译期文法校验的工程质量**。Clojure 无相位分离,宏展开期可直接调用按语言描述数据驱动的普通函数做校验,比 R6RS 的 3-D 语法简单得多(07 D1 砍掉 meta-parser/parser/unparser 的"生成代码"部分即此红利)。难点只剩校验性能与报错可读性(要指到具体 alternative)。
3. **托管运行时无可靠 TCO**。processor 间互递归(Expr → Clause → Expr)`recur` 救不了,trampoline 污染约定。缓解:(a) AST 深度有界,body 递归加大栈即可;(b) 深递归热点改显式栈。注意 cleat 的编译器先在 stage0 解释器上跑(07 D7),栈消耗在解释器里再放大一层,stage0 的栈策略需在 08 号文范围内核实。
4. **API 风格适配(软难度)**。多值返回 vector 化、`maybe` 字段用 `nil` 代 `#f`、谓词名与元变量名冲突(iron-022 教训:元变量名禁 `?` 后缀)——都有 07 的既定方案,无未决问题。

### 4.4 存在性证明

jolt(Clojure 自寄宿编译器,nanopass 管线 + fixpoint cluster)与本项目自身引用的 IronScheme 用法(4 语言、10 define-pass、58 nanopass-case)证明:Clojure 语义子集足够表达 nanopass 管线;IronScheme 的两次性能教训(iron-034:nanopass-case 每次执行重建 pass 闭包、define-nanopass-record 重复建 RTD)已计入 07 D4/D6。

## 5. 四方对比

| | Scheme(原版) | SBCL/CLOS | Erlang | Clojure(cleat) |
|---|---|---|---|---|
| 模式分发 | 编译期,ellipsis 模板 | 类多分派 + 方法体内补模式 | 函数子句,原生 | 宏展开期编译,`case` on tag |
| 上下文维度 | 手动穿线 + guard | **声明式多分派** | 手动穿线 + case | 手动穿线 + guard(07 D4) |
| 文法静态检查 | ✅ 编译期 | ❌(可自造) | ❌(Dialyzer 部分) | **宏展开期校验,无相位分离更易做** |
| 模板系统 | quasisyntax 原生 | 自造 | merl 准引用 | 显式 `out`(reader 约束) |
| TCO | 语言保证 | 不保证 | **语言保证** | 不保证(托管运行时) |
| 生态证据 | 框架本体 | SICL/Cleavir | Erlang 编译器、LFE | jolt、IronScheme 用法 |

## 6. 对 cleat 的结论

1. **Clojure 路线成立,且 07 的每个非常规决策都有外部约束背书**:D1(重写)对应"相位分离代码占上游一半以上,Clojure 无此问题";D2(显式 `out`)对应"反引号 reader 期展开"这一硬约束;D5(注册表)对应无 `syntactic-property` 的替代品需求。
2. **难度集中于一处**:ellipsis 引擎是全库唯一的高复杂度区,建议按 07 §3 的验收计划(翻译上游 tests/ 的省略号多层用例)优先攻此点,其余皆是平坦工程量。
3. **TCO 是唯一没有干净答案的项**,但它影响的是"pass 怎么写递归"的局部风格,不影响架构;stage0 解释器上的栈消耗需在 08 §4 的 stage0 速度/资源基线里一并实测。
4. 三宿主对比的最终判词与 00/07 的方向一致:**想在 Clojure 之外找"更省力的 nanopass 宿主",只有 Scheme 本身**;在 Clojure 之内,07 的设计已经把可控成本压到了最低。

## 7. 与其他文档的关系

- [07](07-nanopass-port.md):本文是其可行性背书;§4.2/§4.3 的修正与难度排序细化 07 §0 的"Clojure 侧的事实"。
- [04](04-compiler.md):pass 链的处理器分发模型(§1 的"pass 的本质")是其 L0~L3 设计的理论底座。
- [08](08-bootstrap.md):§4.3 难度 3 的 stage0 栈消耗实测项归其 §4 基线。
- [01](01-reference-analysis.md):jolt/IronScheme 先例的引用与取舍判断与此处 §4.4 互证。
