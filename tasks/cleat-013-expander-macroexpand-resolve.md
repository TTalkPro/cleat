# cleat-013 展开器：宏展开与符号决议

- 里程碑：M2　依赖：cleat-009、cleat-010、cleat-012　状态：未开始
- 设计依据：[03 §2、§3](../designs/03-language-core.md)、[00 D4](../designs/00-overview.md)

## 目标
`Cleat.Core.Expander`：顶层形式 → Lcore（除 cleat-014 的特殊形式细节）。

## 工作内容
1. `macroexpand-1`/`macroexpand`/`macroexpand-all`：Var `:macro` 判定、`&form`/`&env`、`:inline`、互操作糖（`.m`、`T/m`、`T.`、`T/.m`、`T/new`）改写。
2. 符号决议：词法环境（`LocalBinding`：名字、唯一化名、类型提示、是否 arg/loop/field）、特殊形式、Var（`ns/name`、别名、refer、`*ns*`）、宿主类型与静态成员；错误信息与 Clojure 一致。
3. Lcore 输出：`const`/`local`/`var-ref`/`the-var`/`if`/`do`/`let`/`loop`/`recur`/`fn`/`letfn`/`def`/`set!`/`throw`/`try`/`monitor-*`/`host-*`/`instance-of`/`import`/`call`/`vector`/`map`/`set`/`keyword-invoke`；源位置。
4. 实现 `IReaderResolver`（cleat-009）。
5. `eval` 钩子：展开 → `RuntimeHooks.Compile`（stage0 登记解释器；新系统登记编译器）。
6. 测试：每个节点的 Lcore 快照（EDN）；错误信息快照。

## 验收标准
- `core.clj` 中不含 `case*`/`deftype*`/`reify*` 的顶层形式全部能展开为 Lcore。

## 结论（2026-10-10）

完成（与 cleat-014 一起实现在 `src/Cleat.Core/Expander/`，命名空间 `Cleat.Core.Expand`）。
- `Expander.cs`（约 900 行）：`ExpandTopLevel` 一个顶层形式 → Lcore；`Macroexpand1` / `Macroexpand`（规则同 ClojureCLR `MacroexpandSeq1`：宏 Var 的 `&form &env` 调用、`ArityException` 隐藏两个参数、`.m` / `T.` / `T/m` / `T/.m` / `T/new` 糖改写为 `.` / `new`）；`:inline` 与 `:inline-arities`；符号决议（词法 → `Type/member` → `Resolve`：Var / Type / `ns` / `in-ns` / `*allow-unresolved-vars*`）；`LookupVar`（`def` 时在当前命名空间 intern）；`:const` Var 折成常量；`(instance? T x)` → `instance-of`；`(:k x)` → `keyword-invoke`；集合字面量全常量时整体为常量，否则 `vector` / `map` / `set`，用户元数据经 `with-meta`；类型提示 → `(tag "T" e)`；错误为 `ExpanderException`（含 `:clojure.error/*` 数据与 `Syntax error (file:line:col)` 信息）。
- `Lcore.cs`：节点名、`LocalBinding`（作为 `&env` 的值）、`ExpanderException`。`Expander.ReaderResolver` 实现 `IReaderResolver`。
- 词法变量唯一化 `name__N`，计数每个顶层形式重置（08 §7）。
- 测试 `ExpanderTests`（13 项 / 约 110 个断言）：常量与集合、符号与 Var、控制形式与 recur 规则、fn 的 arity 规则、def / set!、宏 / inline / `&env` / 局部遮蔽宏、调用、宿主互操作全部糖、try、case*、deftype* / reify*、reader 决议、顶层 do。
