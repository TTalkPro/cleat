# cleat-006 Cleat.Core 骨架、值表示与持久集合

- 里程碑：M1　依赖：cleat-005　状态：未开始
- 设计依据：[02 §1、§2、§10](../designs/02-runtime.md)

## 目标
建立 `Cleat.Core` 项目，提取集合、数值、`Symbol`、`Keyword`、`Util`、`Murmur3`。

## 工作内容
1. `src/Cleat.Core/clojure.lang/`：按 cleat-005 的清单复制并改写集合组、数值组、名字组中的 `Symbol`/`Keyword`；保留 EPL 头，加 `// Adapted from ClojureCLR <commit>` 注释。
2. `Util.TryPrimTypeToName` 移到 `Cleat.Core.Runtime.Munge`（含 `munge`/`demunge`）。
3. `BigInteger` 按 cleat-005 的结论处理。
4. 单元测试：集合（conj/assoc/dissoc/seq/transient 往返、哈希与 `=`）、数值塔（溢出、`+'`、ratio、`BigDecimal`）、`Symbol`/`Keyword` 的哈希按名字。

## 验收标准
- `Cleat.Core` 编译，无 DLR 引用；测试通过。
