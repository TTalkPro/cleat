# cleat-098 偏离表清理

- 里程碑：M9　依赖：cleat-095　状态：未开始

## 背景
`tests/conformance/cleat-divergences.edn` 有 529 条：309 条 `:host/jvm-api`、98 条 `:corpus/jolt-value` 等是宿主差异或语料本身的问题，不动；
`:error-model` 6、`:numeric` 2、`:host/boxing` 2、`:strictness` 2、`:printf` 2、`:not-yet` 1（`seque`）、`:upstream/clojureclr` 7 需要逐条判断。

## 工作内容
1. 逐条过上述小类：能修的修（编译器或运行时），不该修的改写 `:reason` 说明为什么是永久偏离。
2. nREPL 的 middleware 测试改回上游的 `^{…}` 写法（cleat-065 之后可行）。

## 验收标准
- `conformance --run --gate` 通过；偏离表里没有 `:not-yet`（或每条 `:not-yet` 指向一个任务）。
