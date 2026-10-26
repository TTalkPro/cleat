# cleat-164 `CLEAT_NO_OPT` 精确匹配、accinline 去留与开关清单

- 里程碑：M19　依赖：无　状态：未开始
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
