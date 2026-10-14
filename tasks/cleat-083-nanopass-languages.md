# cleat-083 cleat.nanopass 语言层

- 里程碑：M8（阶段 A）　依赖：cleat-081　状态：未开始
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
