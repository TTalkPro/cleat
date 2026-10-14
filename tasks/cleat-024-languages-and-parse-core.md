# cleat-024 语言 L0 ~ L3 与 parse-core

- 里程碑：M4　依赖：cleat-023、cleat-011　状态：由 cleat-083 ~ 091 取代（[19](../designs/19-clojure-self-hosting.md)，2026-10-14）
- 设计依据：[04 §1 ~ §3](../designs/04-compiler.md)、[03 §3](../designs/03-language-core.md)

## 目标
编译器库结构、L0 ~ L3 定义、第一个 pass。

## 工作内容
1. `src/cleat/compiler/languages.clj`：L0（终结符：symbol、datum、name、type-name、var-info；非终结符 Expr、Fn、Clause、Catch、DeftypeMethod…），L1 ~ L3 以 `extends` 定义；应用产生式带关键字 `call`。
2. `primitives.clj`：读 `primitives.edn`。
3. `parse.clj`：Lcore → L0；原语调用识别为 `primcall`；常量表（去重规则）；源位置。
4. 快照测试：从 `core.clj` 展开后的真实 Lcore（cleat-018 的输出）取样本，`tests/compiler/snapshots/*.l0`。

## 验收标准
- 全部 boot 命名空间的 Lcore 都能通过 `parse-core`。
