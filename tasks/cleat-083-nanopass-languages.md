# cleat-083 cleat.nanopass 语言层

- 里程碑：M8（阶段 A）　依赖：cleat-081　状态：完成
- 设计依据：[19 D2](../designs/19-clojure-self-hosting.md)、[07 D1 ~ D3、D5、D6](../designs/07-nanopass-port.md)

## 目标
Clojure 写的 `define-language`：能逐字表达 `languages.sls` 的 L0 ~ L3。

## 工作内容
1. `src/clojure/cleat/nanopass/`：语言描述的数据表示（terminal / nonterminal / 产生式 / 元变量与后缀规则）、`define-language`（terminals、entry、`extends` 的 `+` / `-`、`maybe`、一层省略号含 `e* ... e`）。
2. 记录：每产生式一个 `deftype`（字段按元变量名 + int 标签，引用相等）、带检查的构造函数、谓词、访问器；标签与类型名只取决于定义顺序。
3. `unparse-L`（输出与 Scheme 版 `unparse-Ln` 相同形状的列表数据，供差分）、S 表达式 → 记录的解析（测试用）。
4. 语言注册表：宏展开期与加载期都登记，跨命名空间、AOT 后可查。
5. L0 ~ L3 用 Clojure 拼写写出（`cleat.compiler.languages`），含 `clj-fn-name` 记录。

## 验收标准
- 测试：L0 ~ L3 的构造、谓词、`extends` 的增删、`maybe`、省略号字段、错误的构造参数报错；同一源码两次展开结果相同。
- 对 Scheme 快照（`tests/scheme/compiler/snapshots/*.l0` 等）里的每个 IR：解析成 Clojure 记录再 unparse，得到相同数据。

## 结论（2026-10-14）

完成。

**文件**

| 文件 | 内容 |
|---|---|
| `src/clojure/cleat/nanopass/records.clj` | `INode`（`nanoTag` 产生式序号、`nanoFields` 字段向量、`nanoLang` 语言限定名）、语言的运行时对象 `Language` 与运行时注册表、构造检查的辅助函数 |
| `src/clojure/cleat/nanopass/language.clj` | 语言描述（纯数据）：子句解析、`extends` 的 `+` / `-`（终结符、产生式、整个非终结符）、元变量后缀规则、字段 / 标签 / 类型名的注解、宏展开期的描述注册表；按描述数据驱动的解析与 unparse（上游生成代码的部分改为普通函数，07 D1）、`language->s-expression` |
| `src/clojure/cleat/nanopass.clj` | 对外入口：`define-language`、`language`、`parse`、`unparse`、`language->s-expression`、节点的打印（`#<L0 (if a b c)>`） |
| `src/clojure/cleat/compiler/languages.clj` | `languages.sls` 的 L0 ~ L3 逐字移植，`CljFnName` 记录（Scheme 的 `clj-fn-name`） |

**展开结果**（每个语言）：标记接口 `L0$Node`；每个产生式一个 `deftype L0-Expr-if [e0 e1 e2]`（实现 `INode` 与标记接口，引用相等）；
非终结符谓词 `L0:Expr?`（标记接口 + 标签 `case`，再试终结符谓词）；带检查的构造函数 `make-L0:Expr:if`（`*check-constructors*` 在展开期读取，缺省开；
省略号字段逐个检查、`maybe` 接受 nil / false）；产生式谓词 `L0:Expr:if?`；运行时对象 `L0`；`unparse-L0`。标签按定义顺序 0 起，类型名 = 语言-非终结符-关键字，
展开结果不含 gensym（同一源码两次展开相同）。`extends` 与上游一样不共享记录类型。

**与上游的差别**：产生式必须以关键字开头（07 D3）；同一层列表只允许一个 `...`；`maybe` 的"无"是 nil 或 false；
终结符谓词为 `名字?`，在定义语言的命名空间里决议，描述里存限定符号。

**验证**（`tests/nanopass`，由 Host 测试 `NanopassTests` 从源码运行，2/2）：
- `cleat.nanopass-test`：构造与谓词、字段访问、构造检查（含两层省略号与 `maybe`）、解析与 unparse 往返、`extends` 的终结符 / 产生式增删与新增非终结符、
  不共享记录类型、`language->s-expression`、定义错误的报错、展开确定性。
- `cleat.compiler.languages-test`：Scheme 编译器的快照 `tests/scheme/compiler/snapshots/*.l0 / .l2 / .l3`（全部 87 个；测试里带一个只认快照用到的 Scheme 语法的读入器，
  `#f` → false）逐个按 L0 / L2 / L3 解析成记录再 unparse，与原数据相同；L1 的 letrec 只绑 lambda、L3 的自由变量表、`CljFnName` 作为过程名。

**记下的事实**：从源码加载 `cleat.compiler.languages` 要 7.7 s（4 个语言约 200 个 deftype、400 个 defn，逐个形式 eval 各发射一个程序集）；cleat-085 预编译成程序集后不再有这个开销。
