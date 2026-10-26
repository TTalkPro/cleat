# cleat-163 sigspec 整体消融与三档裁决

- 里程碑：M19　依赖：无（建议排在 cleat-164 修完子串缺陷之后跑分）　状态：未开始
- 设计依据：[29 §1 第 6 条 / §5.1](../designs/29-usability-and-engineering.md)、[24](../designs/24-runtime-signature-specialization.md) ~ [27](../designs/27-jit-style-runtime-specialization.md)

## 问题

M14 / M15 的运行时签名特化（`RuntimeSigSpec` / `RuntimeSigTable` / `RuntimeSigKey` / `TypeFeedback`，约 1000 行 C#，
编译器 43 处引用）引起过 dynamic x1.144、pi x1.126 的回退（152），156 调参后才收住。它的**整体**净收益
没有单独做过同轮配对，只在 150 的开关贡献分解里出现过。

## 工作内容

1. 跑分前先确定门槛，写进本任务书（29 §5.1 提议 5%）。
2. 同一构建，56 项同轮配对，本机 5 轮（111 在线时复核）：默认 vs `CLEAT_NO_OPT=sigspec`、
   vs `sigspec,typefeedback`、vs `sigspec,typefeedback,typed`。用 `m16sum.py` 和 `R7_FLOOR` 判定。
3. 代码面统计：相关 C# / Clojure 行数、种子体积占比、`CLEAT_RTSPEC_STATS` 下各基准的运行时编译次数、
   后台编译线程的内存与启动影响（与 159 的画像共用）。
4. 三档裁决：(a) 可声明且 ≥ 门槛 → 保留，补 24 ~ 27 现状注记；(b) 可声明但 < 门槛 → 保留 cleat-140 的编译期调用路径推导，
   删运行时观察 / 重编；(c) 不可声明 → 整体删除。(b) / (c) 的删除另开任务执行，种子重走。

## 验收

- 全表（每项中位比值、同向轮数、门槛判定）和代码面统计写进 notes 与本任务书。
- 裁决明确；本任务只改 bench / 文档，不改产品代码。
