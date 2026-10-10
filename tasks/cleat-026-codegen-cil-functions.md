# cleat-026 codegen-cil：函数、调用、Var、原语内联、控制流

- 里程碑：M4　依赖：cleat-025、cleat-020　状态：未开始
- 设计依据：[04 §4 ~ §6](../designs/04-compiler.md)、[05 §4](../designs/05-cil-backend.md)

## 目标
L3 → Lcil，覆盖除 `deftype`/`case`/`try`/互操作（cleat-027）外的全部节点。

## 工作内容
1. `targets.edn` 的 `:clr` 项与读取。
2. 按上下文（value / effect / test）生成：`fn` → `AFunction`/`RestFn` 子类（字段、ctor、`invoke(k)`、`invokeStatic`、单例、`getRequiredArity`）；`let`/`label`/`jump`/`box`/`if`/`do`/`def`/`set!`/`var-ref`/`the-var`/`throw`/`monitor-*`/集合节点/`keyword-invoke`。
3. 调用：直接链接（单元内已知）、`invoke(k)`、> 20 参、`applyTo`。
4. 原语：内联模板（`Intrinsics.cs` 的那组）与静态调用；作为值时读 Var。
5. 顶层形式 → `Init<k>`；模块入口；常量表（cleat-021 的编码）。
6. `lcil.clj`：Lcil S 表达式写成文本。
7. 测试：`compile-small`——几十个小程序经 stage0 上的编译器 → Lcil → `Cleat.Emit` → 执行，与解释执行结果比较。

## 验收标准
- `compile-small` 通过；生成的 dll 过 ILVerify。
