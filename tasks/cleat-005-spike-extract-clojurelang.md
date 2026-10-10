# cleat-005 提取试验：ClojureCLR 的 clojure.lang 不带 DLR 能否编译

- 里程碑：M0　依赖：cleat-001　状态：未开始
- 设计依据：[01 §2](../designs/01-reference-analysis.md)、[02 §10](../designs/02-runtime.md)

## 目标
在正式提取（cleat-006 ~ 009）之前，用一个一次性项目确认 02 §10 清单的文件在去掉 DLR 与编译器引用后能编译，并列出每个文件需要改的位置。

## 工作内容
1. `spikes/cleat-005/`：复制 02 §10 "集合、数值、名字、引用、函数、reader" 六组文件，去掉 `using Microsoft.Scripting*`、`IDynamicMetaObjectProvider`、对 `Compiler`/`HostExpr`/`CljCompiler` 的引用（先以 `NotImplementedException` 占位）。
2. 逐文件记录改动点（行号、原因）到本文件；统计占位数量与种类（符号决议、`demunge`、`TryPrimTypeToName`、`load`…）。
3. 核对 `Symbol.GetHashCode`、`Keyword` 驻留、`PersistentHashMap` 哈希与 JVM 一致（Murmur3，`hasheq`）。
4. 评估 `BigInteger.cs`（ClojureCLR 自带）能否替换为 `System.Numerics.BigInteger`。

## 验收标准
- spike 项目编译通过，`PersistentHashMap`/`PersistentVector`/`Numbers` 的几条冒烟测试运行正确。
- 改动点清单成为 cleat-006 ~ 009 的工作内容。
