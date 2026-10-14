# cleat-095 cleat.nanopass 第二层：给用户的特性与上游测试

- 里程碑：M8（不阻塞阶段 B / C）　依赖：cleat-084　状态：未开始
- 设计依据：[19 D2](../designs/19-clojure-self-hosting.md)、[07 D2 ~ D4](../designs/07-nanopass-port.md)；取代 [cleat-056](cleat-056-nanopass-clojure-surface.md)

## 背景
cleat-084 原本包含两层；第一层（编译器用到的特性）在 cleat-084 完成，第二层不在关键路径上，拆到这里（2026-10-14）。

## 工作内容
1. 显式 catamorphism `~[f :> x -> y z]`（指定处理器、额外实参、多个返回值：处理器的返回值向量 `[v …]` 与解构）。
2. 多层省略号：模式与模板里的单项（不只是整段接入）在第二层以下也能用（cleat-084 只支持整段接入）。
3. `define-parser`（按语言生成 S 表达式 → 记录的函数；现在有按描述解释的 `cleat.nanopass/parse`）、`trace-define-pass` / `echo-define-pass`。
4. 不以关键字开头的产生式（上游允许，cleat-083 拒绝）、同一层多个 `...`（若上游测试需要）。
5. 上游 `tests/compiler.ss` / `tests.ss` 翻译为 `tests/nanopass/`，全部通过。
6. 用户文档：与 cleat-094 的 `cleat.nanopass` 文档合并。

## 验收标准
- 上游测试全部通过；宏展开结果确定。
