# cleat-110 用户函数的已知调用：经 Var 调用、分析时 Var 的值是有 invokeStatic 的函数类时，isinst 守卫后直接 call（cleat-107 原因 7 的一部分）

- 里程碑：M11　依赖：cleat-109　状态：未开始
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 依据：经 Var 调用每次 castclass IFn、接口分派、invoke 转发到 invokeStatic；对照实验直接 call 约省 6%（装箱另计）。boot 镜像里的核心函数已有这条路（接口文件的已知函数）。
2. 分析器：Var 的当前值是 AFunction 子类实例、有参数个数相符的静态 invokeStatic（非变长子句）、类型可按名字引用（已加载的程序集，含先前求值生成的）时，
   生成 `(if (IsInstanceOf 类 r) (静态调用 类 invokeStatic r 实参…) (clj-invoke r 实参…))`；重定义照常生效（守卫）。
3. 与 cleat-104 的类型化入口衔接：调用点实参类型已知时可直调 invokeStaticTyped（或留给 cleat-114）。

## 验收标准
- browse、takl、nqueens、deriv 等前后数字；语义开 / 关对照（含重定义、with-redefs）。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
