# cleat-150 M16 收口：r7rs 全量重跑与三方对照

- 里程碑：M16　依赖：cleat-142 ~ 149　状态：进行中（runner 就位；**2026-10-20 下午 192.168.3.111 离线，
  三臂全量改在本机重跑**，口径见 [notes 的 M16 §5](../notes/r7rs-performance.md)。
  已在本机拿到第一批配对读数：五开关 on/off × 10 项 × 5 轮，几何平均 x0.938，
  wc / browse 方向与远端一致，slatex / peval 待单开关归因 —— 见 [notes M16 §6](../notes/r7rs-performance.md)。）
  **2026-10-21 更新**：111 已恢复在线，三臂全量回到远端跑。远端工作位 `~/workspace/cleat-145-git`
  已同步到 main 的最终提交（工作树干净、Release 构建产物与 m145-ab2 的 jsonl 都留着），非交互 shell 里要先
  `export PATH=/home/david/.local/share/mise/dotnet-root:$PATH`。跑分前的纪律：种子换代 → boot image id 变，
  `/tmp` 里上一代留下的 `cleat-nuget-*` / `cleat-tools-build-*` 缓存里的包程序集是按旧镜像编的，
  不清会在用例开头的 `BootInterface.Check` 抛 StaleModuleException（本轮踩过，见 notes M16 §8）。
- 设计依据：[28 §6 ~ §7](../designs/28-hot-path-optimizations.md)

## 问题

M16 的 P0 ~ P3 改动落地后，需要在同一口径下确认整体收益、无语义回退，
并把 M15 的 1.61x（192.168.3.111，OpenJDK 25 C2）作为对照基线更新。

## 工作内容

1. 全量验证：Release 构建 0 warning、语料门禁（gate 521 分类）、CTS 与 test-clojure 基线、
   Core / Host 全量、种子不动点（stage2 == stage3，BuildFromTheSeedReproducesTheSeed）。
2. r7rs 56 项 × 3 轮，cleat / OpenJDK 25 C2（必要时加 GraalVM 作上限）同窗口重跑，
   双态项按 cleat-149 的口径报告区间；compare.py --md 出表入 notes。
   本机改法（111 离线后的口径，m16full.py 头注与 notes M16 §5 为准）：
   轮数从 3 提到 **5**（配对口径要够轮）；每格跑前先等安静窗口（`bench/r7rs/quiet.py`，
   load1 ≤ 核数 x 25%，等不到标 `loaded` 并由 `m16sum.py` 缺省剔除）；
   **判定只认同轮配对比值**（同一基准 × 同一轮的 on/off、on/jvm 秒数比中位 + 同向轮数），
   单臂最小值差 <10% 只报方向；JVM 臂显式 `JAVA=/usr/lib/jvm/java-25-openjdk-amd64/bin/java`
   （本机 mise 缺省 java 是 GraalVM CE 25，与 111 的 OpenJDK 25.0.4 C2 基线不同口径；
   2026-10-20 本机 `java-25-openjdk-amd64` 是 25.0.4+7-deb13，与远端同版本号）。
3. 每项改动用各自 `CLEAT_NO_OPT` 开关做一次贡献分解（开 / 关同窗口），
   确认没有互相抵消或隐藏的负作用。
4. 分配画像复跑：browse / peval / array1 / scheme 的 alloc.cs top 项与 M15 对照。
5. 更新 notes/r7rs-performance.md（新增 M16 节）、designs/28 状态改**收口**、
   TASK.md 里程碑表与关键路径同步。

## 验收

- 几何平均对 M15 的 1.61x 有可测改善；无任何项相对本轮起点回退超过噪声（或已记录取舍）。
- 三套基线的 fail / error 与既有分类一致；运行时特化无编译失败。
- 文档同步：notes / designs/28 / TASK.md 一致，收口结论写入任务书。
