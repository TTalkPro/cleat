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
