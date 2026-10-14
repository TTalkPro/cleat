# cleat.nanopass：Clojure 版的 nanopass 框架

`cleat.nanopass` 是 [nanopass-framework-scheme](https://github.com/nanopass/nanopass-framework-scheme) 的 Clojure 移植（MIT，LICENSE-MIT.txt），
cleat 的编译器（`cleat.compiler.*`）就是用它写的；镜像里自带，`(require '[cleat.nanopass :as np])` 即可用。
设计见 [designs/07](../../../../designs/07-nanopass-port.md)（D1 ~ D6）与 [designs/19](../../../../designs/19-clojure-self-hosting.md) D2；本文是用户文档。
第二层特性（显式 catamorphism 的处理器指定、多层省略号的单项、`define-parser`、`trace-define-pass`）在 [cleat-095](../../../../tasks/cleat-095-nanopass-user-layer.md)，
本文只写现在有的。

## 1. 定义语言

```clojure
(ns demo.lang
  (:require [cleat.nanopass :as np :refer [define-language define-pass nanopass-case with-output-language out in-context]]))

(defn datum? [x] true)

(define-language L0
  (terminals (symbol (x)) (datum (d)))        ; 终结符：名字 + 元变量；谓词是 名字?（symbol?、datum?，在当前命名空间决议）
  (entry Expr)                                 ; 入口非终结符
  (Expr (e body)                               ; 非终结符：名字 (元变量 …) 产生式 …
    x                                          ; 终结符交替
    (quote d)
    (if e0 e1 e2)
    (begin e* ... e)                           ; 省略号：e* 是序列（任何 seqable；构造出来的节点里是向量）
    (set! x e)
    le                                         ; 非终结符交替
    (letrec ([x* le*] ...) body)               ; 产生式与模式里 [ ] 等同 ( )，unparse 一律输出列表
    (call e e* ...)
    (opt (maybe e)))                           ; (maybe e)：可以没有，值为 nil
  (LambdaExpr (le)
    (lambda (x* ...) (maybe x) body)))

(define-language L1 (extends L0)               ; 继承：终结符与非终结符子句里用 (+ …) / (- …) 增删
  (terminals (+ (string (str))))
  (Expr (e body)
    (- (set! x e))
    (+ (set-box! x e) (unbox x) (named str e))))
```

规则：
- 元变量按后缀区分（`e0`、`e*`、`x1*`），**不能以 `?` 结尾**；同一产生式里每个字段名唯一。
- 应用类的产生式必须以关键字开头（`(call e e* ...)` 而不是 `(e e* ...)`）：不带关键字的产生式会吞掉同形的形式。
- 终结符的谓词名是 `名字?`，须在定义语言的命名空间里能决议（`symbol?` 是 clojure.core 的；`datum?` 自己定义）。

`define-language` 展开为：每个产生式一个 `deftype`（`L0-Expr-if`，字段按元变量名，实现 `cleat.nanopass.records.INode`）、带检查的构造函数
`make-L0:Expr:if`、谓词 `L0:Expr:if?` 与 `L0:Expr?`、语言对象 `L0`、`unparse-L0`。展开结果不含 gensym：同一源码两次展开相同（自举不动点需要）。

```clojure
(make-L0:Expr:if 'a (make-L0:Expr:quote 1) 'b)   ; 构造函数检查每个字段（省略号字段逐个检查，maybe 字段接受 nil）
(L0:Expr? 'x)                                   ; => true（终结符交替）
(.-e0 ^L0-Expr-if n)                            ; 字段按元变量名访问
(np/parse L0 '(if a (quote 1) b))               ; S 表达式 → 节点（缺省按入口非终结符；(np/parse L0 'LambdaExpr form) 指定）
(np/unparse n)                                  ; 节点 → S 表达式
(np/language->s-expression L1)                  ; 展开 extends 之后的全量 define-language 形式
(binding [np/*check-constructors* false] …)     ; 宏展开期读取：关掉构造函数的字段检查
```

节点按引用比较（`=` 只对同一个对象为真）；打印为 `#<L0 (if a (quote 1) b)>`。

## 2. 写 pass

```clojure
(define-pass boxify L0 [ir boxed] -> L1 []            ; 输入语言 [ir 额外形参 …] -> 输出语言 [额外返回值 …]
  (definitions                                        ; 可选：(def x e) 与 (defn f [a] …)；def 先于所有函数求值
    (defn boxed? [x] (contains? boxed x)))
  (Expr [ir] -> Expr []                               ; 处理器：名字 [ir 额外形参 …] -> 输出非终结符 [] 子句 …
    [~x (if (boxed? x) (out (unbox ~x)) x)]           ; 模式：~x 绑定字段
    [(set! ~x ~[e]) (out (set-box! ~x ~e))])          ; ~[e]：catamorphism，e 绑定为 (Expr e) 的结果
  (Expr ir))                                          ; 体（可省略：缺省调入口非终结符的处理器）

(boxify (np/parse L0 '(letrec ((f (lambda (a) nil (begin (set! a (quote 1)) a)))) (call f b))) #{'a})
```

- 输入 / 输出写语言 `L0` 或 `[L0 Expr]`；输出 `*` 表示处理器返回任意值（不是节点）。
- 处理器名与输入非终结符相同时可省略后者；`(LambdaOf LambdaExpr [ir] -> LambdaExpr [] …)` 是另起名字的写法。处理器在 pass 里互相可见（`letfn`），
  体里可以直接调用。
- **自动补全**：没写的处理器、处理器里没写的产生式，按输出语言同样模式的产生式自动复制（字段经对应的处理器；经非终结符交替到达的产生式交给该
  非终结符的处理器）。`(define-pass identity-pass L0 [ir] -> L0 [])` 就是一个复制 pass。
- 额外形参按名字传给 catamorphism 与复制子句：`(Expr [ir depth] -> Expr [] …)` 里 `~[body]` 等于 `(Expr body depth)`；要改值就显式调 `(Expr body (inc depth))`。
  自动生成的处理器需要某个形参而没有时，展开期报错（`needs k`）。
- 子句可带 `(guard 条件)`：失败时试下一个子句；没有子句匹配时报错 `no clause matches`。
- 输入入口检查：传入别的语言的节点报 `expected L0 Expr`。

### 模式（`nanopass-case` 同样）

| 写法 | 含义 |
|---|---|
| `(quote 0)` | 字面量，按 `=` 比较 |
| `~x` | 绑定字段 |
| `~[e]` | catamorphism：字段经本非终结符（或 nanopass-case 自身）处理后绑定 |
| `~e* ...` / `~[e*] ...` | 省略号区段（整段，向量） |
| `(call ~f)`、`(call ~f ~a ~b)`、`(call ~f ~a ~rest ...)` | 省略号区段里的单项（0 个、固定个数、单项 + 余下整段） |
| `(letrec ([~f ~le]) ~body)` | 省略号区段里恰好一项 |
| `(call (lambda (~x* ...) ~r ~body) ~e* ...)` | 嵌套的产生式 |
| `~le` | 非终结符交替（整个节点） |
| `[else …]` | 兜底 |

```clojure
(defn count-vars [e]
  (nanopass-case [L0 Expr] e                         ; 按 L0 的 Expr 分派；展开期生成内联代码，不建闭包
    [~x 1]
    [(if ~[a] ~[b] ~[c]) (+ a b c)]                   ; ~[a] 对同一个 nanopass-case 递归
    [(begin ~[e*] ... ~[e]) (apply + e e*)]
    [(opt ~[e]) (or e 0)]                             ; maybe 字段的 catamorphism：无值时原样（nil）
    [else 0]))
```

### 模板（`out`）

`(out 模板)` 只在输出上下文里有意义：`define-pass` 的处理器体（输出非终结符即上下文）与 `(with-output-language [L0 Expr] …)`。

| 写法 | 含义 |
|---|---|
| `(out (if ~e0 ~e1 ~e2))` | 构造 `L0:Expr:if`，`~e` 填入值（构造时检查字段） |
| `(out (begin ~@xs c))`、`(out (begin ~xs ... c))` | 序列整段接入（两种写法相同），可与单项混合 |
| `(out (letrec ([~xs ~les] ...) f))` | 子模板按整段接入 |
| `(in-context LambdaExpr (out (lambda () nil y)))` | 在模板里把当前非终结符换成 LambdaExpr |
| `(out (quote (1 2)))` | 终结符位置的列表是引用的数据，不是模板 |
| `(out (opt ~nil))` | maybe 字段为 nil |
| `(out (lambda (p q) r (set! p (quote 0))))` | 非终结符交替：Expr 上下文里直接构造 LambdaExpr |

模板里的求值顺序从左到右，序列立即实现（处理器的副作用顺序与上游相同）。模板里的名字不是本语言的产生式时展开期报错。

## 3. 与上游的对照

| 上游（Scheme） | `cleat.nanopass` |
|---|---|
| `(define-language L (terminals (symbol (x))) …)` | 相同；谓词按 `名字?` 在当前命名空间决议 |
| `(extends L0)`、`(+ …)` / `(- …)` | 相同 |
| `` `(if ,e0 ,e1 ,e2) `` 模板 | `(out (if ~e0 ~e1 ~e2))`；`out` 只在输出上下文里识别，不是全局宏 |
| `,e` | `~e` |
| `,e ...` | `~@e*` 或 `~e ...`（`...` 是普通符号） |
| `,[e]` catamorphism | `~[e]`（向量，只在模式位置） |
| `,[f : x -> y z]`（指定处理器、多个返回值） | 第二层（cleat-095），现在没有 |
| `(maybe x)`，`#f` 表示无 | `(maybe x)`，`nil` 表示无 |
| `(define-pass p : L0 (ir) -> L1 () …)` | `(define-pass p L0 [ir] -> L1 [] …)`；额外形参与返回值用向量 |
| `(Expr : Expr (ir) -> Expr () …)` | `(Expr [ir] -> Expr [] …)`；另起名字 `(名字 Expr [ir] -> Expr [] …)` |
| `(definitions …)` | 相同（`def` / `defn`） |
| 多个返回值 `(values a b)` | 第二层；现在用向量 + 解构 |
| `nanopass-case` 每次执行重建闭包 | 展开期一次生成内联代码（IronScheme iron-034 的教训） |
| `trace-define-pass`、`echo-define-pass`、`define-parser` | 第二层；S 表达式 → 节点现在用按描述解释的 `np/parse` |
| 不以关键字开头的产生式 | 拒绝（会吞掉同形形式） |

## 4. 例子：编译器里的用法

`src/clojure/cleat/compiler/languages.clj` 定义 L0 ~ L3（`define-language`、`extends`），`loops.clj` / `front.clj` / `closures.clj` 是
`define-pass` 写的 pass（`lower-loops`、`simplify-letrec`、`uncover-free` …），`codegen.clj` 用 `nanopass-case` 遍历 L3；
`tests/nanopass/cleat/nanopass_test.clj` 与 `nanopass_pass_test.clj` 是覆盖本文每一项的小例子（`dotnet test` 的 NanopassTests 跑它们）。
