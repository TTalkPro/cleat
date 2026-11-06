# cleat-174 基础下降与 ILGenerator 发射：eval 路径跑通

- 里程碑：M20　依赖：cleat-173　状态：完成（2026-11-04）
- 设计依据：[30 §2 D2 / D4、§3.3 / §3.4](../designs/30-csharp-compiler.md)；现行 `codegen.clj` 的 `cg*` / fn 类 / `generate-unit`，`src/Cleat.Emit/Emitter.cs`

## 目标

不做任何优化，把 L3 正确地变成可运行的 CIL：所有值都是 `Obj` 表示，所有宿主调用暂走反射（静态决议在 175）。

## 工作内容

1. L4 / L5 节点定义补全（30 §3.3 / §3.4）。
2. `ChooseRepresentation` 的"全 Obj"档：L3 → L4，不插任何拆箱。
3. `ConvertClosures`：L4 → L5。fn 类（`AFunction` / `RestFn`、自由变量字段、`.ctor`、`invoke`、无自由变量时 `invokeStatic` 与 `Instance` 单例、
   `getRequiredArity`），`label` / `jump`，`clj-def` / `clj-var-ref` / `clj-var-set!`，`clj-invoke` / `clj-keyword-invoke`，`clj-static-call` 的守卫，
   顶层 `Init<k>` 与 `Initialize`、`$Vars`，常量表。
4. `LiftTry`、`ExplicitLocals`（含局部清除 `Locals.Take` 的位置，规则同现在 codegen:1946-2093）。
5. `EmitIL`：两阶段（类型 / 字段 / 方法签名 → 方法体 → 基类先 `CreateType`）；从 `Cleat.Emit` 搬入类型解析、成员解析、常量 fasl、
   内存模式分块（1000 个类型）、`EvalLoadContext` / `RunAndCollect`、`tiered` 属性。
6. eval 驱动：逐形式"分析 → 编译 → 执行"，简单形式解释（`seval` / `simple?` 的 C# 版）、`HoldResult`、`load-source` 钩子。

## 验收

- 在 Clojure 编译器编出的镜像上，`CLEAT_COMPILER=cs` 跑 `tests/Cleat.Host.Tests/data/*_semantics.clj` 中不涉及 deftype / case / try / async 的部分，输出与 `.out` 相同。
- REPL 能定义并调用函数、宏、闭包、loop / recur。
- 每个 pass 后 `CLEAT_IR_CHECK=1` 不报错。

## 实施记录（2026-11-04）

**产物**（`src/Cleat.Compiler/`）
- `Lower/L5.cs`：L5 模型——`UnitDecl` / `ClassDecl` / `FieldDecl` / `MethodDecl`，方法体是已决议的指令（`Il`、`LabelIns`、`LdConst`、`Line`、结构化 `TryIns`）。
  类型引用是 `System.Type` / 本单元的 `ClassDecl` / 类型名字符串（分析器给的 deftype 名、静态决议的签名，发射时解析）；方法引用是 `MethodBase` / `MethodDecl` / `MethodRef`。
- `Lower/Rt.cs`：生成代码调用的运行时成员，编译期就是 `MethodInfo` / `FieldInfo`（拼写错误在类型初始化时暴露）。
- `Lower/CodeGen.cs`：cg* 的基础节点、局部变量清除（`WalkVarRefs` / 分支计数 / 循环钉住 / `^:once` 字段清除）、lift-try、常量表与 `$Vars`、序列点。
- `Lower/FnClasses.cs`：fn* → AFunction / RestFn 子类（静态 invokeStatic、转发 invoke、原始签名的三种桥、doInvoke、getRequiredArity、HasArity），顶层 `Init<k>` 与 `Initialize`。
- `Emit/IlEmitter.cs`：两阶段 ILGenerator 发射；eval 缺省发射成 PE 字节装进不可回收的 `EvalLoadContext`（同 Cleat.Emit 的 cleat-114 行为，带内存 PDB），`CLEAT_NO_OPT=tiered` 时 RunAndCollect + 每 1000 个类型分块。
- `Driver/CsDriver.cs`：eval / load-source 钩子；**172 之前宏展开与分析借用 Clojure 分析器**（`ClojureFrontEnd`：analyze-top → unparse → `IrReader`），从 L0 起全部是 C#。

**与设计的偏差**
- **L4 不单独建节点**：表示层做成"L3 + 按变量的表示表"（`Loc` 的 Raw 与 179 的推导表），codegen 读表；cleat-174 只有原始签名的形参 / 返回值是原始类型。designs/30 §3.3 待改。
- **L5 方法体是线性指令序列**（带结构化 try），不是语句树：codegen 的 lift-try、按缓冲区生成操作数都依赖指令序列，原样移植最可靠。designs/30 §3.4 待改。
- 分层编译（`tiered` 方法、jump 的 keep 集、if 汇合局部）未做，留给 180；eval 的函数类方法因此全部按优化编译（不会抓住惰性序列的头）。

**验收**：`CLEAT_COMPILER=cs` 下 REPL / `-e` 可定义并调用函数、宏、闭包、loop / recur、变长参数；`tests/Cleat.Host.Tests/data/*_semantics.clj`
里不涉及 deftype 的 13 个文件输出与 Clojure 编译器逐字相同（其余 4 个数值文件的差异是缺静态决议，175 补上后相同）。
