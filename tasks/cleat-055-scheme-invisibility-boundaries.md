# cleat-055 "看不到 Scheme"的三道边界

- 里程碑：M4′　依赖：cleat-052　状态：未开始
- 设计依据：[13 §7](../designs/13-syntax-objects-and-scheme-core.md)、[14 §1 问题 3](../designs/14-three-layer-architecture.md)、[09 §2](../designs/09-testing.md)

## 目标
用测试把三道边界守住，使 Clojure 用户在语法、值、诊断三个层面都见不到 Scheme。

## 工作内容
1. 语法边界：对每个特殊形式断言 `macroexpand-1` 返回原形式；`macroexpand` 的输出只含 Clojure 数据（无 `case-lambda` / `letrec` / Scheme 符号）；`&form` 与 `defsyntax` 输出经 `syntax->datum` 只含 Clojure 集合。
2. 值边界：审计编译器对 Clojure 代码生成的全部原语调用只落在 `Cleat.Core`（`clojure.lang`）与 `cleat.kernel`；Scheme 的 fixnum / `Cons` / `Unspecified` / 可变字符串 / `SchemeChar` 不出现在 Clojure 值中（语料的数值、字符串、集合 suite 作为回归）；互操作返回值经 cleat 的 `Reflector` 转换而不是 Scheme 的 `Interop`。
3. 诊断边界：reader / 展开 / 宏 / 运行时四类错误的黄金文件（`tests/errors/`，jolt 形态）；错误文本 `Syntax error (file:line:col)`；函数类名 `ns$name`；`(class f)` 为 `ClojureFn`、`(fn? f)` 为真；CLR 栈过滤 `Cleat.Scheme.*` 帧。
4. 函数值跨层：Scheme 过程流入 Clojure 的表示只有一种（`Procedure` 实现 `IFn`，或只经 `cleat.kernel` 包装）；两方向调用测试。

## 验收标准
- 四组测试进入 `dotnet test`；审计清单记入本文件。
