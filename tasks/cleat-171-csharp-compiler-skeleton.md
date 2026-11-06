# cleat-171 Cleat.Compiler 骨架、IR 节点与 Source Generator

- 里程碑：M20　依赖：无　状态：完成（2026-11-04）
- 设计依据：[30 §2 D1 / D3、§3](../designs/30-csharp-compiler.md)

## 目标

把 C# 编译器的地基搭好：项目、IR 节点、重写 / 遍历基类、校验器、S 表达式打印与读入、编译器切换开关。本任务不编译任何 Clojure 代码。

## 工作内容

1. 新建 `src/Cleat.Compiler/Cleat.Compiler.csproj`（引用 `Cleat.Core`），加入 `Cleat.slnx`；`Cleat.Host` 引用它。
   新建 `src/Cleat.Compiler.Generators`（Source Generator 项目）与 `tests/Cleat.Compiler.Tests`。
2. IR 节点：`abstract record Node`；L0 ~ L3 的节点按 30 §3.1 / §3.2 定义（`clj-call` 拆成具名节点 + `RuntimeCall(MethodInfo)`），
   每个节点带 `[Lang(...)]` 与源位置。L4 / L5 节点先只放占位定义，由 174 补全。
3. Source Generator 从节点定义生成：`Rewriter`（默认递归、结构共享）、`Walker`（只读）、`IrPrinter`（S 表达式）、节点相等比较（测试用）。
4. `IrValidator.Check(node, Lang)`：节点集合检查 + 各层附加不变量的挂钩点；`CLEAT_IR_CHECK=1` 时每个 pass 后运行。
5. L0 的 S 表达式**打印与读入**：形状与 Clojure 编译器的 `unparse` 一致（`RuntimeCall` 打印为 `(clj-call "名字" …)`），供 G1 与 `.inl`（30 D6）。
6. Pass 管线：`Pipeline` 按序运行 pass，`CLEAT_DUMP_IR=<pass,…>` 写出指定 pass 之后的 IR。
7. 编译器切换：`Startup.EnsureCompiler` 读 `CLEAT_COMPILER`；`cs` 时加载 `Cleat.Compiler` 调 `CompilerHooks.Install()`（本任务里钩子只抛"未实现"），
   缺省仍走 `cleat.compiler.driver`。开关加入 designs/README 的环境变量清单。

## 验收

- `Cleat.Compiler.Tests`：每种节点打印 → 读入往返相等；从 `src/boot/cleat.boot.inl` 读出全部 L0 子句再打印，与原文逐字相同；
  校验器能拒绝"L1 里出现 letrec 绑非 lambda"和"L2 之前出现 box"。
- 加一个空 pass 跑通管线与 `CLEAT_DUMP_IR`。
- 缺省配置下 Core / Host 全量不受影响。

## 实施记录（2026-11-04）

**产物**
- `src/Cleat.Compiler`（程序集 **`Cleat.Compiler.CSharp`**）：`Ir/Node.cs`（`Langs`、`SourcePos`、`Node` / `Expr`、`Rewriter` / `Walker` 基类）、
  `Ir/Nodes.cs`（L0 ~ L3 共 29 种节点）、`Ir/FnName.cs`、`Ir/IrPrinter.cs`、`Ir/IrReader.cs`、`Ir/IrValidator.cs`、`Passes/Pipeline.cs`（`IPass`、`Pipeline`、`PipelineOptions`）、
  `CompilerHooks.cs`。
- `src/Cleat.Compiler.Generators`：`NodeGenerator`（增量生成器），按 `[Node(Langs)]` 类的公开只读字段生成构造器、`Kind` / `Langs`、`Update`（不变返回自己、变了复制源位置）、
  `Rewriter` / `Walker` 的缺省访问方法与 `NodeKind` 枚举。
- `tests/Cleat.Compiler.Tests`（9 个用例）；`Cleat.slnx` 加入三个项目；`Cleat.Host` 引用 `Cleat.Compiler`。
- `Startup.UseCsCompiler` / `InstallCsCompiler`：`CLEAT_COMPILER=cs` 时 `EnsureCompiler` 按名字加载 `Cleat.Compiler.CSharp` 调 `CompilerHooks.Install()`；
  `clj` / 未设走原路径；其他值报错。designs/README 环境变量清单 18 → 22（`CLEAT_COMPILER`、`CLEAT_IR_CHECK`、`CLEAT_DUMP_IR`、`CLEAT_DUMP_IR_FILE`）。

**与任务书的偏差**
1. **程序集名**改为 `Cleat.Compiler.CSharp`：叫 `Cleat.Compiler` 时，require `cleat.compiler.driver` 按名字解析编译器包 `cleat.compiler.dll`
   （.NET 程序集名不区分大小写）解析到了新程序集，`--compile-boot` 报"没有 CleatModule 属性"。cleat-182 删除编译器包后可以改回。已写入 30 D1。
2. **`clj-call` 暂不拆**：本任务保留通用 `CljCall(Op, Datum, Args)`，具名节点随 cleat-172 分析器的语义一起拆（拆开后仍打印成 `clj-call` 形状）。
3. **打印 / 读入手写**，不由生成器生成：各节点 S 表达式形状不规则（`letrec` 的绑定对、`try-catch` 的处理器对、L3 lambda 的自由变量表），手写约 300 行比模式描述更直接。30 D3 已改。
4. `CljDef` / `CljCall` / `CljStaticCall` / `CljCase` / `CljDeftype` 的数据字段先按产生式位置命名（`Datum`、`Flag0`、`Expr0`……），172 按语义细化。
5. `FnName`（CljFnName）打印为映射 `{:class :variadic :prims :async?}`；读入时映射与 Clojure 的记录都认。现有 .inl 里没有 FnName（内联子句只含内层 lambda）。

**验收**
- `Cleat.Compiler.Tests` 9/9：每种节点（含 L2 的 box 三种、L3 lambda）文本 → 节点 → 文本往返相同且覆盖全部 `NodeKind`；
  **`src/boot/cleat.boot.inl` 的 859 个 L0 子句读入再打印与原数据结构逐元素相同，3354 个节点的源位置 metadata 相同，文本也相同**
  （测试只载入 `cleat.boot.dll` 让 quote 里的 deftype 类型可解析，不执行镜像）；校验器拒绝 L1 letrec 绑非 lambda、L2 之前的 box 节点、
  L3 缺自由变量表 / L3 之前带自由变量表、null 子节点；重写基类结构共享且重建节点保留源位置；管线校验 / 转储 / 层衔接检查。
- 开关：缺省与 `CLEAT_COMPILER=clj` 下 `-e "(+ 1 2)"` 为 3；`CLEAT_COMPILER=cs` 下平凡形式 `-e 42` 不加载编译器照常求值，`(+ 1 2)` 报
  "the C# compiler (CLEAT_COMPILER=cs) does not implement eval yet (cleat-174 …)"；`CLEAT_COMPILER=xyz` 报取值错误。
- 缺省配置回归：Release 构建（含种子 stage1 → 镜像 → 编译器包）通过、0 warning；Core 125/125；Host 291/291（32 m 30 s）。种子未动。
