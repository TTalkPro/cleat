# cleat-051 编译器的 Clojure 节点

- 里程碑：M4′　依赖：cleat-050　状态：未开始
- 设计依据：[15 §3](../designs/15-scheme-core-vendoring.md)、[05](../designs/05-cil-backend.md)、[02 §4](../designs/02-runtime.md)

## 工作内容
1. L0 ~ L3 加节点：`var-ref` / `def` / `set!` 到 Var、`keyword-invoke`、`instance-of`、`deftype` / `reify`、`switch`（`case*`）、多类型 `$try/catch`、集合字面量；`targets.sls` 加 Clojure 运行时类名。
2. 调用约定：`ClojureFn : Procedure, IFn`（`invoke(k)` 0 ~ 20、`invokeStatic`、`RestFn` 变长）；Clojure 调用点发 `IFn.invoke(k)`；已知调用直接 `call`；`loop` / `recur` 复用 `label` / `jump`。
3. codegen：`deftype` → Lcil `type` + `implements` + 可变 / volatile 字段；`switch`；`try/catch` 多子句；`tail.` 对 Clojure 代码按机器描述表关闭。
4. `compile-small` 的 Clojure 版本（几十个小程序经编译 → CIL → 运行，与预期值比较）。

## 验收标准
- `compile-small` 通过；产物过 ILVerify。
