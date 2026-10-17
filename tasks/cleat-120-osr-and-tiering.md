# cleat-120 array1 的 OSR 诊断与保守循环内联规则

- 里程碑：M12　依赖：cleat-119　状态：完成（2026-10-17）
- 设计依据：[22 §1 D1 ~ D2](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 先复测（2026-10-16 两机实测的前提：r7rs 尺度上内联 / tier 开关对 array1 只有 0 ~ 11%，server+gen0 下不可见，见 [notes](../notes/gc-config-and-tier0-two-machines.md) §3）：确认回退仍显著才继续；不显著则记录并关闭本项。
2. 从 `TMP_DUMP_EVAL` / Lcil 转储抽出 `array1/go` 的最小复现，保留 loop、长运行调用、异常表与 local slot 形态。
3. 用 OSR 阈值、`DOTNET_JitDisasm` / JIT 日志与 Tiered Compilation 开关查清 `go` 不进入 OSR 的原因；逐项排除显式尾调用、localloc、补丁点求值栈不空、异常区域、方法大小与参数 slot 使用。
4. 若为 .NET JIT 限制，记录运行时版本与最小复现；若可修，修分层 / Lcil 生成规则。
5. 增加保守规则：循环体里的用户函数调用不在 analyzer 内联——内联子句展开后仍含真实调用（`clj-invoke` / `clj-static-call` /
   `clj-keyword-invoke` / `KnownCall` / `PrimInvoke`）、调用点在 `loop*` 体（`:in-loop`，`fn*` / `catch` 清除）、且目标 Var 不在
   `clojure.core` 时保留普通调用；`CLEAT_NO_OPT=loopinl` 关闭。纯算术子句和 core 助手照旧内联，保留 fft 的收益。
   运行时静态快路径与局部 lambda 调用不算"真实调用"。镜像 / 包构建把 `restrict-loop-inlines?` 关掉，保证 eval 与自举产物
   的内联决定一致、种子不动点不被跨级反馈破坏。

## 结果（2026-10-17）

- 根因确认：`array1/go` 内联 `my-try` 后停留插桩 tier-0；限制规则只阻止 `#'r7rs.array1/my-try 1`，`destruc` 只剩
  `#'clojure.core/apply 2` 被暂时挡住，证明初始"所有带调用的循环子句"太宽。收窄到非 core 后 `destruc` 不受影响。
- JIT 证据：限制开启时 `create_x` / `create_y` 到达 `Tier1-OSR`；`CLEAT_NO_OPT=loopinl` 复现旧形态。
- array1 对照：r7rs 同窗口限制开 14.3 s、开关关 19.6 s；本机 500 × 1,000,000 脚本复测开 13.98 s、关 17.88 s。
  .NET GC 次数（脚本内 `System.GC/CollectionCount` 差值）：开 1217 / 1216 / 500（gen0 / gen1 / gen2），关 1020 / 1019 / 303。
  开启后回收次数更多但存活区间更短、总耗时更低；`create_y` 的上一轮数组不再被内联产生的 JIT 临时变量多抓一轮。
- 其他基准：初始宽规则曾测出几何平均 1.026 回退；收窄到非 core 后安静同窗口抽样基本持平。第一轮 r7rs A/B 曾被
  开机后另一 JVM 应用和机器负载污染（同 cleat-123 的教训），只采信同窗口安静环境结果。

## 测试与验证

- `tests/Cleat.Host.Tests/data/loopinl_semantics.clj` 覆盖循环内用户 wrapper、循环中重定义 wrapper、实参副作用与异常顺序；
  `CLEAT_NO_OPT=loopinl` 开 / 关输出逐字节相同。测试：`UserCallsInLoopsAreNotInlined`，种子测试
  `BuildFromTheSeedReproducesTheSeed`。
- 种子：`dotnet run tools/build.cs -- clojure-seed` 通过，2076 KiB；种子重生成的 `cleat.boot.dll` 与 `cleat.compiler.dll`
  和 stage3 输出逐字节一致，`src/boot/seed/*.gz` 已更新。
- 门禁 521 行 OK；cts 6868 通过 / 29 失败 / 6 错误、test-clojure 3688 通过 / 18 失败，均为既有基线；自举 stage2 ==
  stage3 逐字节一致。

## 验收标准
- `array1` 的 `go` 进入 OSR / 优化代码，或明确记录不可修复的宿主限制并启用保守回退。
- array1 前后耗时、GC 各代次数、OSR / tier 状态写入任务书。
- `CLEAT_NO_OPT` 相关开关开 / 关输出相同；基线、门禁、全量不变。
