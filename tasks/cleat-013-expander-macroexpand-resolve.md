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
