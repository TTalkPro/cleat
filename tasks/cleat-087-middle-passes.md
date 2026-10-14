# cleat-087 前中端 pass

- 里程碑：M8（阶段 A）　依赖：cleat-086　状态：未开始
- 设计依据：[19 D4、D5](../designs/19-clojure-self-hosting.md)

## 目标
`loops.sls`、`front.sls`、`closures.sls`（以及普查认定需要的 `inline.sls` / `primdata.sls` 部分）用 cleat.nanopass 重写。

## 已有的部分
cleat-084 的试点已移植：`src/clojure/cleat/compiler/loops.clj`（`lower-loops` 全部）、`closures.clj`（`uncover-free` / `attach`）、`front.clj` 的 `analyze`、`check-forward`、`convert-assignments`，
在快照上与 Scheme 版相同（`cleat.compiler.pilot-test`）。本任务补上其余 pass，并接入 cleat-085 的差分工具在全部输入集上验收。

## 工作内容
1. `lower-loops`、`analyze`（身份表）、`simplify-letrec`、`check-forward`、`cp0-lite`（只保留 Clojure 输入会触发的折叠）、`simplify-if`、`convert-assignments`、`uncover-free` / `attach`。
2. 普查认定不需要的 pass 不移植，在本任务结论里列出依据。
3. `CLEAT_SCHEME_DUMP_PASSES` 对应的导出与差分接入。

## 验收标准
- G1@la / l1 / l2 / l3：输入集零差异。
- Scheme 快照里属于 Clojure 子集的用例（closure、named-let-loop、try-catch 等）在 Clojure 编译器上也有快照。
