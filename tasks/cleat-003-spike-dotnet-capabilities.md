# cleat-003 验证 .NET 10 的关键能力（Clojure 相关补充）

- 里程碑：M0　依赖：cleat-001　状态：完成
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

## 结论（2026-10-10，.NET 10.0.x，Linux x64）

验证程序：`spikes/cleat-003/`（`dotnet run -c Release -- <interfaces|table|switch|save|load <dll>|count|threading|all>`；`save` 会自动 spawn 一个 `dotnet Spike.dll load gen.dll` 子进程）。实测环境 .NET 10.0.12，Debian 13，x64。耗时为第二遍（排除 JIT）的数值。

| 项 | 结论 | 说明 |
|---|---|---|
| 内存程序集里的接口实现类型 | **可行** | `RunAndCollect` 程序集中 `DefineType(…, interfaces)`；公开可变字段 = `FieldAttributes.Public`；`volatile` 字段 = `DefineField(…, requiredCustomModifiers: [IsVolatile])` + 读写时 `volatile.` 前缀，反射 `GetRequiredCustomModifiers()` 能看到 `IsVolatile`；显式接口实现 = `Private \| Virtual \| Final \| HideBySig \| NewSlot` 的方法（任意名字）+ `DefineMethodOverride`，类型上没有公开的 `first`，接口映射指向私有方法。C# 经 `ISeq`、`IFn` 两个接口调用全部通过；同一类型同时显式实现生成程序集内定义的接口与宿主接口也通过 |
| stage0 方法表转发 | **可行** | 表是生成的持有者类型里的 `public static IFn[] table`，`CreateType()` 之后用反射 `SetValue` 填入 C# 对象；填表前调用抛 `NullReferenceException`；运行中替换表项立即生效（无内联缓存问题）。开销：直接 `callvirt` C# `IFn` 2.4 ns/次，经 `ldsfld; ldc.i4; ldelem.ref; ldarg…; callvirt` 转发 6.2 ns/次，**转发约 +4 ns/次** |
| `switch` 指令 | **可行** | 500 与 5000 个标签全部分支正确，越界走 default；5000 标签的方法首次 JIT 约 25 ms |
| PersistedAssemblyBuilder 保存接口、嵌套泛型、`params` | **可行** | 程序集内定义接口（`TypeAttributes.Interface \| Abstract`，方法 `Abstract \| Virtual \| NewSlot`；`TypeBuilder` 可直接作为接口传给 `DefineType` 与 `DefineMethodOverride`）；`List<object>` 的 `newobj`/`callvirt Add`、`Dictionary<string, List<object>>`（嵌套泛型实例化）直接用运行时 `Type` 取成员即可；生成带 `ParamArrayAttribute` 的 `params` 方法用 `DefineParameter().SetCustomAttribute`；生成代码调用 `params` 方法（`String.Format(string, object[])`、C# 的 `params`、生成的 `params`）必须自己 `newarr`/`stelem`，`ParamArrayAttribute` 只是给 C# 调用方的标记。`Save()` 17 ms，8 KB |
| 另一进程加载已保存程序集 | **可行** | 子进程 `Assembly.LoadFrom` 后 15 项检查全部通过；程序集引用宿主程序集（`Spike.dll`）里的 `ISeq`/`IFn`/`Helpers`，宿主已加载即可解析 |
| 10000 个 `fn` 类的创建耗时 | **有条件：每个程序集不超过约 1000 个类型** | `CreateType()` 的耗时随同一模块内类型数**二次增长**（先全部定义再全部创建，10000 类合计）：每程序集 100 / 500 / 1000 类 → `CreateType` 总计 121 / 147 / 180 ms；2000 / 5000 / 10000 类 → 1028 / 3063 / 6710 ms。每 1000 类一个程序集（10 个）：定义+发射 0.13 s、`CreateType` 0.17 s、实例化+首次调用（JIT）0.6 s，合计 0.9 s；单程序集 10000 类合计 7.6 s。逐个"定义后立即 `CreateType`"（stage0 按需编译 `fn` 的方式）：单程序集 3.3 s，分块 0.26 s。`Run` 与 `RunAndCollect` 耗时相同；工作集 +20 ~ 50 MB |
| 跨程序集 `newobj`/`call` 非公开类型 | **有条件：类型公开，或加属性** | 非公开类型：`CreateType` 成功，首次调用时 JIT 抛 `MethodAccessException`（IronScheme 结论相同）。两条替代路都验证可行：被引用程序集加 `[InternalsVisibleTo("B")]`（动态程序集上也生效）；或引用方程序集自己定义并标注 `System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute("A")`（运行时按名字识别） |
| 可回收与不可回收程序集互引 | **单向** | `Run` 程序集引用 `RunAndCollect` 程序集的类型在 `Emit` 时就抛 `NotSupportedException`（"A non-collectible assembly may not reference a collectible assembly"）；反向可以 |
| `Monitor` 可重入 | **可行** | 同线程 `Enter` 两次，退出一次其他线程仍进不去，退出两次才释放；多余的 `Exit` 抛 `SynchronizationLockException`。无竞争 `Enter`/`Exit` 一对 14.5 ns，嵌套两层 18.6 ns |
| `ThreadLocal` vs `AsyncLocal` | **用 `ThreadLocal`（或 `[ThreadStatic]`）** | 读：`[ThreadStatic]` 0.6 ns，`ThreadLocal<object>` 2.6 ns，`AsyncLocal<object>` 4.5 ns。写：`[ThreadStatic]` 0.7 ns，`ThreadLocal` 1.3 ns（无分配），`AsyncLocal` 写同一值 5.5 ns（短路）、交替值 **25 ns 且每次分配 72 B**（同时另有 8 个 `AsyncLocal` 有值时 50 ns、232 B）。语义：`AsyncLocal` 随 `ExecutionContext` 流到 `Task.Run` 和 `new Thread`（`SuppressFlow` 可关），`ThreadLocal` 不流 |

### 对设计的影响

1. 05 §2 的扩展项（接口与显式实现、`volatile` 的 `modreq`、公开可变字段、`switch`、`params` 调用、嵌套泛型引用）在内存程序集与 `PersistedAssemblyBuilder` 两条路上都可行，发射器不需要特殊处理：`params` 由 codegen 自建数组；生成 `definterface` 时 `TypeBuilder` 可直接作为接口被同一模块的其他类型实现。
2. 08 §4 stage0 的 `deftype*`/`reify*` 方法表转发方案成立，每次调用约多 4 ns；表字段放在类型自身或单独的持有者类型都可以，类型创建后再填表、运行中换表都没有问题。
3. stage0 / REPL 的动态类型：**每个 `AssemblyBuilder` 最多放约 1000 个类型**（超过 2000 后 `CreateType` 明显变慢，10000 个要 7 s），满了就开新程序集；`fn`/`deftype` 类声明为 `public`（与 Clojure 一致），跨块引用不需要 `InternalsVisibleTo`；所有动态程序集**统一用 `RunAndCollect`**（不可与 `Run` 混用，非可回收的不能引用可回收的）。
4. Var 的线程绑定用 `ThreadLocal<T>`（绑定帧对象，读 2.6 ns / 写 1.3 ns，不分配）或 `[ThreadStatic]`；不用 `AsyncLocal`：写一次 25 ns 并分配 72 B，而且隐式流到子任务，与 Clojure `binding` 不跨线程、`future` 由 `binding-conveyor-fn` 显式传递的语义不符。`locking` 直接用 `Monitor`，可重入符合 Clojure 语义。
