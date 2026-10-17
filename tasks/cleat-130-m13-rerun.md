# cleat-130 M13 收尾：数组推测微基准、r7rs 无提示变体与全量重测

- 里程碑：M13　依赖：cleat-127、cleat-128　状态：完成（2026-10-17）
- 设计依据：[23 §3](../designs/23-type-specialization.md)

## 工作内容
1. linux01（192.168.0.10）上 r7rs 56 项与无提示变体，cleat 与 JVM（OpenJDK C2、GraalVM）各 3 次。
   192.168.3.111 已离线；所有对照都在同一窗口按基准 / 轮次交错执行。
2. 微基准：无提示数组循环与 eval-only 类型反馈候选；确认类型反馈在标准文件路径保持零触发。
3. 更新 designs/23 的结论与 notes/r7rs-performance.md。

## 验收标准
- 数字写进任务书；基线、门禁、全量不变；种子不动点。

## 测量资产与兼容性修复

- `bench/m13_micro.clj`：无提示 long / double / object 数组循环，以及 50M 次两参 eval 候选。
- `bench/r7rs/nohint.py`：生成只移除数组元素提示（`objects` / `longs` / `doubles` 等）的 r7rs 变体；
  保留 `^long` / `^double` 标量提示，避免混入 M11 收益。本轮共改写 22 个 r7rs 文件。
- `bench/r7rs/run.py` 支持 `CLEAT_R7RS_SRC`；`run130.py` 在同一基准窗口交错跑 cleat / C2 / GraalVM，
  `summarize130.py` 按最小值汇总。原始 JSONL 属于生成结果，不入库。
- 为 JVM 兼容性，`fibc.cljc` / `ctak.cljc` 改成 reader conditional：cleat 分支保留语言层 `escape*`，
  `:clj` 分支用 `r7rs.util/call-with-escape`；并修复 `ctak-aux` 的括号。两例在 cleat、OpenJDK C2 都通过。

## 结果（2026-10-17，linux01）

### 数组推测微基准

五次交替取中位数，`on` 是 cleat-127 开启，`off` 是 `CLEAT_NO_OPT=arrayspec`：

| 场景 | on | off | off / on |
|---|---:|---:|---:|
| long-sum-u | 2.111 ms | 42.681 ms | 20.2x |
| long-fill-u | 1.396 ms | 153.610 ms | 110x |
| double-sum-u | 3.204 ms | 8.820 ms | 2.75x |
| object-count-u | 3.253 ms | 3.892 ms | 1.20x |

同一微基准里 typefeedback 开 / 关为 141.060 / 85.180 ms（50M 次直接调用），关闭快约 1.656x；
残差 56 ms，约 1.12 ns / 次。`on` 触发 1 个两参候选，多态候选被抑制；`off` 的 summary 是
`reports=0 triggered=0 profiled-calls=0`。这进一步支持 cleat-129 的关闭决定。

### r7rs 三方对照

56 项、3 轮取最小值，比率大于 1 表示 cleat 慢：

| 变体 | cleat / C2 | cleat / GraalVM | GraalVM / C2 | 完整样本 |
|---|---:|---:|---:|---:|
| normal | 1.672 | 1.791 | 0.934 | 56 |
| nohint | 1.817 | 1.936 | 0.937 | 55 |

nohint 缺 `maze`：移除数组提示后 cleat 编译 `print-hexmaze` 报
`codegen: unbound variable g$t$…$inl`；`CLEAT_NO_OPT=arrayspec`、`loopspec` 或 `infer` 都能通过，
说明是数组推测与循环推测 / 内联临时变量交互触发，而不是基准源语法问题。该问题不在本测量任务里改编译器，
已留给后续编译器任务。JVM 两列不受影响。

### 数组提示的贡献

`nohint / normal` 的 cleat 时间比只取同一基准都成功的 21 个改写文件，几何平均 **1.281**——
数组提示整体带来约 22% 的时间下降。最明显的回退：fft 7.58x、simplex 2.63x、triangl 2.48x、
mbrot 1.65x、quicksort / conform 约 1.36x。array1、matrix 等在开 / 关之间基本持平，
说明 array1 的主要收益来自已有循环推测，而不是元素数组提示本身。
JVM 对照几乎不变：同 22 项 C2 几何比 0.997，GraalVM 1.009，确认上述差异来自 cleat 编译器。

按全部 55 个两变体都成功的项计算，nohint / normal 的几何比是 cleat 1.101、C2 1.004、GraalVM 1.008。

## 验证

- `dotnet build -c Release` OK；`dotnet test -c Release --no-build` 与基线一致。
- 语料门禁 521 行 OK；cts 6868 / 29 / 6，test-clojure 3688 / 18。
- 本任务只改基准资产、r7rs 基准源与文档，不改编译器 / runtime / codegen；种子输入未变，
  `tools/build.cs -- selfhost` 确认不动点。
