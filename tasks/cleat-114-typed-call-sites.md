# cleat-114 类型化调用点与小函数（cleat-107 原因 7；designs/20 §6 的候选）

- 里程碑：M11　依赖：cleat-113　状态：未开始
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 非递归的纯数值函数也有类型化入口；调用点实参类型已知时直调 invokeStaticTyped，结果用 Num 承接（Var 被重定义时走通用调用，不需要退回）。
2. let 绑定、不被赋值的已知 lambda：直调其静态方法（与 cleat-110 的已知调用合并考虑）。
3. 小的访问函数（可变序对 mcar / mcdr 一类）跨调用的开销：量化后决定是否做同单元内联。

## 验收标准
- browse、destruc、lattice、puzzle、takl 前后数字；语义开 / 关对照。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
