# cleat-087 前中端 pass

- 里程碑：M8（阶段 A）　依赖：cleat-086　状态：完成
- 设计依据：[19 D4、D5](../designs/19-clojure-self-hosting.md)

## 目标
`loops.sls`、`front.sls`、`closures.sls`（以及普查认定需要的 `inline.sls` / `primdata.sls` 部分）用 cleat.nanopass 重写。

## 已有的部分
cleat-084 的试点已移植：`src/clojure/cleat/compiler/loops.clj`（`lower-loops` 全部）、`closures.clj`（`uncover-free` / `attach`）、`front.clj` 的 `analyze`、`check-forward`、`convert-assignments`，
在快照上与 Scheme 版相同（`cleat.compiler.pilot-test`）。本任务补上其余 pass，并接入 cleat-085 的差分工具在全部输入集上验收。

## 普查给出的范围（cleat-085）
要实现：lower-loops、cp0-lite（不含查表折叠与 discard）、simplify-lets、uncover-free / attach、convert-assignments（L1 → L2 的语言变换）、simplify-letrec 的形状变换；
不移植：check-forward、simplify-if、primdata 折叠（Clojure 输入下不改变任何形式或 Lcil）；cp0-inline 是否需要用 G1 确认。

## 工作内容
1. `lower-loops`、`analyze`（身份表）、`simplify-letrec`、`check-forward`、`cp0-lite`（只保留 Clojure 输入会触发的折叠）、`simplify-if`、`convert-assignments`、`uncover-free` / `attach`。
2. 普查认定不需要的 pass 不移植，在本任务结论里列出依据。
3. `CLEAT_SCHEME_DUMP_PASSES` 对应的导出与差分接入。

## 验收标准
- G1@la / l1 / l2 / l3：输入集零差异。
- Scheme 快照里属于 Clojure 子集的用例（closure、named-let-loop、try-catch 等）在 Clojure 编译器上也有快照。

## 结论（2026-10-14）

完成。

**移植**（`src/clojure/cleat/compiler/front.clj`，在 cleat-084 试点的 `analyze`、`check-forward`、`convert-assignments` 之上）：
- `simplify-letrec`（L0 → L1，含 recognize-let、前向变量、常量并组、`LambdaOf` 处理器）逐条照 Scheme 版。
- `cp0-lite`：只有 Clojure 输入用得到的两条——常量测试的 `if` 折叠、`begin` 里删掉常量语句；查表折叠、discard、memv / eqv? / list 的改写只作用于 `primcall` 与 boot 的 `gref`，不移植。
- `count-refs!` 与 `simplify-lets`；`simple-datum?` 照 Scheme 版（Int32 fixnum、flonum、布尔、字符、Scheme 符号、空表——Clojure 的 long 常量不传播）。
- 不移植：`cp0-inline`（候选只有非 fn* 的 lambda）、`simplify-if`、primdata 折叠——普查（cleat-085）与下面的 G1 都表明它们不改变 Clojure 代码。

**流水线**：`cleat.compiler.pipeline/front-end`：parse-core 的输出（前端的 L0）→ lower-loops → analyze → simplify-letrec → check-forward → cp0-lite →
simplify-lets + cp0-lite → convert-assignments → uncover-free，返回 `[L3 分析表]`；各阶段的转储名同 Scheme 版（`p0 l0 sl cf c1 in l1 l2 l3`，`l1` 与 `in` 相同，因为没有 simplify-if）。
影子模式对每个形式走完整个前中端（lower-loops 的临时名每个 eval 形式从头编号）。

**验收（G1@la / l1 / l2 / l3）**：`CLEAT_NO_INLINE=1 tools/build.cs ir-dump <目录> --shadow --corpus`，九个阶段各 13330 条记录全部与 Scheme 编译器相同（规范化后）。
Host 测试 `CompilerDiffTests.ShadowCompilerMatchesSchemeThroughL3`（原 `ShadowFrontEndMatchesSchemeAtL0`，改为比较全部阶段）；cleat-084 的试点测试仍按 Scheme 快照逐 pass 对照。
