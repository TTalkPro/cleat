# cleat-019 Lcil 规格（继承 + 扩展）与读取器

- 里程碑：M3　依赖：cleat-003　状态：未开始
- 设计依据：[05 §1、§2](../designs/05-cil-backend.md)、IronScheme `designs/04-lcil-and-emitter.md`

## 目标
冻结 Lcil 第 1 版规格并实现读取与校验。

## 工作内容
1. 把 IronScheme 04 §2 的格式作为基础写成 `designs/05` 的附录（或 `src/Cleat.Emit/LCIL.md`），加入 05 §2 的扩展：`interfaces`、`implements`、`interface` 类型、`volatile` 字段、`switch`、程序集属性。
2. `SExpr` 读取器（从 IronScheme 的 `SExpr.cs` 改写，Apache 许可不冲突——IronScheme 为 BSD）与 `Model`（类型 / 字段 / 方法 / 指令树）。
3. 校验：未知指令、操作数类型、标签、局部变量下标、`ldconst` 越界、`try` 入口栈为空、`switch` 标签表。
4. 测试：规格中每种子句与指令的正反例。

## 验收标准
- 规格文件冻结；读取器对规格样例全部通过，对错误样例给出带行列的错误。
