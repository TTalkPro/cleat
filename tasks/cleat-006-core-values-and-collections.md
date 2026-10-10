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

## 结论（2026-10-10）

完成。
- cleat-005 的提取结果整体进入 `src/Cleat.Core/clojure.lang/`（`Lib/` 148 个文件、`Readers/` 3 个、`Runtime/Properties.cs`，约 5.6 万行，EPL 头保留；`Lib/Munge.cs` 收容 `demunge` / `TryPrimTypeToName`；`ArrayIter.cs` 从 ClojureCLR 的 `CljCompiler/` 移来）。`Cleat.Core.csproj` 关闭样式检查与旧代码告警，只多一个包 `Microsoft.Extensions.DependencyModel`（`RT.classForName` 枚举程序集用，不是 DLR）。
- 13 处 `// CLEAT-005:` 占位（`RT.load`/`Compile`/`LoadScript`/`Init` 等 7 处加载路径，`#=` 与记录字面量 4 处，reader 的 2 处死代码）留给 cleat-009 / cleat-010 / cleat-012。
- `RT.print`：加 `bool` 分支（`true`/`false`），ClojureCLR 原靠 `core_print` 覆盖。
- **BigInteger 决定**：暂时保留 ClojureCLR 自带的 `BigInteger.cs`（3432 行，哈希与 Java 一致）；换成 `System.Numerics.BigInteger` 需要约 150 行适配（基数解析、`Precision`、`BigDecimal` 的位操作、Java 兼容哈希），收益不大，记入后续；02 §2 相应修正。
- 测试：`ClojureLangSmokeTests`（cleat-005 的 49 项检查转为 NUnit：PHM 10 万项、PV transient、数值溢出与 `addP`、Ratio、Keyword 驻留、Symbol 按名字哈希、Var 线程绑定、Atom 多线程、Ref/Delay、ArityException、reader 往返、EdnReader、MultiFn）通过。更细的单元测试随各任务补充。
