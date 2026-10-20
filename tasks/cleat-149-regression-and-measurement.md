# cleat-149 parsing / mbrot / pi 回归排查与测量口径修正

- 里程碑：M16　依赖：无（宜早做，避免污染后续 A/B）　状态：未开始
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
