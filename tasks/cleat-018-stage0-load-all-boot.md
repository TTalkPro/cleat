# cleat-018 在 stage0 上加载全部 boot 命名空间；语料子集通过

- 里程碑：M2　依赖：cleat-016、cleat-017　状态：未开始
- 设计依据：[08 §3、§4](../designs/08-bootstrap.md)、[09 §2](../designs/09-testing.md)

## 目标
stage0 从源码按 03 §5 顺序加载全部 boot 命名空间，成为能跑 Clojure 程序的解释器。

## 工作内容
1. 逐文件加载并修复：展开器缺失的形式、解释器缺失的节点、运行时缺失的原语 / 类。
2. 记录耗时（每个文件）；若 Phase A 预计超过 5 分钟，提前启动 cleat-036（Lcore 种子）。
3. 语料 runner（cleat-002）接入 stage0：`:common` 子集的通过率；偏离按 09 §1 分类。
4. `clojure.test` 可用：跑 `test_clojure` 中不依赖 AOT 与外部库的几个命名空间。

## 验收标准
- 全部 boot 命名空间加载成功；语料 `:common` 通过率记入本文件（目标 ≥ 90%，未通过的分类）。
