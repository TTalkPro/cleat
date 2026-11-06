# cleat.nanopass：Clojure 版的 nanopass 框架

`cleat.nanopass` 是 [nanopass-framework-scheme](https://github.com/nanopass/nanopass-framework-scheme) 的 Clojure 移植（MIT，LICENSE-MIT.txt）。
cleat 原来的 Clojure 编译器（`cleat.compiler.*`，cleat-182 删除）就是用它写的；编译器改成 C#（[designs/30](../../designs/30-csharp-compiler.md)）之后，
它是 `packages/nanopass` 下的普通用户库：包程序集 `Cleat.Nanopass`（`cleat -T:build install`，`:package "nanopass"`），
或把 `packages/nanopass/src` 放上加载路径，`(require '[cleat.nanopass :as np])` 即可用。
设计见 [designs/07](../../designs/07-nanopass-port.md)（D1 ~ D6）与 [designs/19](../../designs/19-clojure-self-hosting.md) D2（历史）；本文是用户文档。
上游 `tests/unit-tests.ss` 与 `tests/compiler.ss`（一个 20 趟的 Scheme 子集编译器）的翻译在 `test/cleat/upstream/`（[cleat-095](../../tasks/cleat-095-nanopass-user-layer.md)），
是最完整的写法样本。测试：`cleat -I packages/nanopass/src -I packages/nanopass/test packages/nanopass/test/run.clj`。

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

(define-language L2
  (terminals (symbol (x)) (vref (v)) => vref-sym)  ; 终结符后的 => 函数：unparse 时作用于值
  (Expr (e)
    (var v) => v                               ; 产生式后的 => 模板：unparse 时按它输出（(unparse x true) 不美化）
    (seq e0 e1) => (begin e0 e1)
    (if e0 e1)                                 ; 同一关键字可以有几个产生式，按形状（元素个数）区分
    (if e0 e1 e2)
    (e0 e1 ...)))                              ; 不以关键字开头的产生式：按形状匹配（上游允许；(e0 . e1) 这种带点对的不支持）
```

规则：
- 元变量按后缀区分（`e0`、`e*`、`x1*`），**不能以 `?` 结尾**；同一产生式里每个字段名唯一。
- 应用类的产生式最好以关键字开头（`(call e e* ...)`）：不带关键字的产生式按形状匹配，会吞掉同形的形式（编译器自己的语言一律带关键字）。
- 终结符的谓词名是 `名字?`，须在定义语言的命名空间里能决议（`symbol?` 是 clojure.core 的；`datum?` 自己定义）。

`define-language` 展开为：每个产生式一个 `deftype`（`L0-Expr-if`，字段按元变量名，实现 `cleat.nanopass.records.INode`）、带检查的构造函数
`make-L0:Expr:if`、谓词 `L0:Expr:if?` 与 `L0:Expr?`、语言对象 `L0`、`unparse-L0`。展开结果不含 gensym：同一源码两次展开相同（自举不动点需要）。

```clojure
(make-L0:Expr:if 'a (make-L0:Expr:quote 1) 'b)   ; 构造函数检查每个字段（省略号字段逐个检查，maybe 字段接受 nil）
(L0:Expr? 'x)                                   ; => true（终结符交替）
(.-e0 ^L0-Expr-if n)                            ; 字段按元变量名访问
(np/parse L0 '(if a (quote 1) b))               ; S 表达式 → 节点（缺省按入口非终结符；(np/parse L0 'LambdaExpr form) 指定）
(define-parser parse-L0 L0)                     ; 同上，定义成函数 (parse-L0 form) / (parse-L0 'LambdaExpr form)
(np/unparse n)                                  ; 节点 → S 表达式（按 => 美化；(np/unparse n true) 不美化）；每个语言另有 unparse-L0
(np/language->s-expression L1)                  ; 展开 extends 之后的全量 define-language 形式
(binding [np/*check-constructors* false] …)     ; 宏展开期读取：关掉构造函数的字段检查
```

节点按引用比较（`=` 只对同一个对象为真）；打印为 `#<L0 (if a (quote 1) b)>`。

## 2. 写 pass

```clojure
(define-pass boxify L0 [ir boxed] -> L1 []            ; 输入语言（或 *）[ir 额外形参 …] -> 输出语言（或 *）[额外返回值 …]
  (definitions                                        ; 可选：(def x e) 与 (defn f [a] …)；def 先于所有函数求值
    (defn boxed? [x] (contains? boxed x)))
  (Expr [ir] -> Expr []                               ; 处理器：名字 [ir 额外形参 …] -> 输出非终结符 [] 子句 …
    [~x (if (boxed? x) (out (unbox ~x)) x)]           ; 模式：~x 绑定字段
    [(set! ~x ~[e]) (out (set-box! ~x ~e))])          ; ~[e]：catamorphism，e 绑定为 (Expr e) 的结果
  (Expr ir))                                          ; 体（可省略：缺省调入口非终结符的处理器）

(boxify (np/parse L0 '(letrec ((f (lambda (a) nil (begin (set! a (quote 1)) a)))) (call f b))) #{'a})
```

- 输入 / 输出写语言 `L0` 或 `[L0 Expr]`；`*` 表示任意值：输入 `*` 的 pass / 处理器没有子句，只有普通的体（`(Expr * [ir] -> Expr [] (cond …))`）；
  输出 `*` 的处理器返回任意值。
- 处理器名与输入非终结符相同时可省略后者；`(LambdaOf LambdaExpr [ir] -> LambdaExpr [] …)` 是另起名字的写法。处理器在 pass 里互相可见（`letfn`），
  体里可以直接调用。处理器的输入也可以是终结符（`(symbol [ir] -> symbol [] 体…)`）：自动复制子句与到达终结符的分派会调它。
- **额外返回值**：`(Expr [ir] -> Expr [free*] …)` 的处理器返回向量 `[节点 free*]`，子句体也写向量；catamorphism `~[e free*]` 依次绑定。
  自动复制的子句返回 `[节点 返回值表达式…]`（表达式按声明写，常用处理器的形参：`-> Expr [var*]`）。pass 的缺省体调入口处理器时多出来的返回值丢掉。
- **形参的缺省值**：`(Expr [ir [env ()]] -> Expr [] …)`——catamorphism 与复制子句按名字传额外形参，作用域里没有同名变量时用缺省值。
- **处理器的选择**：catamorphism 按字段的非终结符找处理器；同一个输入非终结符有几个处理器时，优先返回值个数与结果个数相符、输出非终结符与
  结果变量的元变量相符的（`~[p1]` 找输出 Predicate 的），再优先额外形参在作用域里都有（或有缺省值）的。`~[f :> x … -> y]` 直接指定。
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
| `~[e free*]` | 处理器有额外返回值：依次绑定 |
| `~[x args … -> y z]` | 显式写出额外实参（x 只是输入的名字），结果绑定到 y、z |
| `~[f :> x args … -> y]` | 指定处理器 f（上游的 `,[f : x … -> y]`） |
| `(letrec ([~x (lambda (~y ...) ~[body])] ...) …)` | 省略号区段里的子模式（含嵌套的产生式、更深的 `...`）：逐元素匹配 |
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
| `(out (letrec ([~x1 (lambda (~x2 ...) ~body1)] ...) ~body2))` | 省略号区段里带产生式的子模板：按各 `~` 表达式（都是序列）逐元素构造 |
| `(in-context LambdaExpr (out (lambda () nil y)))` | 在模板里把当前非终结符换成 LambdaExpr |
| `(out (quote (1 2)))` | 终结符位置的列表是引用的数据，不是模板 |
| `(out (opt ~nil))` | maybe 字段为 nil |
| `(out (lambda (p q) r (set! p (quote 0))))` | 非终结符交替：Expr 上下文里直接构造 LambdaExpr |

模板里的求值顺序从左到右，序列立即实现（处理器的副作用顺序与上游相同）。模板里的名字不是本语言的产生式时展开期报错。

### 其他

- `(trace-define-pass …)`：同 `define-pass`，运行时把输入与输出（unparse 过）打印到 `*out*`；`(echo-define-pass …)` 展开期打印展开结果。
- `(pass-input-parser p)` / `(pass-output-unparser p)`：pass 的输入语言的解析函数 / 输出语言的 unparse 函数（`*` 时是 identity）。

## 3. 与上游的对照

| 上游（Scheme） | `cleat.nanopass` |
|---|---|
| `(define-language L (terminals (symbol (x))) …)` | 相同；谓词按 `名字?` 在当前命名空间决议 |
| `(extends L0)`、`(+ …)` / `(- …)` | 相同 |
| `` `(if ,e0 ,e1 ,e2) `` 模板 | `(out (if ~e0 ~e1 ~e2))`；`out` 只在输出上下文里识别，不是全局宏 |
| `,e` | `~e` |
| `,e ...` | `~@e*` 或 `~e ...`（`...` 是普通符号） |
| `,[e]` catamorphism | `~[e]`（向量，只在模式位置） |
| `,[f : x … -> y z]`（指定处理器、额外实参、多个返回值） | `~[f :> x … -> y z]`；`,[x … -> y]` → `~[x … -> y]`；`,[y z]` → `~[y z]` |
| `(maybe x)`，`#f` 表示无 | `(maybe x)`，`nil` 表示无 |
| `(define-pass p : L0 (ir) -> L1 () …)` | `(define-pass p L0 [ir] -> L1 [] …)`；额外形参与返回值用向量；形参缺省值 `(ir [env '()])` → `[ir [env ()]]` |
| `(Expr : Expr (ir) -> Expr () …)` | `(Expr [ir] -> Expr [] …)`；另起名字 `(名字 Expr [ir] -> Expr [] …)` |
| `(definitions …)` | 相同（`def` / `defn`） |
| 多个返回值 `(values a b)`、`(let-values …)` | 向量 `[a b]`、`(let [[a b] …])` |
| `(terminals (var (x)) => var-sym)`、`(var x) => x` | 相同（`=>` 美化；`(unparse-L x #t)` → `(unparse-L x true)` 不美化） |
| 同一关键字几个产生式 `(if e1 e2)` / `(if e1 e2 e3)` | 相同，按形状区分 |
| `(e0 e1)` 不以关键字开头 | 相同；`(e0 . e1)`、`(e0 ... . e1)`（点对）不支持 |
| `(Expr : * (ir) -> Expr () 体)`、`(symbol : symbol (ir) -> symbol () 体)` | `(Expr * [ir] -> Expr [] 体)`、`(symbol [ir] -> symbol [] 体)` |
| `#f` 作 maybe 的"无"、布尔 | `nil` / `false` 都是"无"；`(true)` / `(false)` 这样的产生式名要改（Clojure 的布尔字面量） |
| 元变量 `e?` | 不行（`?` 结尾留给谓词），写 `e2` |
| `nanopass-case` 每次执行重建闭包 | 展开期一次生成内联代码（IronScheme iron-034 的教训） |
| `trace-define-pass`、`echo-define-pass`、`define-parser`、`pass-input-parser`、`pass-output-unparser` | 相同（`define-parser` 生成的函数按描述解释，即 `np/parse`） |

## 4. 例子

`test/cleat/nanopass_test.clj` 与 `nanopass_pass_test.clj` 是覆盖本文每一项的小例子；`test/cleat/upstream/` 是上游测试的翻译
（`unit_tests.clj`、`compiler.clj` + `alltests.clj` + `compiler_tests.clj`：20 趟 pass 跑 194 个表达式）。`dotnet test` 的
`PackageTests.Nanopass` 跑它们（从源码，再从编出的包程序集各一遍）。原来的 Clojure 编译器（`cleat.compiler.*`，L0 ~ L3 的 `define-language`、
`define-pass` 写的 `lower-loops` / `simplify-letrec` / `uncover-free` …）是更大的例子，cleat-182 删除前的版本见 git 历史。
