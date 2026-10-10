# cleat-045 纳入并更名 C# 元层

- 里程碑：M2′　依赖：cleat-017　状态：完成
- 设计依据：[15 §1](../designs/15-scheme-core-vendoring.md)

## 目标
把 IronScheme 的 C# 元层纳入本仓库并更名，`dotnet build` / `dotnet test` 通过。

## 工作内容
1. `src/Cleat.Scheme/` ← `IronScheme.Core`：命名空间与程序集改 `Cleat.Scheme`；去 `AsyncFrame` / `Tasks` / `Ffi` / 调试器钩子；`ExceptionHandlers` 与 `Control.cs` 的逃逸续延先保留（cleat-047 删）；`SchemeModuleAttribute` 改用 `Cleat.Core.CleatModuleAttribute`（`Cleat.Core` 不引用 `Cleat.Scheme`，反向引用也不要：属性放到一个两者共用的小程序集 `Cleat.Abi` 或由 `Cleat.Scheme` 定义、`Cleat.Core` 引用——任选其一并记录）。
2. `src/Cleat.Stage0/` ← `IronScheme.Stage0`（Scheme Lcore 解释器）。
3. `src/Cleat.Emit/`：与 IronScheme.Emit 比对，合并 `EmitInterop`（类型操作数经 `Cleat.Scheme.TypeResolver` 还是 `Cleat.Core.Runtime.TypeResolver`：按 Lcil 方法级标志选择，或统一到一个解析器）、常量 fasl 合并为一种编码（15 §1）；`FaslEncoder` 实现 IronScheme 编码 + Clojure 标签；`Cleat.Scheme.Fasl` 读回全部标签。
4. `src/Cleat.PrimGen/`：反射 `Cleat.Scheme` 的 `[Primitive]` 与 `Cleat.Core.Runtime.Primitives.Table`，生成 `src/boot/primitives.ss`（Scheme 编译器用）与 `primitives.edn`。
5. 测试：`tests/Cleat.Scheme.Tests/` ← IronScheme 的运行时、发射器、stage0 单测（去 async / FFI / R6RS 符合性）。
6. `NOTICE` 加 IronScheme（BSD）条目；`.editorconfig` 对 `Cleat.Scheme` 沿用 IronScheme 09 的规范。

## 验收标准
- `dotnet build` 通过；纳入的测试通过；`Cleat.Core.Tests` 不退化。

## 结论（2026-10-10）

来源：IronScheme 仓库（`/home/david/workspace/IronScheme`）提交 `26386793ee27f478a2e69ccdc17bc10a3a134944`，BSD 3-Clause；`NOTICE` 加条目，许可全文在 `src/Cleat.Scheme/LICENSE-BSD.txt`，来源与改动清单在 `src/Cleat.Scheme/README.md`。

### 纳入了什么

| 目标 | 来源 | 行数 | 说明 |
|---|---|---|---|
| `src/Cleat.Scheme/`（50 个 .cs，10,984 行） | `IronScheme.Core`（11,579 行） | 命名空间与程序集 `IronScheme.Core` → `Cleat.Scheme`（含反射用的类型名字符串；`TypeResolver` 里 `IronScheme.Runtime.Builtins` 这个**键**保留，因为 nanopass 的适配文件按这个名字调用，值改为 `Cleat.Scheme.Builtins`） | 引用 `Cleat.Core`（`CleatModuleAttribute`、fasl 读回 Clojure 数据）；`Cleat.Core` 不反向引用，`SmokeTests` 断言这一点 |
| `src/Cleat.Stage0/`（5 个 .cs，1,050 行） | `IronScheme.Stage0` | 原样更名；没有 async 节点；`escape-frame`（call/cc 原语）与 `$try/finally` 先留着，cleat-047 改 | 程序集名仍为 `stage0`；引用 `Cleat.Scheme` 与 `Cleat.Emit` |
| `src/Cleat.Emit/`（2,253 行，+257） | `IronScheme.Emit` 比对合并 | 新增 `SchemeData.cs`（Scheme `Cons` / `Symbol` → SNode，与 `ClojureData.cs` 并列，按顶层对象所属程序集二选一）；`EmitInterop.cs` 改为两套 `CoreApi`；`FaslEncoder.cs` 联合编码；`Model.cs` / `Emitter.cs` 加 `runtime` 子句；`live-constants` cleat 早已有 | `LCIL.md` 同步 |
| `src/Cleat.PrimGen/`（157 行） | `IronScheme.PrimGen` 合并 | 同时生成 `src/boot/primitives.edn`（Cleat.Core 表，格式不变）与 `src/boot/primitives.ss`（IronScheme 格式：465 个 `[Primitive]` 原语 + 62 个 `cleat.kernel/…` 条目 + 10 个 special；去掉 `await`） | 构建后 Exec 目标生成两个文件；`--check` 模式保留 |
| `tests/Cleat.Scheme.Tests/`（21 个 .cs，2,802 行 + 7 个 Lcil 样例） | `IronScheme.Core.Tests` | 208 个测试全部通过：运行时（数、符号、过程、记录、哈希表、端口、reader、互操作、钩子）、发射器、fasl、模块加载、stage0、原语表 | 新增 `RuntimeClauseTests`（3 个）验证 `runtime` 子句与混合常量表 |

### 删了什么

- `AsyncFrame.cs`、`Tasks.cs`、`Interop/Ffi.cs`、`ConstantPool.cs`（用 `Cleat.Core.Runtime.ConstantPool`）：共 622 行；`ClrHelpers.cs` 的 FFI 转换辅助、`Interop.cs` 的 Task 装箱分支。
- `Parameters.cs`：`ExceptionHandlers` 改为 `[ThreadStatic]` 的处理器栈（不再有 async 帧）；`ParamSlot` 保留 `AsyncLocal`（参数对象随 ExecutionContext 流动与 async 无关），只去掉"帧内不写回退值"的判断。
- `SchemeModuleAttribute` 删除，`ModuleLoader` 读 `Cleat.Core.CleatModuleAttribute`（**属性放在 Cleat.Core**：它已经存在、发射器已经写它，`Cleat.Scheme` 单向引用 `Cleat.Core` 即可，不需要 `Cleat.Abi`）。
- 测试：`AsyncTests`、`HostTests`（需要 IronScheme.Host 与 boot 镜像）、`LineEditorTests`（宿主）、`Stage0ScriptTests`（需要 `src/scheme/tests`，cleat-046 纳入 Scheme 源码后再接）：共 573 行不纳入；`InteropTests.FfiHelpers` 改为只测非 FFI 的辅助方法。
- 调试器钩子：IronScheme.Core 里没有独立的调试器钩子（只有 `$break` → `Debugger.Break` 与 `debug-mode?` 标志），原样保留。
- `src/Cleat.Core/Runtime/Fasl.cs` 删除（Cleat.Core 内部没有使用它），`tests/Cleat.Core.Tests/FaslTests.cs` 改为测 `Cleat.Scheme.Fasl`。

### 决定

1. **Lcil 的 `(runtime scheme|clojure)` 子句**：程序集级设缺省（缺省 `clojure`），方法级可覆盖。发射器按方法选 `CoreApi`：`clojure` 用 `Cleat.Core.Runtime.TypeResolver` / `Reflector`（void → nil，`long` 等只装箱）；`scheme` 用 `Cleat.Scheme.TypeResolver` / `Interop`（void → 未指定值，字符串规范化，尖括号泛型转成符号说明）。隐含引用：`Cleat.Core` 总是，`Cleat.Scheme` 在用到 scheme 或有编码常量表时。见 `LCIL.md` §2、§5。
2. **常量 fasl 联合编码**：IronScheme 的 26 个标签 + cleat 的 21 个标签各占独立字节（`0x0_` 单例、`0x1_` 数、`0x2_` 字符 / 字符串 / 符号、`0x3_` 序列、`0x4_` Scheme 记录等、`0x5_` Clojure 其他、`0x6_` def / ref），magic 保持 `CLFASL`；一张表可以混用。两边同名的 `str` / `sym` / `list` / `vec` 按程序集的 `runtime` 解释（这是唯一的歧义点：`(sym "x")` 在 scheme 下是驻留的 Scheme 符号、在 clojure 下是 `clojure.lang.Symbol`）。`Cleat.Scheme.Fasl` 读回全部标签、写出全部对象；`Write(obj, clojure: true)` 把 CLR 字符串写成 Clojure 字符串（缺省按 Scheme 字符串：含 BMP 外字符的读回为宽串，iron-055 的契约）。见 `LCIL.md` §8。
3. `fasl-write` 遇到不能序列化的对象报 `assertion-violation`（它是 Scheme 原语 `serialize-port` 的后端），cleat 的 `FaslTests` 把原来的 `NotSupportedException` 断言改为 `SchemeRuntimeError`。
4. `primitives.ss` 里 `cleat.kernel/…` 条目的内联模板一律 `#f`（Scheme 编译器按名字查模板，cleat 的模板在发射器里），`inline` 作为标志保留信息；Scheme 编译器用 `memq` 读标志，多一个标志无害。
5. `.editorconfig` 两仓库对 C# 完全一致，只把 2 空格缩进的 glob 扩到 `sls / ss / sps / scm / core`。

### 验证

`dotnet build Cleat.slnx` 通过（只有 IronScheme 原有的 2 个 CA2013 与 1 个 CS7022 警告）；`dotnet test Cleat.slnx`：`Cleat.Core.Tests` 129 通过（不退化），`Cleat.Scheme.Tests` 208 通过。

### 未做 / 留给后续

- `tools/build.cs` 的 `stage1` / `seed` / `ilverify` 与 `GenerateSchemeBootImage` 目标：随 Scheme 源码与种子在 cleat-046 合并。
- `designs/05-cil-backend.md` 仍写 `Cleat.Core.Runtime.Fasl` 读回常量（本任务不改设计文档）；实际是 `Cleat.Scheme.Fasl`。
- `Cleat.Scheme.Interop` 与 `Cleat.Core.Runtime.Reflector` 仍是两套互操作实现（设计 15 §1 的"统一到一个解析器"选项未取）：以方法级 `runtime` 选择为准，等 cleat-051 的 Clojure 节点落地后再看是否合并。
