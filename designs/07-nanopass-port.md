# 07 nanopass 框架移植到 Clojure（`cleat.nanopass`）

> **状态（2026-10-14）：重新启用。** [19](19-clojure-self-hosting.md) D2 采用本文 D1 ~ D6 的表面设计，在编译后的 Clojure 镜像上实现（cleat-083 / 084）。下面 2026-10-12 的搁置说明仅对 stage0 前提的部分仍成立。

> **状态（2026-10-12）：搁置，部分失效。** 本文为"Clojure 编译器在 stage0 上解释运行"的旧路线而写，该路线随 cleat-023 搁置，
> 现行方向是原生 Scheme nanopass 一行不改（[15 §4](15-scheme-core-vendoring.md)）。凡以"stage0 解释运行本库"为前提的内容
> （文首目标的最后一句、§1 D7、§3 第二条、§4 后两条）已失效，下文以 **【失效】** 标注；§0 事实与 D1 ~ D6 的表面设计仍可作参考。
> 若在编译后的 Clojure 镜像上重做（"第三段"），见 [评估笔记](../notes/clojure-nanopass-plan-review.md)。

> 蓝本：`~/workspace/nanopass-framework-scheme`（R6RS，约 4900 行核心 + 每宿主约 200 行适配）与 IronScheme 对它的使用（`src/scheme/lib/cleat/scheme/compiler/`：4 个语言、10 个 `define-pass`、58 个 `nanopass-case`）。
> 目标：用 Clojure 重写一个**语义等价、表面按 Clojure 习惯调整**的 nanopass，供 [04](04-compiler.md) 的编译器使用；~~它必须能在 stage0 解释器上展开并运行（[08](08-bootstrap.md)）。~~ **【失效】** 第三段路线下本库编译后在镜像上运行。

## §0 事实

**原框架的组成**

| 文件 | 行数 | 职责 |
|---|---|---|
| `nanopass/pass.ss` | 1628 | `define-pass`、`nanopass-case`、`with-output-language`、trace/echo；处理器与子句生成、catamorphism、自动补全处理器（`find-proc`）与子句（`make-system-clause`）、输出检查 |
| `nanopass/records.ss` | 825 | 展开期的语言描述记录（`language`、`tspec`、`ntspec`、`pair-alt`、`terminal-alt`、`nonterminal-alt`、`nano-*`）；`annotate-language!` 建 RTD 与标签；`language->lang-records` 生成构造器、谓词、访问器 |
| `nanopass/language.ss` | 536 | `define-language`：解析子句、`extends` 的 `+`/`-` 编辑、`language->s-expression`、`diff-languages`、`prune-language` |
| `nanopass/meta-parser.ss` | 410 | 为每个语言生成"元解析器"：把模式 / 模板语法变成 `nano-meta` 树；`output-records->syntax` 把模板变成构造器调用 |
| `nanopass/parser.ss` / `unparser.ss` | 172 / 150 | S 表达式 ↔ 记录 |
| `nanopass/helpers.ss`、`syntaxconvert.ss`、`*-syntax-dispatch.ss` | 约 750 | 关键字判定、模式转换、匹配器 |
| `implementation-helpers.*.sls` | 约 200 | 宿主适配：gensym、`define-property`、编译期值、记录定义 |

**它依赖的宿主能力**（全部是 R6RS 宏系统的能力）：`syntax-case` 过程式宏、故意不卫生的 `datum->syntax`（捕获 `quasiquote`、`in-context`、`who`）、`generate-temporaries`/`gensym`、`free-identifier=?` 识别关键字（`...`、`unquote`、`:`、`->`、`=>`、`+`、`-`）、**`let-syntax` 在展开期重绑 `quasiquote`**（不是 syntax parameter）、过程式记录层（展开期建 RTD 并把 RTD 对象直接 quote 进展开结果，"3-D 语法"）、编译期环境（`make-compile-time-value` 存语言描述，`define-property` 存 pass 信息）。不用 `eval`、不用 `include`。

**记录层次**：`nanopass-record(tag)` → 每语言一个 RTD → 每非终结符一个 RTD → 每个产生式一个 sealed RTD（字段按元变量命名）。标签 = `(产生式序号 << bits) + 非终结符序号`。`extends` **不共享**记录类型，每个语言全部重建。

**IronScheme 的性能教训**（iron-034）：`nanopass-case` 每次执行都构造一整个 pass 的闭包集合（每节点约 1 KB），codegen 中改成库级 `define-pass`；上游 `define-nanopass-record` 在每次 pass 调用里重建记录类型，IronScheme 用覆盖库改为库里定义一次。

**Clojure 侧的事实**：反引号由 reader 展开成 `(seq (concat …))` 并限定符号命名空间，宏看不到模板；`,` 是空白；`~x` 读成 `(clojure.core/unquote x)`、`~@x` 读成 `(clojure.core/unquote-splicing x)`，宏能识别；`...` 是合法符号；`:` 单独不是合法 token；`[ ]` 是向量；没有点对；没有多值；没有 `let-syntax`；`defmacro` 不卫生但有 `gensym`/auto-gensym；宏可以直接调用普通函数（没有 R6RS 的相位分离）。

## §1 决策

**D1 重写而不是机械翻译**。上游一半以上的代码在对付 R6RS 的相位分离（元解析器以代码形式生成、RTD 以 3-D 语法嵌入）。Clojure 的宏可以直接调用解释语言描述的普通函数，所以 `meta-parser.ss`、`parser.ss`、`unparser.ss` 的"生成代码"部分改成**按描述数据驱动的普通函数**。保留的是上游的**语义**：语言定义、`extends`、产生式记录、标签分派、catamorphism、省略号、自动补全处理器与子句、输出检查、unparse。目标规模：约 2000 行 Clojure。

**D2 模板与模式用显式形式，不用反引号**。

| 上游 | `cleat.nanopass` |
|---|---|
| `` `(if ,e0 ,e1 ,e2) `` 模板 | `(out (if ~e0 ~e1 ~e2))`；`out` 由 `define-pass` / `with-output-language` 的代码遍历识别（D4），不是全局宏 |
| `,e` 反引用 | `~e`（reader 给出 `(clojure.core/unquote e)`） |
| `,e ...` | `~@e*` 或 `~e ...`；两者都接受，`...` 是普通符号 |
| `,[e]` catamorphism | `~[e]`（向量，模式位置专用） |
| `,[f : x -> y z]` | `~[f :> x -> y z]`（`:>` 代替 `:`，`->` 不变） |
| `,[-> y]` | `~[-> y]` |
| `(maybe x)` | `(maybe x)` 不变；字段值 `nil` 代替 `#f` |
| `(letrec ([x* e*] ...) body)` 中的方括号 | **产生式与模式中 `[ ]` 等同 `( )`**（和上游一样只是写法）；运行时 unparse 输出一律用列表。L0 的 Clojure 绑定形式（`let*` 的向量）由 parse-core 自己拆开，语言里没有向量字段 |

**D3 语言定义的表面**：

```clojure
(define-language L0
  (terminals
    (symbol? (x))                 ; 谓词 + 元变量；Clojure 谓词名不改
    (datum? (d))
    (name? (name)))
  (entry Expr)
  (Expr (e body)
    x
    (quote d)
    (if e0 e1 e2)
    (do e* ... e)
    (lambda name (clause* ...))
    (call e e* ...))
  (Clause (clause)
    (clause (x* ...) rest body)))        ; rest 为 (maybe x)

(define-language L1
  (extends L0)
  (Expr (e body)
    (- (call e e* ...))
    (+ (let (x* ...) (e* ...) body))))
```

- 元变量后缀规则与上游相同（`e0`、`e*`、`x1*`）；**元变量名不能以 `?` 结尾**（iron-022 教训），谓词用 `symbol?` 没有问题，因为谓词不是元变量。
- `define-language` 展开为：每个产生式一个 `deftype`（字段按元变量名，另有 `tag` 字段）、一个带检查的构造函数 `make-L0:Expr:if`、谓词 `L0:Expr?`、`L0:Expr:if?`、访问器（用 `.-字段`），加 `unparse-L0`，并在**语言注册表**登记描述（D5）。
- 应用产生式带关键字（`call`），遵守 IronScheme 03 §9 的教训：不带关键字的产生式会吞掉同形形式。

**D4 pass 的表面与上下文切换**：

```clojure
(define-pass simplify-letrec [L0 Expr] [ir] -> [L1 Expr] []
  (definitions (defn- helper [x] …))
  (Expr [ir] -> Expr []
    [(call ~[e] ~[e*] ...) (out (call ~e ~e* ...))]
    [(if ~[e0] ~[e1] ~[e2]) (guard (const? e0)) (out …)]
    [else (default ir)])              ; 可省略；省略时自动生成
  (Clause [c] -> Clause []))          ; 无子句：全部自动生成
```

- 签名 `[L NT]`、`*`（任意）与上游一一对应；额外形参 `[ir extra]`、额外返回值 `[xval …]` 用向量；多返回值统一用向量 + 解构（Clojure 没有多值）。
- **上下文切换不靠 `let-syntax`**：`define-pass`、`with-output-language`、`nanopass-case` 的宏**自己遍历**处理器体（先 `macroexpand-all` 用户代码中的普通宏，再遍历），遇到 `(out tpl)` 按当前输出非终结符编译模板，模板里非终结符字段位置的 `~expr` 子表达式递归遍历时把上下文切换为该字段的非终结符（对应上游 `process-nano-elt` 的嵌套 `let-syntax`）；`(in-context NT body)` 显式切换。这样保留了上游"按字段切换上下文"的规则。
- 处理器分派：按 `tag` 字段 `case`（int 开关），非终结符交替用掩码；终结符用谓词；子句 lambda 绑定访问器取出的字段，catamorphism 递归调用处理器。
- `nanopass-case` 展开为一个**在宏展开期一次性生成的**本地 `fn`，不像上游那样每次执行重建 pass（iron-034 教训）。
- 自动补全（`find-proc`、`make-system-clause`）、输出检查（`build-checked-body`）、`trace-define-pass`、`echo-define-pass` 保留。

**D5 语言注册表取代编译期环境**。`define-language` 展开结果里包含 `(cleat.nanopass.registry/register! 'ns/L0 描述)`，既在宏展开期执行（本命名空间后面的 `define-pass` 立刻可用），也在加载期执行（AOT 编译的命名空间加载后，其他命名空间的宏仍能找到）。查找按 `resolve` 得到的限定符号。对应 IronScheme 的 `syntactic-property.sls` 做法。

**D6 运行时表示**：每个产生式一个 `deftype`（CIL 目标是真实 CLR 类；LLVM 目标是带类型标签的结构体，[06](06-llvm-backend.md)）。`extends` 不共享类型（与上游相同：L1 的 `if` 与 L0 的 `if` 不是同一类型，自动生成的复制子句因此必要）。所有记录实现 `cleat.nanopass/INode`（`tag`、`language`、`unparse`）。确定性：类型名与标签只取决于定义顺序，不含 gensym（[08 §7](08-bootstrap.md) 的不动点需要）。

**【失效】** 第三段路线下本库编译后在镜像上运行，可用完整的 `clojure.core`，下列子集限制不再适用。

**D7 对 stage0 的要求**。编译器在 stage0 上以解释方式运行，nanopass 宏在 stage0 的宏展开中执行。所以 `cleat.nanopass` 只能用 [03](03-language-core.md) 核心语法与 `clojure.core` 中 stage0 能跑的部分：`deftype*`（stage0 用发射器生成真实类型，08 §4）、`case`、`defmacro`、`macroexpand-all`（由 `cleat.nanopass` 自带一个只认核心形式的版本，不依赖 `clojure.walk`）、`gensym`。禁止：`eval`、反射、`proxy`、`gen-class`。

## §2 不做的

- `experimental.sls`、`exp-syntax.sls`、`prefix-matcher.ss`（Chez 专用，上游也不导出）。
- `with-extended-quasiquote` / `with-r6rs-quasiquote`（Scheme 反引号语义扩展，无意义）。
- `define-language-node-counter`、`prune-language`、`diff-languages` 第一版不做，`language->s-expression` 做（调试需要）。

## §3 验证

- 把上游 `tests/`（约 4700 行的测试编译器 `compiler.ss` 与 `tests.ss`）翻译成 Clojure，作为 `cleat.nanopass` 的验收用例（cleat-095 完成：`tests/nanopass/cleat/upstream/`）：语言定义、`extends`、省略号多层、`maybe`、catamorphism 各种写法、自动生成处理器与子句、`trace`。
- **【失效】** ~~在 stage0 上展开并运行上述测试（任务 cleat-020），记录展开与运行耗时，作为 08 §4 对 stage0 速度要求的依据。~~
  （原链接的任务号有误，应为 [cleat-023](../tasks/cleat-023-nanopass-port.md)，已搁置。）
- 展开结果确定：同一源码两次展开得到相同形式（类型名、标签、临时变量名不含进程相关成分）。

## §4 与其他文档的关系

- [04](04-compiler.md) 的 L0 ~ L3 用本库定义；04 §9 的"应用产生式带关键字"规则来自上游陷阱。
- **【失效】** [08 §4](08-bootstrap.md)：stage0 必须支持本库用到的核心形式。
- **【失效】** [03](03-language-core.md)：本库是 "核心语法 + `clojure.core` 子集" 的第一个真实用户，决定 stage0 必须覆盖的 core 范围。
- [16](16-nanopass-host-survey.md)：宿主语言调研与可行性背书；[评估笔记](../notes/clojure-nanopass-plan-review.md)：第三段路线的范围、门控与迁移建议。
