# cleat-048 可扩展语法对象

- 里程碑：M2′　依赖：cleat-047　状态：完成
- 设计依据：[15 §2](../designs/15-scheme-core-vendoring.md)

## 工作内容
1. 新库 `(cleat scheme syntax-ext)`：`syntax-ext` 记录与注册表；绑定扩展表；`syntax-error-hook`。
2. "零扩展"重构：`add-mark`、`syntax-kind?` 一族、`strip` / `strip-annotations`、`syntax-dispatch`（模式 `#(ext kind p)`）与 `syntax-case` 的模式编译、`syntax->datum`、`do-macro-call` 输出检查、quasisyntax 模板——全部改为查表，表里只有 pair / vector；快照与不动点逐字节不变（重生成种子）。
3. 注册 Clojure 集合（向量 / map / set / 带元数据对象）：wrap → `syntax->datum` 往返测试，模式匹配 Clojure 向量，模板重建 Clojure 向量并保留元数据；reader 的 `:line` / `:column` / `:file` 转注解。
4. `chi-expr` 对未知绑定种类查绑定扩展表；注册一个测试用种类验证。

## 验收标准
- 零扩展阶段产物逐字节不变；Clojure 集合的往返、匹配、模板测试通过；nanopass 快照不变。

## 结论（2026-10-10）

完成。语法对象里"按数据种类分支"的地方都改为查语法扩展表，向量是表里内建的第一项；绑定种类与语法错误各有一个扩展点。Scheme 数据的行为不变（不动点、44 个快照、种子不动点都通过），Clojure 的向量 / map / set / 列表由按需加载的库登记，往返、模式、模板、卫生、源位置都有测试。

### 注册表 API：`(boot expander syntax-ext)`（`src/scheme/boot/expander/syntax-ext.sls`，124 行）

库名用 `(boot expander …)` 而不是设计 15 写的 `(cleat scheme syntax-ext)`：它必须在 boot 镜像里、在 `core.sls` 之前展开（展开器不能导入镜像之后才定义的库），与其他展开器库同列（库名统一改由 cleat-054 做）。对外经新图例库 **`(ironscheme syntax-ext)`**（`library-legend` 的 `isx`，放在 `$all` 之前；导出名列在 `identifier->library-map` 末尾），两个构建脚本同步。

| 名字 | 说明 |
|---|---|
| `(make-syntax-extension kind pred children rebuild)`、`syntax-extension?`、`syntax-extension-{kind,pred,children,rebuild}` | 语法扩展记录：`children` 为 datum → 子项列表（map 为 k v 交替）；`rebuild` 为 `(datum 新子项列表) → datum`，保留不参与展开的部分（元数据），子项个数可以变 |
| `(register-syntax-extension! x)` | 追加到表尾；同 kind 已登记则原位替换 |
| `(syntax-extension-of datum)` | 按登记顺序第一个 `pred` 成立的项，或 `#f` |
| `(syntax-extension-by-kind kind)`、`(syntax-extensions)` | 按 kind 查；整张表 |
| `(syntax-ext-rebuild kind proto children)` | 运行时重建：`syntax` 模板 / quasiquote 对非向量种类生成对它的调用（因此是原语） |
| `(make-binding-extension kind expand-reference expand-set! expand-form macroexpand-1?)`、`binding-extension?`、`binding-extension-*` | 绑定扩展：三个展开过程签名都是 `(e value expand)`，`value` 为绑定值，`expand` 为"在当前环境展开一个子表达式"的回调，返回核心形式（`(quote d)`、`((primitive list) …)` 等，即 builders 的输出语言）；`expand-set!` / `expand-form` 可为 `#f` |
| `(register-binding-extension! x)`、`(binding-extension-of kind)` | 绑定扩展表（初始为空） |
| `syntax-error-hook` | 参数，缺省 `#f`；否则 `(hook who msg form condition)` 在 `syntax-violation*` / `syntax-error` 构造好 R6RS 条件之后、`raise` 之前调用。钩子可以改抛别的异常（Clojure 的 `ExpanderException`）；正常返回则照常抛条件 |

内建项：`(make-syntax-extension 'vector vector? vector->list (lambda (v ls) (list->vector ls)))`。**序对不进表**：psyntax 的匹配、遍历依赖 car / cdr 与非正规尾部，仍然特判。与设计 15 §2 的差别：记录没有 `pattern?` 字段（凡登记的种类都能做模式）；注册表是库内可变列表而不是参数；绑定扩展多一个 `expand-form`（以该标识符开头的形式；没有则按过程调用展开）。

### `core.sls` 的改动（行号为改后的文件）

| 函数 | 行 | 改动 |
|---|---|---|
| 导入 | 58 | 加 `(boot expander syntax-ext)` |
| `add-mark` 的内部 `f` | 479 ~ 527 | 删向量分支；在 `symbol?` 之后加 `(syntax-extension-of e) =>`：对 `children` 递归，全部 `eq?` 则返回原数据，否则 `rebuild` |
| `syntax-ext-datum`、`syntax-ext-kind`、`syntax-ext?`、`syntax-ext->list` | 538 ~ 558 | 新增：去 wrap / 注解后的裸数据、它的种类、`(syntax-ext? x kind)`、`(syntax-ext->list x kind)`（用 `syntax-dispatch` 的 `#(ext kind each-any)` 取子项，wrap 下推的方式与 `syntax-match` 的 `#(p ...)` 完全一致，所以向量的展开结果逐字节不变） |
| `syntax-vector->list`、`syntax-vector?` | 559 ~ 564 | 改为 `(syntax-ext->list x 'vector)`、`(syntax-ext? x 'vector)` 的薄包装（两者在 psyntax 里本就没有调用者） |
| `strip-annotations` | 676 ~ 685 | 向量分支 → `children` / `rebuild` |
| `strip` | 688 ~ 717 | 顶层标记分支"首个子项是注解"的判断与非顶层分支的向量重建都改查表 |
| `syntax-type` | 834 ~ 879 | 标识符：不认识的绑定种类若已登记绑定扩展，返回该种类（不再是 `other`）；序对头：已登记且有 `expand-form` 时返回该种类，否则仍是 `call` |
| `sanitize-binding` | 898 ~ 911 | 接受 `(kind . value)`（kind 已登记绑定扩展），使 `let-syntax` / `define-syntax` 能产生扩展绑定 |
| `bless` | 1282 ~ 1293 | 向量分支 → 查表 |
| `quasiquote-macro`：新 `quasiext`，`quasi` 加一条子句 | 1846 ~ 1856、1905 ~ 1906 | 向量仍走原来的 `quasivector`（产物不变）；其他种类：子项都是常量时展开期 `rebuild`，否则生成 `(syntax-ext-rebuild 'kind 'proto 子项表达式)` |
| `quasisyntax-macro` 的 `quasi` | 1967 ~ 1971 | `#(x* ...)` → 任意种类，展开期 `syntax-ext-rebuild`（向量即 `list->vector`） |
| `convert-pattern`（`syntax-case` 的模式编译） | 2289 ~ 2291、2328 ~ 2331 | 被某个 `pred` 认出的字面形状编成 `#(ext kind p)`；kind 为 `vector` 时仍写 `#(vector p)`（编进各库的模式常量不变） |
| `syntax-dispatch` | 2418 ~ 2426、2470 ~ 2471 | 新模式 **`#(ext kind p)`**：`e` 是 kind 的裸数据时，用 `p` 匹配它的 `children` 列表（wrap 照常下推）；`#(vector p)` 是 `#(ext vector p)` 的别名，二者都经新的 `match-ext` 查表；`match-empty` 认 `ext` |
| `syntax-transformer` 的 `gen-syntax`，新 `gen-ext` | 2671 ~ 2680、2749 ~ 2761 | 模板里认出的扩展数据：向量照旧走 `gen-vector`（生成 `vector` / `list->vector`，产物不变）；其他种类走 `gen-ext`：子项不变则引用原模板，子项都是常量则展开期 `rebuild`，否则生成 `(syntax-ext-rebuild 'kind '原型 子项)`，原型是"子项为空的重建结果"（只带元数据） |
| `do-macro-call` 的输出检查 | 2890 ~ 2903 | 遍历改为 `children`（裸符号检查穿过 Clojure 集合） |
| `chi-expr` 的 `else` | 3048 ~ 3054 | 已登记的绑定种类：序对调 `expand-form`，标识符调 `expand-reference`；否则仍报 "invalid expression" |
| `chi-set!` 的 `else` | 3081 ~ 3086 | 已登记且有 `expand-set!` 时调用它，否则仍报错 |
| `syntax-error`、`syntax-violation*` | 4325 ~ 4337、4369 ~ 4379 | 构造条件后先调用 `syntax-error-hook` 再 `raise` |

`chi-body*` / `chi-internal` 不用改：它们对不认识的种类走 `else`（当表达式处理），最后进 `chi-expr`。
`syntax-match`（展开器自己用的模式宏，约 950 行）的元层 `convert-pattern` **没有推广**：它在宿主的 `syntax-case` 上运行、只处理写在 `core.sls` 里的 Scheme 模式，产出的 `#(vector p)` 由新 `syntax-dispatch` 经表处理。其余 `vector-*`（rib、模块导出表、`define-record-type` 生成代码）与语法对象数据无关，未动。

### Clojure 种类：`(clojure syntax-kinds)`（`src/scheme/clojure/syntax-kinds.sls`，134 行，不进镜像）

导入即登记 `clj-vector`（`IPersistentVector`）、`clj-map`（`IPersistentMap`，子项 k v 交替、seq 顺序，重建用 `PersistentArrayMap.createAsIfByAssoc`，超过 8 对用 `PersistentHashMap.create`）、`clj-set`（`IPersistentSet`）、`clj-list`（`IPersistentList` 或 `ISeq`，重建为 `PersistentList`）；`rebuild` 都把原数据的元数据（`IMeta.meta` → `IObj.withMeta`）放回去；map / set 的重建不检查重复（剥离 wrap 后不同的语法对象可能变成相等的数据，取 assoc / conj 语义）。互操作用 `clr-call` / `clr-static-call` / `clr-is`，类型由 `TypeResolver` 在已加载的 Cleat.Core 中找到（显式参数类型，如 `(create vector)`，避免 params 重载的歧义）。另导出构造器 `clj-vector` `clj-map` `clj-set` `clj-list` `clj-keyword`、`seq->list`。
源位置：`clojure-annotate` 把元数据有 `:line`（可带 `:column` `:end-line` `:end-column` `:file`）的集合包成 psyntax 的注解 `(make-annotation 表达式 (文件 . "(行,列) - (行,列)") 原数据)`，子项递归；`clojure-datum->syntax` = `datum->syntax` + 这一步。注解后的数据 `syntax->datum` 得回原对象（`eq?`），能做模式匹配，`syntax-violation` 的条件带 `&source-information`。

### 种子与镜像

改动只用到旧展开器已有的语言，新库只是 boot 源文件表里多一项，所以 05 §5 的两步法在这里**一步即可**：旧种子直接构建出新镜像（构建脚本导入的是展开器的源码副本，boot 源码由新展开器展开），再用新镜像 `seed`、用 stage0 `seed --stage0`，两者生成的种子逐字节相同（种子 +12,821 / −12,546 行，主要是 gensym 编号顺移与新库的代码）；新种子构建出的镜像与旧种子构建的**逐字节相同**。
- 镜像：`cleat.scheme.boot.dll` sha256 `a912ebc58c3a1b7d…`（1,858,560 字节），Lcil `527e92a7c699a1d9…`（15,672,447 字符）；改前 `ddef42cf…` / `63f6deab…`。
- 不动点：`Bootstrap` 类别全部通过（stage1 == stage2 == stage3，ILVerify）。阶段 2 后再跑全量，装入的 stage3 与阶段 1 结束时逐字节相同（阶段 2 不改镜像）。

### 阶段 1 的 Lcil 差异（规范化后逐单元比较）

把改前 / 改后的 Lcil 中 gensym 计数（`g$x$N$%b`、`genN`、`x$N`）、常量下标（`ldconst` 换成常量内容）、过程编号（`PN` 换成过程名）、模块字段与闭包类型编号规范化，按"闭包类型 / `#Module` 的方法 / `#Module$P*` 的方法与 `.cctor` 条目"切成单元比较多重集：改前 8,812 个单元中 39 个、改后 8,925 个中 152 个不同，全部可归因——
- 新库：`syntax-extension*` / `binding-extension*` 的访问器、`register-*`、`syntax-ext-rebuild`、`syntax-error-hook` 等过程及其 `.cctor` 条目、模块字段、库的 `Init`；
- 展开器中改了或新增的函数：`add-mark` 的 `f`、`strip` / `strip-annotations`、`syntax-ext-*`、`syntax-vector?` / `syntax-vector->list`、`syntax-type`、`sanitize-binding`、`bless` 的 `f`、`quasi` / `quasiext`、`cvt`、`syntax-dispatch` / `match_` / `match-ext` / `match-empty`、`gen-syntax` / `gen-ext`、`do-macro-call` 的 `f`、`chi-expr`、`chi-set!` 的闭包、`syntax-violation*`、`syntax-error`，以及 `core` 库 `Init` 中这些闭包的分配；
- primlocs 库（图例）：新增 `(ironscheme syntax-ext)` 的 `install-library` 与 21 个导出名；`Initialize` 中对新库过程的登记；
- 其余是切分边界的噪声：`#Module$P*` 每类约 200 个过程，新增过程使各类 `.cctor` 的首尾条目移位。
**其他任何库（boot 的其余库、nanopass、编译器）的代码没有差异**；44 个快照逐字相同。

### 测试

- 新增 `tests/scheme/compiler/syntax-ext.sps`（`SchemeScript` 类别自动收录，镜像上约 1.8 秒），**35 项**：注册表顺序与 `syntax-extension-of`；往返（类型、顺序、各层元数据，含子项是语法对象时 `strip` 经 `children` / `rebuild`）；`syntax-case` 的 Clojure 向量模式 `[a b ...]`、map 模式、Clojure 列表模式（带字面量与嵌套向量）、Clojure 向量与 Scheme 向量模式互不匹配；模板重建 Clojure 向量 / map（替换、省略号、模板元数据保留）、常量模板、quasisyntax、quasiquote；**卫生**：宏输出一个未包装的 Clojure map（子项是语法对象），引入的 `tmp` 带上标记而用户的 `tmp` 不带，结果 `(inner outer)`；输出里含裸符号的 Clojure 向量报 "raw symbol"；源位置（`clojure-source-of`、注解后 `syntax->datum` 得回原对象、注解数据可匹配、`syntax-violation` 条件带位置）；绑定扩展（引用、形式、`set!`、`expand` 回调展开子表达式、未登记种类仍报 "invalid transformer"）；错误钩子（拿到 who / msg / form / 条件并改抛、正常返回则仍抛条件、用户调用 `syntax-violation` 也经钩子、缺省为 `#f`）；Scheme 向量的模式 / 模板 / quasiquote 不变。
- Clojure 字面量由准引用拼进形式后 `eval`（Scheme reader 读不出 Clojure 字面量），因此也验证了 Clojure 常量能进编译后的代码（进程内发射）。
- `dotnet test Cleat.slnx`：Cleat.Core.Tests 129 通过；Cleat.Scheme.Tests **218 通过**（217 + 新脚本），该程序集 3 分 36 秒，全程约 3 分 44 秒。`dotnet build` 生成镜像约 60 秒；`seed` 10 秒，`seed --stage0` 24 秒。

### 未做 / 留给后续

- Clojure 符号（`clojure.lang.Symbol`）还不是标识符：`id?` 只认 Scheme 符号，Clojure 符号在 `add-mark` 里按原子包装（cleat-049 / 050 决定 Clojure 符号与标识符的关系）。
- `syntax-ext?` / `syntax-ext->list` 只在展开器内部，没有导出（需要时随 cleat-049 加到 `(ironscheme syntax-ext)`，会改镜像）。`syntax-error` 只在 `(boot system all)` 里，用户代码测不到，钩子路径与 `syntax-violation*` 相同。
- 编译到文件（fasl 常量）的代码里还不能有 Clojure 常量：常量 fasl 合并是设计 15 §1 的另一项工作；进程内 `eval` 不受影响。
- 绑定扩展的 `macroexpand-1?` 只存不用（cleat-049 的 `macroexpand-1` 使用）；扩展绑定从库中导出（`global-macro` 一类的转换）未处理，目前只能来自 `let-syntax` / 体内 `define-syntax`。
- `syntax-match` 的元层模式编译未推广（见上）。

## 进度
- 2026-10-10 阶段 1 代码完成：新库 `src/scheme/boot/expander/syntax-ext.sls`（`(boot expander syntax-ext)`，经图例库 `(ironscheme syntax-ext)` 导出），`core.sls` 各处改为查表；两个构建脚本已加文件、图例与导出名。种子已重生成（`seed` 与 `seed --stage0` 结果相同）；旧种子与新种子构建出的镜像逐字节相同（dll `a912ebc5…`，lcil `527e92a7…`）；规范化后的 lcil 差异只在展开器函数、新库与 primlocs。下一步：全量测试，然后阶段 2（`src/scheme/clojure/syntax-kinds.sls` 与测试）。
- 2026-10-10 阶段 1 全量测试通过（Cleat.Core.Tests 129，Cleat.Scheme.Tests 217，含不动点与 44 个快照）。阶段 2 代码完成：`src/scheme/clojure/syntax-kinds.sls`、`tests/scheme/compiler/syntax-ext.sps`（35 项全部通过）。下一步：全量测试并核对镜像与阶段 1 相同，写结论、改 TASK.md。
- 2026-10-10 完成：全量测试通过（Core 129、Scheme 218），阶段 2 后镜像与阶段 1 相同；结论已写，TASK.md 已改为完成。
