# cleat-151 M17：本机噪声地板标定与 r7rs 全量三臂重跑

- 里程碑：M17　依赖：cleat-150（沿用它的 runner 与配对口径）　状态：完成（2026-10-22，四条工作内容全做，见文末结论）
（历史：2026-10-21：111 已恢复在线，
  M16 §6 的单开关归因已在 111 上收尾并否证了那两个疑点，见 [notes M16 §7](../notes/r7rs-performance.md) ——
  本任务的地板标定与全量三臂仍全部待做）
- 设计依据：[28 §6 ~ §7](../designs/28-hot-path-optimizations.md)、[notes 的 M16 §5 ~ §6](../notes/r7rs-performance.md)

## 问题

M16 的收口数字还没有：56 项全量的三臂对照（对 JVM 的倍数 vs M15 的 1.61x）没跑完，
而"本机能不能判 <10% 的差异"至今只有一句经验描述（cleat-149 §2：等过 load 之后同一构建
同一项的 5 轮极差仍达 17 ~ 43%），没有成表的地板。没有地板，M16 §6 那批"同向 2 ~ 4/5、
量级 < 5% → 不可判定"的格子就永远不可判定，M17 的每一项 A/B 也仍然只能靠"5 轮 + 只报方向"
这条口头规则。两件事一起解：先标定，再按标定结果跑全量。

## 工作内容

1. **地板标定**（本机，安静窗口）：选代表项 wc / browse / lattice / slatex / peval / mbrot /
   pi / parsing / array1 / takl（含一个已知双态项），单臂连跑 N >= 9 轮，给每项轮内极差的
   p50 / p90 / max，并据此给"检出 5% / 10% 差异各需要多少同轮配对"的经验表；
   工具在 `bench/r7rs/` 内新增（复用 `quiet.py` 的门槛与 `m16sum.py` 的配对统计，不自带第二套口径）。
2. **判定门槛落地**：把标定结果写成 `m16sum.py` 能读的缺省（每项的最小可声明量级 + 最少同向轮数），
   低于门槛的格子输出"不可判定"而不是数字，避免 notes 里再出现"x0.984（4/5）"这种被当作结论的读数。
3. **全量三臂**：56 项 × 5 轮 × cleat-on / cleat-off / jvm（`m16full.py`，关侧
   `NOPT=charbox,strseq,intern,nthfast,crossnstype` 回 M15 收口行为 —— `accinline` 不在名单里，
   它是 cleat-145 落地后按 111 A/B 定为**默认关**的 opt-in 开关（`CLEAT_OPT_ACCINLINE=1` 才开），
   两边都默认不开，进名单只是空转；要单独量它就 `CLEAT_OPT_ACCINLINE=1 … NOPT=accinline`，见 notes M16 §8；
   JVM 臂 `JAVA=/usr/lib/jvm/java-25-openjdk-amd64/bin/java`），出 `m16sum.py` 两张表：
   开关贡献分解与对 JVM 的倍数。192.168.3.111 在线时优先在 111 跑（轮内极差 < 3%），
   本机只作补充并注明 runtime 版本（本机 .NET 10.0.12 / SDK 10.0.402，与 111 的历史绝对值不同口径）。
4. takl 等双态项按 cleat-149 的口径报区间，不报 min-of-N。

## 验收

- notes/r7rs-performance.md 新增 M17 节：地板表 + 全量三臂表 + 对 M15 1.61x 的结论（改善可声明才写量级）。
- `designs/28` §6 的验证纪律块按标定结果改写（把"< 3% 记为噪声"换成分项门槛）。
- 跑分窗口内不做任何构建 / 测试；被 load 筛除的轮次留在 jsonl 里可追溯。

## 结论（2026-10-22）

1. **地板标定**（本机 Debian-VM01，10 项 × 9 轮）：轮内极差 max/min 从 pi 1.050 到 takl 1.456，
   分项相差一个数量级；检出 5% 差异需要的同轮配对数 pi/mbrot 5 对即可、wc 要 156 对、takl 要 175 对。
   工具 `bench/r7rs/floor.py`、门槛 `results/floor-Debian-VM01.json`，原始数据 `results/floor.jsonl`
   （rep8 时被外部 SIGTERM 杀过一次，缺格补齐后汇总重算，无影响）。结论与本机只作补充、判定优先 111。
2. **门槛落地**：`m16sum.py` 读 `R7_FLOOR`（缺省 `bench/r7rs/floor.json`，不存在则行为与旧版逐字
   一致——已用 m150-full-111 的输出 diff 验证为空）；门槛表存在时低于 `min_declared_ratio_delta`
   或同向轮数不足的格子输出"不可判定"。`designs/28` §6 的"< 3% 记为噪声"按此改写成按项门槛的引用。
3. **全量三臂**（111，`m151-full-111.jsonl` 840 格 0 loaded）：对 JVM **x1.596**、开关贡献
   **x0.983** —— 与 150 的 x1.594 / x0.981 复核一致，M16 结论可复现。回退项读数：array1 x1.022
   （5/5，与 intern 归因一致）、lattice x1.031、simplex x1.026（均在地板附近，151 的按项门槛下
   lattice / simplex 已落到"不可判定"），ntakl 不可判定。
4. **双态项口径**沿用 149：takl / ntakl / triangl / puzzle 报区间，不参与 min-of-N。

notes/r7rs-performance.md 新增 M17 §1（地板表）与 §2（三臂重跑）。M17 剩余：cleat-152（bisect）、
153（调用点反馈）、147 第 2/3 条。
