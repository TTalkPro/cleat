# cleat-023 cleat.nanopass 移植

- 里程碑：M4　依赖：cleat-018　状态：由 cleat-083 ~ 091 取代（[19](../designs/19-clojure-self-hosting.md)，2026-10-14）
- 设计依据：[07](../designs/07-nanopass-port.md)

## 目标
在 stage0 上可展开、可运行的 `cleat.nanopass`。

## 工作内容
1. `src/cleat/nanopass/`：`registry.clj`（语言注册表）、`language.clj`（`define-language`、`extends`、`language->s-expression`、`unparse-L`）、`records.clj`（每产生式 `deftype` + 带检查构造器、标签分配）、`meta.clj`（模式 / 模板解析为 `nano-*` 树，按描述数据驱动）、`pass.clj`（`define-pass`、`nanopass-case`、`with-output-language`、`in-context`、处理器 / 子句生成、自动补全、输出检查、`trace-`/`echo-define-pass`）、`walk.clj`（自带的 `macroexpand-all`，只认核心形式）。
2. 表面语法按 07 D2 ~ D4；多返回值用向量。
3. 把上游 `tests/compiler.ss` 与 `tests.ss` 翻译为 `tests/nanopass/`；在 stage0 上运行，记录展开与运行耗时。
4. 展开确定性测试：两次展开形式相同。

## 验收标准
- 翻译的上游测试全部通过；耗时记入本文件与 08 §4。
