# cleat-122 本方法内已知 throw 编成 leave

- 里程碑：M12　依赖：cleat-121　状态：未开始
- 设计依据：[22 §3 D8](../designs/22-gc-and-performance-followups.md)、[escape 实测](../notes/escape-continuations-and-try.md)

## 工作内容
1. 统计编译器与核心库中满足 D8 条件的 throw 出现频率：throw 点与 catch 同方法、异常对象编译期已知、catch 判断恒真、不穿过可截获的嵌套 handler（内联后的 `reduced` / `some` 一类提前退出）。
2. 频率为零或可忽略时，记录统计并关闭本项，不做实现。
3. 实现受限变换：该路径编成 CIL `leave` 跳到出口（结果先落局部），原 catch 保留处理其他来源异常；finally 照常执行。
4. 语义对照：异常对象被观察、rethrow、穿过嵌套 try 等情形逐字不变。

## 背景数字（2026-10-16 微基准，本地机器）
CLR throw/catch 同方法预分配 1.8 ~ 2.0 µs，一帧深 2.3 ~ 2.6 µs；JVM 同形态 5 ns（内联后编成跳转）、新建异常 467 ns。
预分配在 CLR 上只省 0 ~ 8%。比值约 400x；fibc / ctak 的 throw 在其他栈帧，本项帮不上（见 notes）。

## 验收标准
- 频率统计写进任务书；实现时受益项（`some` / `reduced` 一类写法）前后耗时与 r7rs 数字。
- 语义开关开 / 关输出逐字相同；基线、门禁、全量不变。
