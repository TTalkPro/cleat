# cleat-007 Var、Namespace、引用类型、STM 与 MultiFn

- 里程碑：M1　依赖：cleat-006　状态：未开始
- 设计依据：[02 §3](../designs/02-runtime.md)

## 目标
提取 `Var`、`Namespace`、`Atom`、`Ref`/`LockingTransaction`、`Agent`、`Volatile`、`Delay`、`Reduced`、`MultiFn`、`MethodImplCache`。

## 工作内容
1. 复制改写；`Var` 的线程绑定用 `ThreadLocal`（按 cleat-003 的结论决定是否 `AsyncLocal`）。
2. `Namespace` 的 `refer`/`alias`/`import` 表；`Namespace.find`/`findOrCreate`。
3. `Agent` 的线程池用 `ThreadPool`/`Task`；`Future`/`Promise` 用 `Task`。
4. 测试：根绑定、`binding` 的线程隔离、`alter-var-root`、STM 的冲突重试、`MultiFn` 的层次分派与 `prefer-method`。

## 验收标准
- 测试通过；`Var.getRawRoot`/`deref` 的区分可供编译器使用。

## 结论（2026-10-10）

完成（随 cleat-006 的整体提取进入 `clojure.lang/`，本任务只做核对与测试）。
- `Var`、`Namespace`、`Atom`、`Ref`/`LockingTransaction`、`Agent`、`Volatile`、`Delay`、`Reduced`、`MultiFn`、`MethodImplCache` 均已在 `src/Cleat.Core/clojure.lang/Lib/`，无编译器引用。
- 线程绑定：`Var` 用 `[ThreadStatic]`（cleat-003 实测优于 `ThreadLocal`/`AsyncLocal`；新线程不继承绑定，与 Clojure 一致）。`Agent` 沿用 `ThreadPool`；`Future`/`Promise` 的 `Task` 化留给 `core.clj` 适配（cleat-017）。
- 测试 `VarTests`（根绑定 / 线程绑定隔离 / `set` / `alterRoot` / 未绑定 `set` 报错 / `setMacro` 与元数据）+ `ClojureLangSmokeTests` 中的 Atom 多线程、Ref 事务、Delay、MultiFn 用例。
