# cleat-120 array1 的 OSR 诊断与保守分层规则

- 里程碑：M12　依赖：cleat-119　状态：未开始
- 设计依据：[22 §1 D1 ~ D2](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 先复测（2026-10-16 两机实测的前提：r7rs 尺度上内联 / tier 开关对 array1 只有 0 ~ 11%，server+gen0 下不可见，见 [notes](../notes/gc-config-and-tier0-two-machines.md) §3）：确认回退仍显著才继续；不显著则记录并关闭本项。
2. 从 `TMP_DUMP_EVAL` / Lcil 转储抽出 `array1/go` 的最小复现，保留 loop、长运行调用、异常表与 local slot 形态。
3. 用 OSR 阈值、`DOTNET_JitDisasm` / JIT 日志与 Tiered Compilation 开关查清 `go` 不进入 OSR 的原因；逐项排除显式尾调用、localloc、补丁点求值栈不空、异常区域、方法大小与参数 slot 使用。
4. 若为 .NET JIT 限制，记录运行时版本与最小复现；若可修，修分层 / Lcil 生成规则。
5. 增加保守分层规则：只调用一次、循环内调用长运行或不可判断耗时函数的方法，不能停留 tier-0；必要时直接优化编译或不内联该调用。

## 验收标准
- `array1` 的 `go` 进入 OSR / 优化代码，或明确记录不可修复的宿主限制并启用保守回退。
- array1 前后耗时、GC 各代次数、存活数组轮数、Lcil / IL 快照写入任务书。
- `CLEAT_NO_OPT` 相关开关开 / 关输出相同；基线、门禁、全量不变。
