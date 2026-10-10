# cleat-003 验证 .NET 10 的关键能力（Clojure 相关补充）

- 里程碑：M0　依赖：cleat-001　状态：未开始
- 设计依据：[05 §2](../designs/05-cil-backend.md)、[08 §4](../designs/08-bootstrap.md)

## 目标
IronScheme iron-003 已验证 `tail.`、`PersistedAssemblyBuilder`、内存程序集、确定性回填、Portable PDB（结论直接采用）。本任务只验证 Clojure 额外需要的项。

## 工作内容
`spikes/cleat-003/`，`dotnet run -c Release -- <项>`：
1. **接口实现类型**：内存程序集（`RunAndCollect`）里生成实现 `clojure.lang.ISeq`/`IFn` 风格接口（先用 spike 自带的接口）的类，公开可变字段、`volatile` 字段（`modreq`），由 C# 代码经接口调用；显式接口实现（`DefineMethodOverride`）。
2. **stage0 的方法表转发**：生成的方法体 `ldsfld IFn[] table; ldc.i4 i; ldelem.ref; ldarg…; callvirt invoke(k)`，验证可以在类型创建后再填表。
3. **`switch` 指令**与大量标签的方法；`PersistedAssemblyBuilder` 保存含接口、嵌套泛型引用（`List<object>`）、`params` 调用的程序集，另一进程加载。
4. **动态程序集数量**：每 1000 个类型一个程序集时，跨程序集的 `newobj`/`call` 是否要求类型公开（IronScheme 结论为是）；10000 个 `fn` 类的创建耗时。
5. **`Monitor` 可重入、`ThreadLocal` vs `AsyncLocal`** 的开销（Var 线程绑定用）。
6. 结论写回本文件末尾，并更新 05 §2 与 08 §4。

## 验收标准
- 每项给出 可行 / 不可行 / 有条件 的结论与最小复现。
