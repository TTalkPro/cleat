# cleat-150 M16 收口：r7rs 全量重跑与三方对照

- 里程碑：M16　依赖：cleat-142 ~ 149　状态：未开始
- 设计依据：[28 §6 ~ §7](../designs/28-hot-path-optimizations.md)

## 问题

M16 的 P0 ~ P3 改动落地后，需要在同一口径下确认整体收益、无语义回退，
并把 M15 的 1.61x（192.168.3.111，OpenJDK 25 C2）作为对照基线更新。

## 工作内容

1. 全量验证：Release 构建 0 warning、语料门禁（gate 521 分类）、CTS 与 test-clojure 基线、
   Core / Host 全量、种子不动点（stage2 == stage3，BuildFromTheSeedReproducesTheSeed）。
2. r7rs 56 项 × 3 轮，cleat / OpenJDK 25 C2（必要时加 GraalVM 作上限）同窗口重跑，
   双态项按 cleat-149 的口径报告区间；compare.py --md 出表入 notes。
3. 每项改动用各自 `CLEAT_NO_OPT` 开关做一次贡献分解（开 / 关同窗口），
   确认没有互相抵消或隐藏的负作用。
4. 分配画像复跑：browse / peval / array1 / scheme 的 alloc.cs top 项与 M15 对照。
5. 更新 notes/r7rs-performance.md（新增 M16 节）、designs/28 状态改**收口**、
   TASK.md 里程碑表与关键路径同步。

## 验收

- 几何平均对 M15 的 1.61x 有可测改善；无任何项相对本轮起点回退超过噪声（或已记录取舍）。
- 三套基线的 fail / error 与既有分类一致；运行时特化无编译失败。
- 文档同步：notes / designs/28 / TASK.md 一致，收口结论写入任务书。
