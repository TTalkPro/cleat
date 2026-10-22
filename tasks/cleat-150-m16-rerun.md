# cleat-150 M16 收口：r7rs 全量重跑与三方对照

- 里程碑：M16　依赖：cleat-142 ~ 149　状态：完成（2026-10-22 收口，见文末结论）
（历史：runner 就位；2026-10-20 下午 192.168.3.111 离线，
  三臂全量改在本机重跑**，口径见 [notes 的 M16 §5](../notes/r7rs-performance.md)。
  已在本机拿到第一批配对读数：五开关 on/off × 10 项 × 5 轮，几何平均 x0.938，
  wc / browse 方向与远端一致，slatex / peval 待单开关归因 —— 见 [notes M16 §6](../notes/r7rs-performance.md)。）
  **2026-10-21 更新**：111 已恢复在线，三臂全量回到远端跑。远端工作位 `~/workspace/cleat-145-git`
  已同步到 main 的最终提交（工作树干净、Release 构建产物与 m145-ab2 的 jsonl 都留着），非交互 shell 里要先
  `export PATH=/home/david/.local/share/mise/dotnet-root:$PATH`。跑分前的纪律：种子换代 → boot image id 变，
  `/tmp` 里上一代留下的 `cleat-nuget-*` / `cleat-tools-build-*` 缓存里的包程序集是按旧镜像编的，
  不清会在用例开头的 `BootInterface.Check` 抛 StaleModuleException（本轮踩过，见 notes M16 §8）。
收口跑已启动**：56 项 × 5 轮 × 三臂（cleat-on / cleat-off / jvm，同轮交替）在 `~/workspace/cleat-145-git`
  （= main 最终态）上后台跑，`R7_MAXLOAD=1.2 R7_LOAD_CAP=1200`、`JAVA=/usr/bin/java`
  （111 的运行时是 OpenJDK 25.0.4+7，与 M15 的 1.61x 基线同版本号；但 .NET 已升到 10.0.13，绝对值不与 §1 ~ §4 并列），
  结果追加 `results/m150-full-111.jsonl`，日志 `/tmp/m150-full.log`。按 10-20 那份 `m16ab-full` 的实测墙钟
  （同一台 111、同样 56 项 × 三臂，那份的墙钟只用来估时间不是估性能：第 2 / 3 轮各 1336 / 1330 s）
  估算 5 轮 ≈ 1.9 ~ 2.5 h（等安静窗口的时间另计）。
  跑分期间这台机器不再做别的构建 / 测试。
  汇总用 `R7_PAIR_MAXLOAD=1.5 python3 bench/r7rs/m16sum.py m150-full-111`，之后才是第 3 条的单开关贡献分解。
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

## 结论（2026-10-22）

**全部四条工作内容完成，M16 收口。**

1. **语义门禁**：本轮不落产品代码，沿用同一棵树（b407f65）在 145 收口时过掉的完整矩阵
   （种子不动点、gate 521、CTS 6868/29/6、test-clojure 基线、Core / Host 全套，唯一失败是 111 缺
   `Microsoft.Data.Sqlite` 的既有环境项）。
2. **三臂全量**：56 项 × 5 轮跑完（`results/m150-full-111.jsonl` 840 条，0 loaded，0 对被筛）。
   对 JVM x1.594（M15 1.61x → 可声明改善）；收益项 wc x0.558 / cat x0.830 / browse x0.901 /
   tail x0.921 / parsing x0.949 等；结果与归因表见 [notes M16 收口节](../notes/r7rs-performance.md)。
   首跑被会话断开杀掉 → `m16full.py` 补断点续跑（按 (label, rep, name) 跳过已完成格子）后补完。
3. **单开关贡献分解**：整体 on/off 几何平均 x0.981；回退项归因 —— array1 x1.029 主因 intern
   （单关 x1.022，0/5），lattice x1.025 主因 strseq x1.047 + crossnstype x1.024，simplex / matrix
   归因不到单一开关（地板内组合效应）；三项都**记账保留默认开**（intern 换来 wc -44% 等收益，
   净额显著为正）。
4. **分配画像复跑**：browse Char 退出 top、peval Object[] 4.0%→3.8% 且 17% 的闭包类退出 top8、
   array1 Int64 59% + Object[] 40%（与 intern 归因一致）、scheme Object[] 45.4%（147 第 3 条基建
   已就位，M17 决策）。

文档同步：notes M16 收口节、designs/28 §7、TASK.md 的 150 行与 M16 关键路径。M16 关闭，M17
（cleat-151 ~ 153 + 147 第 2/3 条）成为现行。
