# cleat-164 `CLEAT_NO_OPT` 精确匹配、accinline 去留与开关清单

- 里程碑：M19　依赖：无　状态：完成
- 设计依据：[29 §1 第 5 条 / §5.2](../designs/29-usability-and-engineering.md)、[28 §7.2](../designs/28-hot-path-optimizations.md) 的开关纪律

## 问题

1. **缺陷**：`opt-off?`（`codegen.clj`，`str/includes?`）和 analyzer.clj 的 `.Contains` 都用子串匹配，
   `CLEAT_NO_OPT=accinline` 会同时关掉 `inline`。现有 21 个记号中只有这一处冲突，但以后加记号还会撞上。
2. accinline（cleat-145）默认关、实测中性，却在 eval / 包两条路径上留着完整实现（含 `InterfaceFile.FindInline`）。
   它不是默认开优化的归因仪器，不属于 28 §7.2 "不物理删除"的保护范围。
3. 21 个 `CLEAT_*` 环境变量、21 个 `CLEAT_NO_OPT` 记号没有集中清单。

## 工作内容

1. `CLEAT_NO_OPT` 按逗号 / 空白分词后精确匹配，C# 侧有同类读取的一起改，共用一个助手函数；`=1` 的语义不变。
   加测试：`accinline` 不影响 `inline`，`inline` 不影响 `loopinl`。
2. 回查 notes / 任务书里用过含 `accinline` 的 `CLEAT_NO_OPT` 的归因数据，列出受影响的结论，必要时重算。
3. accinline 去留**由用户裁决**：删除（实现、测试、`CLEAT_OPT_ACCINLINE`，145 任务书记账）或保留（README 已知限制写明是实验开关）。
4. 开关清单：每个环境变量 / 记号的用途、默认值、是否影响语义、诊断还是优化、引入任务。放在 designs/README 附表。
   不改名，保证历史 notes 里的命令仍可复现。

## 验收

- 精确匹配测试通过；改的是 `src/clojure` 时重走种子不动点。
- r7rs 不需要重跑（默认路径的行为不变），但要用 `CLEAT_NO_OPT=inline` / `=accinline` 各跑一次冒烟，确认两者互不影响。
- 清单覆盖 `grep -rhoE "CLEAT_[A-Z0-9_]+" src` 的全部结果。

## 进展（2026-11-02）

- **精确匹配落地**：新增 `src/clojure/cleat/compiler/optflags.clj`（编译器唯一 `opt-off?`：
  `=1` 全关，否则按逗号 / 空白分词后 `set` 精确匹配）与 `src/Cleat.Core/Runtime/OptFlags.cs`
  （运行时唯一 `OptFlags.Off`，`Tokenize` 单独暴露给测试）。codegen 的子串 `opt-off?`、analyzer 的
  4 处 `.Contains`（crossnstype / accinline / known / loopinl）、front 的 `letrec` 全部改走 optflags；
  C# 侧 Keyword / RT（strseq / intern / nthfast / crossnstype）/ StringSeq 改走 `OptFlags.Off`；
  GcProfile 是宿主测试需在进程启动后切换的诊断路径，改走 `OptFlags.OffNow`（每次现读、同一分词规则）。
  `Cleat.Emit` 不引用 Core，`tiered` 暂保留原子串读取：改成分词后，eval 程序集的 golden 行号稳定
  变为外层顶层行（5 项失败、可 100% 复现）；还原后全部恢复。该例外记入开关附表，后续若要统一
  需先单独排查 Emit 的 eval PDB 语义。
  `=1` 的语义不变；不改名。
- **归因数据回查（结论：无需重算）**：`m16full.py DEFAULT_NOPT` 的五个记号（charbox,strseq,intern,
  nthfast,crossnstype）与全部 26 个记号逐一核对，无子串包含关系，历史三轮数据不受污染；
  `NOPT=accinline` 只出现在 cleat-145 的 opt-in 归因里，且双臂同设——inline 的误关在两侧相同，
  不影响"accinline 中性"的裁决。notes / 任务书里其余 `NO_OPT=accinline` 都是"仍可单独关"的描述性文字。
- **测试**：`Cleat.Core.Tests/OptFlagsTests`（分词精确性 2 例）+ `Cleat.Host.Tests/OptFlagTests`
  （真实宿主进程：未设 / accinline / accinline+boxcache / inline / inline,loopinl / "1" 共 4 例，
  断言 inline / accinline / loopinl 三个记号互不影响）。验收要求的 `CLEAT_NO_OPT=inline` / `=accinline`
  冒烟（`-e '(println (+ 1 2))'`、分词形式、`=1`）均通过。
- **种子**：改了 `src/clojure`，`clojure-seed` 重走不动点（312 s，stage2 == stage3，种子回射逐字节相同），
  `src/boot/seed/*.gz` 已更新。
- **accinline 去留**：按设计由用户裁决；本轮默认**保留**（不物理删除），并在 README"与 Clojure 的偏离"
  下写明它是默认关、实测中性的实验开关，附 designs/README 开关附表链接。要删的话另起一轮
  （实现 + 测试 + `CLEAT_OPT_ACCINLINE` + 145 任务书记账）。
- **开关清单**：designs/README 新增附表——22 个环境变量（用途 / 默认 / 类别 / 引入任务）与
  26 个 `CLEAT_NO_OPT` 记号（关掉什么 / 读取处 / 引入任务），覆盖 `grep -rhoE "CLEAT_[A-Z0-9_]+" src`
  的全部结果；不改名。
- **回归收口**：还原 Emitter 的临时分词实现与调试探针后，GcProfile 现读修复生效；Core 130 / 130，
  Host `Host|Packages` 262 / 262（22 m 29 s）通过。Emit 侧保持 cleat-114 原读取。
- **补跑符号合基线**：`conformance --run --gate` OK（4842 行，pass 4320 / fail 77 / error 444，
  全部为已分类偏离）；`cts cts` OK（247 ns，6868 / 29 / 6）；`cts test-clojure` OK
  （54 运行 ns，3877 / 25 / 4）。
