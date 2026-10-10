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

## 结论（2026-10-10）

完成。
- 规格冻结在 `src/Cleat.Emit/LCIL.md`：继承 IronScheme 04 §2 的全部内容，扩展 `(interfaces …)` 上的 `(implements "I" "名字")` 显式接口实现、`interface` 类型的约束、字段属性 `volatile`、程序集与类型级 `(attribute "T" "字符串参数" …)`、`switch`（本就是普通 CIL 指令）、入口属性改为 `Cleat.Core.CleatModuleAttribute`。
- `SExpr.cs`（读取器）与 `Model.cs`（模型、解析、校验）从 IronScheme（BSD）移植到 `Cleat.Emit`，加入上述扩展与校验：`implements` 的接口须在 `interfaces` 中、接口类型不得有实例字段或非抽象方法。
- 测试 `LcilParserTests`（3 项，覆盖全部子句、16 种错误样例带行列、读取器词法）通过。
