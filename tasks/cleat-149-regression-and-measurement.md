# cleat-149 parsing / mbrot / pi 回归排查与测量口径修正

- 里程碑：M16　依赖：无（宜早做，避免污染后续 A/B）　状态：完成（2026-10-20，结论 / 口径 / 工具见下方与 `notes/m16-regression-triage.md`；修复动作按本任务限定不在本任务改代码，列 §7 待办）
- 设计依据：[28 §5](../designs/28-hot-path-optimizations.md)

## 问题

同机同 JVM 下 cleat 相对 cleat-118 的回退：parsing +24%、mbrot +28%、pi / dynamic +15%、
equal / deriv / cpstak +8 ~ 10%。这些项对 GC 配置不敏感（归因实验 2），
疑似 M14/M15 的入口选择与特化规则（cleat-138 的 RuntimeSigTable 兼容条目 / 负表 / LRU）副作用。
另有两条口径问题：takl 的 JVM 数字是双态（快 ~12 s / 慢 34 ~ 37 s），min-of-3 不可靠；
微探针若结果不参与数据依赖，JVM 侧数字会被 LICM / EA 消掉（shapes3 的教训）。

## 工作内容

1. 开关矩阵：parsing / pi / mbrot 三项在 `CLEAT_NO_OPT=sigspec,typed,loopinl,…` 各组合下
   同窗口重测，定位回退来源。
2. 开关不能定位时 `git bisect 6ed0da4..65d407d`（每项 3 轮取最小，安静窗口）。
3. 找到后修复或在设计 / notes 记录取舍；确认修复不撤销 M14/M15 的收益（ctak / fibc / destruc）。
4. 测量口径：
   - run.py / compare.py 对双态项报告区间（min ~ median 或全部轮次），takl 单列注记；
   - `micro.cljc` / `shapes2.cljc` / `shapes3.cljc` 对照纳入里程碑收尾清单；
   - "探针结果必须参与数据依赖，否则 JVM 侧数字不可用"写进 run.py / attr.py 与探针文件注释。

## 验收

- 三项回退各有结论：修复（同窗口 A/B 恢复且其余项不回退）或记录为已知取舍；
  门禁与两套基线不变；改 codegen 后种子不动点。
- run.py / 探针注释更新；notes/r7rs-performance.md 记录本轮结论。

## 结论（2026-10-20）

排查记录在 **`notes/m16-regression-triage.md`**（§1 开关全枚举 / §2 端点复现性 / §3 开关矩阵 /
§4 为什么没跑 commit 级 bisect / §5 口径修正 / §6 剩下的坑 / §7 待实施修法）。要点：

1. **回退不在 M16**。三构建同窗口交替（old = 6ed0da4 / cleat-118，m15 = 65d407d，head = 4b29e38，
   即 m15 + 142 / 143 / 146），`head / m15` 四项配对中位 parsing x1.120、mbrot x1.044、pi x0.961、
   dynamic x0.949，几何平均 **x1.016**，且**每一项的逐轮配对都跨越 1** → 不构成可判定回退。
2. **回退在 M14 / M15**：`old / m15` 在 mbrot / pi / dynamic 上**五轮配对全部 < 1**（x0.904 / x0.937 /
   x0.829，几何平均 x0.910）→ 相对 cleat-118 的方向一致的回退是真的，量级 7 ~ 21%；
   parsing 是唯一方向不一致的项（0.814 ~ 1.103），任务书里的 "parsing +24%" 在本机不复现。
3. **本机噪声地板**：等过 `load1 <= 核数 x 25%` 之后，同一构建同一项的 5 轮极差仍有 **17 ~ 43%**。
   所以本机只用来判方向（5/5 一致性），任何 < 10% 的判定必须在 192.168.3.111（极差 < 3%）上做。
   这条是本任务对后续 A/B 最重要的口径产出。
4. **开关矩阵（§3）**：单开关关闭**不是线性成本**。`CLEAT_NO_OPT=host` 让 mbrot 从 0.7 s 涨到
   **147 ~ 158 s（≈200x，两轮复现）**、关 `infer` 涨 14x，而"全关但不含 host"只有 0.6 s。
   矩阵里 > 2x 的格子只能当"这条退化路径不能被整体关掉"的证据，不能反推开关的收益；
   退化路径本身是健壮性缺陷，记进 §7 第 8 条。
5. **bisect 没跑满的原因**（§4）：`bisect149.py` 就位，但 6ed0da4..65d407d 有 31 个 commit，
   在本机噪声地板上跑出来的是噪声；改做"三构建端点 + 单开关矩阵"，把候选面收到
   `sigspec / typed / loopweight / typefeedback / collapse / scalar / imagetier / tiered`，
   commit 级定位挪到 111 安静窗口（§7 第 1 条）。
6. **口径修正已落到代码**：新增 `bench/r7rs/src/r7rs/probe.cljc`（`timed` 宏把结果写进 sink 槽位 +
   环底对照 + `ARTIFACT?` / `DEAD-VALUE` 两档自检）、`micro.cljc` 的 net 列与 `SUSPECT` 标记、
   `shapes2/3` 全部改造；`run.py` / `attr.py` 头注写入"探针结果必须参与数据依赖"；
   `compare.py` 重写为报 min/median/max + 全部轮次 + 双态区间 + 三档几何平均；
   `triage149.py` / `summarize149.py` / `bisect149.py` / `probes.py` 为本轮工具。
   自检实测（§5.2）：`dead-mcar` 的 JVM 净时间 −0.1 ns、`dead-iface-direct` −2.81 ns、
   `dead-eq-char` −2.67 ns —— M15 的"char 常量比较慢 11.7x"就是这样来的。
7. **takl 双态（§5.3）**：cleat 侧 17.21 ~ 17.87（极差 1.04）单态；JVM 侧批内 34.77 ~ 37.29
   但同机曾测 12.05 → 双态是**态间**差异，不能只看一轮极差。倍数只能写成区间 **0.46x ~ 1.43x**，
   含 takl 的几何平均按快 / 慢态差约 0.03x（1.61x → 1.64x）。本机 GraalVM 下 takl 是单态
   （21.78 ~ 22.86），说明双态是 C2 的特性 —— 跨宿主比较 takl 前先确认 JVM 实现。
8. **新增的验证纪律（§6 第 1 条）**：并行 worktree 下 Host 全套不可信，四类互不相干的假失败
   （submodule 未 checkout / 共享 `/tmp/cleat-clojure-bootstrap` 与 `/tmp/cleat-selfhost` /
   nREPL 端口与捕获 stderr 串台出假的 `InvalidCastException` / load 5 ~ 14 的超时）已逐一分类，
   本轮 144 / 147 / 148 各踩到过；纪律是 worktree 阶段只跑 targeted，全套只在合并后的 main 串行一次。

未修任何东西（本任务限定不改 `src/**`）。修复候选与后续动作全部列在 §7 的八条待办里。
