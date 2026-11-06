# cleat-173 前中端 pass：L0 → L3

- 里程碑：M20　依赖：cleat-172　状态：完成（2026-11-04；先于 172 做，输入用 Clojure 分析器的 p0）
- 设计依据：[30 §2 D2](../designs/30-csharp-compiler.md)；现行实现 `loops.clj`、`front.clj`、`closures.clj`、`pipeline.clj:front-end`

## 工作内容

逐个 pass 移植，每个 pass 一个类，输入输出层由校验器检查：

| pass | 层 | 现行 |
|---|---|---|
| `LowerLoops` | L0 → L0 | loops.clj:123 |
| `AnalyzeRefs`（分析：被赋值 / 被引用表） | L0 | front.clj:29 |
| `SimplifyLetrec`（含 const-in-run，开关 `letrec`） | L0 → L1 | front.clj:126 |
| `CheckForward` | L1 → L1 | front.clj:79 |
| `Cp0Lite` | L1 → L1 | front.clj:208 |
| `SimplifyLets`（+ Cp0Lite） | L1 → L1 | front.clj:257 |
| `ConvertAssignments` | L1 → L2 | front.clj:86 |
| `UncoverFree` | L2 → L3 | closures.clj:19 |

`typed-clauses` 与"本形式是否生成了循环"的标记（`pipeline.clj` 的 `front-ends`）一并移植，供 179 决定哪些形式做类型推导。

## 验收

- 单元测试：每个 pass 的典型输入 / 输出用 S 表达式写成用例（可从 `tests/nanopass/cleat/compiler` 的现有用例翻译）。
- 对 172 的 G1 语料，每层输出都过校验器；与 Clojure 编译器的 `dump "l3"` 比较（打印规范化后）——L3 在本阶段两边仍应同形，不同处记账。

## 实施记录（2026-11-04）

**顺序调整**：先于 172 做。差分的输入是 Clojure 分析器给出的 p0（L0），C# 前中端与 Clojure 前中端从同一个 p0 各跑一遍逐阶段比较，
不依赖 C# 分析器。

**产物**（`src/Cleat.Compiler/Passes/Front/`）
- `SymSets.cs`：两种小集合，`SymList` 对应 front.clj（加在前面），`SymVec` 对应 closures.clj（追加在末尾）；顺序决定 letrec-refs、前向变量与 L3 自由变量表的次序，所以照搬。
- `FrontAnalysis.cs`（front/analyze）、`LowerLoops.cs`，以及 `FrontPasses.cs` 里的 `AnalyzeRefs`、`SimplifyLetrec`、`CheckForward`、`Cp0Lite`、`SimplifyLets`、`ConvertAssignments`、`UncoverFree`，
  `FrontState`（分析表、label 个数、`CLEAT_NO_OPT=letrec`）与 `FrontEnd`（按 pipeline.clj 的顺序组装，`RunWithStages` 给出各阶段）。
- `src/Cleat.Compiler/DiffSupport.cs` 与 **`tools/cs-diff.clj`**：同一进程里逐个顶层形式比较 Clojure / C# 的 l0 sl cf c1 in l2 l3 七个阶段与 label 个数
  （`--eval` 边分析边求值；`路径@命名空间` 给没有 ns 形式的文件）。172 起扩展到分析器（G1）。
- `tests/Cleat.Compiler.Tests/FrontPassTests.cs`：17 个用例（每个 pass 的典型改写、前向变量、装箱、自由变量顺序、整条链带校验）。

**移植中确认的语义细节**
- `FnName` 必须保留记录上 assoc 的额外键（`^:once` fn 的 `:once`），改为保存整张映射。
- letrec-refs 以 L0 节点身份为键：分析与改写必须作用于同一棵 IR（C# 的结构共享保证这一点）。
- 新建节点的源位置取被替换的节点；与 nanopass 按 `*src-node*` 复制 metadata 的细节不逐一对齐（差分不比较 metadata），只影响序列点的精度。

**验收**
- 单元测试：`Cleat.Compiler.Tests` 26/26。
- 差分（不比较源位置）：镜像的 41 个文件 2220 个形式、编译器与 nanopass 源码 1226 个、nrepl 533 / neorepl 315 / tools.build 150 / tools.namespace 71，
  **七个阶段与 label 个数全部相同，0 差异**；8 个 `*_semantics.clj` 测试文件（`--eval`）同样 0 差异。
