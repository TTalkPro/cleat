# cleat-118 类型化调用点（cleat-107 原因 7；designs/20 §6 的候选）

- 里程碑：M11　依赖：cleat-117　状态：未开始
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 非递归的纯数值函数也有类型化入口；调用点实参类型已知时直调 invokeStaticTyped，结果用 Num 承接（Var 被重定义时走通用调用，不需要退回）。
2. let 绑定、不被赋值的已知 lambda：直调其静态方法（与 cleat-110 的已知调用合并考虑）。
3. （小函数跨调用的开销已由 cleat-116 的 eval 内联处理。）
（镜像与包的分层编译见 cleat-117。）

## 验收标准
- browse、destruc、lattice、puzzle、takl 前后数字；语义开 / 关对照。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
