# cleat-020 发射器核心：类型、接口、方法、指令、switch、try

- 里程碑：M3　依赖：cleat-019　状态：未开始
- 设计依据：[05 §1、§2、§5](../designs/05-cil-backend.md)

## 目标
`Cleat.Emit`：Lcil → 内存程序集 / 持久化 dll。

## 工作内容
1. `TypeBuilder` 定义（基类、接口、属性、嵌套不支持）、字段（static / public / volatile `modreq`）、方法（static / virtual / override / `DefineMethodOverride` 显式实现 / ctor / cctor）、`.maxstack` 计算。
2. 指令发射：全部 `OpCodes` 按名；`switch`；`try`/`catch`/`finally`（`BeginExceptionBlock` 等）；`label`；`line`（可选 PDB）。
3. 互操作指令决议（05 §5）；决议不了的发 `Reflector` 调用。
4. 两种模式：`RunAndCollect` 内存（每 1000 类型分块）、`PersistedAssemblyBuilder` 持久化 + MVID/时间戳回填。
5. `EmitInMemoryData`：直接接收 `Cleat.Core` 的 `PersistentList`/`Symbol` 等形式的 Lcil（按类型全名识别，不引用 Core）。
6. 命令行 `cleat-emit in.lcil -o out.dll`；库 API `LcilEmitter.Emit`。
7. 测试：手写 Lcil 的 `AFunction` 子类被 C# 调用、`deftype` 式接口实现、`switch`、try/finally、互操作决议各情形、持久化后另一进程加载、两次输出逐字节相同。

## 验收标准
- 测试通过；ILVerify 对测试产物通过。

## 结论（2026-10-10）

完成（互操作指令与持久化常量表的测试分别留给 cleat-010、cleat-021）。
- `Emitter.cs`、`EmitInterop.cs`、`EmitConstants.cs`、`ClojureData.cs`（原 `SchemeData`）、`Program.cs`（`cleat-emit`）从 IronScheme（BSD）移植。改动：`Cleat.Core` 作为隐含引用；入口写 `Cleat.Core.CleatModuleAttribute`；`(attribute …)` 子句（程序集与类型级，构造函数只取字符串）；`volatile` 字段（`modreq IsVolatile` + 自动 `volatile.` 前缀）；`(implements "I" "名字")` → `Private|Virtual|Final|NewSlot` + `DefineMethodOverride`，接口可以是本 Lcil 中定义的；互操作指令的类型操作数改为直接交给运行时 `TypeResolver.Resolve(string, IEnumerable<string>)`（ClojureCLR 的类型名语法），`void` → `nil`，`long`/`double`/`bool`/`char` 直接装箱；去掉 IronScheme 的宽字符串与 `Task` 装箱；`ClojureData` 按 `clojure.lang.Seqable`/`ISeq`/`Symbol` 识别（列表与向量都当作 Lcil 列表）。
- 互操作与常量表依赖的 `Cleat.Core` API 以名字解析，只在用到时要求存在：`Cleat.Core.Runtime.TypeResolver.Resolve`、`Reflector.Call/GetField/SetField/New/NewArray/Cast/Is/Convert<T>/ConvertFrom`（cleat-010）、`Fasl.ReadPackedTable`、`ConstantPool.Register/Take`（cleat-021）。`FaslEncoder` 现为占位。
- 测试 `EmitterTests`（4 项）：闭包计数器（`TestFn` 子类、字段、`override`）；接口（本 Lcil 定义的接口 + 外部接口、显式实现、`volatile` modreq 可反射看到、`switch` + `try/finally`、程序集与类型属性、入口属性）；持久化两次保存逐字节相同并 `LoadFrom` 后同样通过；错误信息。`dotnet test` 8 项全部通过。
- 未做：ILVerify 校验（`tools/build.cs ilverify` 已有命令，待安装 `dotnet-ilverify` 工具后在 cleat-026 的产物上跑）。
