# cleat-025 前中端 pass

- 里程碑：M4　依赖：cleat-024　状态：由 cleat-083 ~ 091 取代（[19](../designs/19-clojure-self-hosting.md)，2026-10-14）
- 设计依据：[04 §3](../designs/04-compiler.md)

## 目标
`recognize-loops`（L0 → L1）、`cp0-lite`（L1 → L1，第一版可只做 `if` 常量测试）、`convert-assignments`（L1 → L2）、`uncover-free`（L2 → L3，含已知调用标记与确定性类名）。

## 工作内容
1. 四个 `define-pass`；`analyze`（一遍求被赋值 / 被捕获 / 已知 Var）。
2. 每个 pass 后的 `unparse` 快照（`.l1` ~ `.l3`）。
3. 驱动骨架 `driver.clj`：pass 链、`CLEAT_DUMP_PASSES` 环境变量写出各阶段。

## 验收标准
- 快照测试通过；L3 中每个 `fn` 的自由变量表与手工核对一致（抽样 20 个）。
