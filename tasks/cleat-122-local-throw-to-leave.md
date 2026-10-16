# cleat-122 本方法内已知 throw 编成 leave

- 里程碑：M12　依赖：cleat-119　状态：关闭（2026-10-16，频率为零）
- 设计依据：[22 §3 D8](../designs/22-gc-and-performance-followups.md)、[escape 实测](../notes/escape-continuations-and-try.md)

## 工作内容
1. 统计编译器与核心库中满足 D8 条件的 throw 出现频率：throw 点与 catch 同方法、异常对象编译期已知、catch 判断恒真、不穿过可截获的嵌套 handler（注意：Clojure 核心库的 `reduced` 是包装值、`some` 是循环，都不用异常，预计频率接近零）。
2. 频率为零或可忽略时，记录统计并关闭本项，不做实现。
3. 实现受限变换：该路径编成 CIL `leave` 跳到出口（结果先落局部），原 catch 保留处理其他来源异常；finally 照常执行。
4. 语义对照：异常对象被观察、rethrow、穿过嵌套 try 等情形逐字不变。

## 背景数字（2026-10-16 微基准，本地机器）
CLR throw/catch 同方法预分配 1.8 ~ 2.0 µs，一帧深 2.3 ~ 2.6 µs；JVM 同形态 5 ns（内联后编成跳转）、新建异常 467 ns。
预分配在 CLR 上只省 0 ~ 8%。比值约 400x；fibc / ctak 的 throw 在其他栈帧，本项帮不上（见 notes）。

## 结果（2026-10-16）
- 统计（种子里的 Lcil，按 S 表达式解析 `(try (body …) (catch …))`）：boot 镜像 12992 个方法、154 个 try，编译器包 9446 个方法、40 个 try；
  **try 体内直接 `throw` 且由本层 catch 接住的：两者都是 0**（全部 259 / 372 处 `throw` 都不在带 catch 的 try 体里）。
- 与预期一致：Clojure 核心库的提前退出不用异常（`reduced` 是包装值、由 reduce 检查；`some` 等是循环）；fibc / ctak 的 throw 在别的栈帧。
- 按工作内容第 2 条关闭本项，不做实现；cleat-125（非本地逃逸协议）仍按其前置门判断。

## 验收标准
- 频率统计写进任务书；实现时受益项前后耗时与 r7rs 数字。
- 语义开关开 / 关输出逐字相同；基线、门禁、全量不变。
