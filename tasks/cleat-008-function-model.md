# cleat-008 函数模型 IFn / AFn / AFunction / RestFn

- 里程碑：M1　依赖：cleat-006　状态：未开始
- 设计依据：[02 §4](../designs/02-runtime.md)、[00 D3](../designs/00-overview.md)

## 目标
无 DLR 的函数基类，`RestFn` 的 arity 打包逻辑放在基类。

## 工作内容
1. `IFn`（0 ~ 20 + params、`applyTo`）、`AFn`（默认抛 `ArityException`、`applyTo` 按长度分派）、`AFunction`（元数据、`IComparer` 适配、`^:once` 支持）、`RestFn`（`getRequiredArity`、`doInvoke` 重载、基类实现全部 `invoke(k)` 打包）。
2. `ArityException` 用 `Munge.demunge`。
3. 一个 C# 手写的 `AFunction` 子类与 `RestFn` 子类作为测试样例；`apply` 的 `applyTo` 路径。
4. 测试：arity 错误信息与 Clojure 一致（"Wrong number of args (3) passed to: ns/name"）；`RestFn` 在 required=2 时 `invoke(1)` 报错、`invoke(5)` 打包。

## 验收标准
- 测试通过；基类无生成代码依赖。

## 结论（2026-10-10）

完成（核对与测试）。
- `IFn`（0 ~ 20 参 + `params` + `applyTo`）、`AFn`（默认抛 `ArityException`，`applyTo` 按长度分派）、`AFunction`（元数据、比较器）、`RestFn`（`getRequiredArity` 抽象，基类实现全部 `invoke(k)` 打包剩余参数后调 `doInvoke`）已随提取进入，`AFn` 不再实现 `IDynamicMetaObjectProvider`。`AFnImpl` / `RestFnImpl`（闭包式实现，ClojureCLR 供生成代码用）保留给 stage0 的解释器闭包备用。
- `ArityException` 的信息 "Wrong number of args (n) passed to: name" 经 `Munge.demunge`。
- 测试 `FunctionModelTests`：固定 arity 错误信息与 `Actual`、`applyTo`、`RestFn` 打包（`(1 2 nil)` / `(1 2 (3 4))`）、超过 20 参的 `params` 重载、关键字 / map / 向量 / 集合作为函数。
