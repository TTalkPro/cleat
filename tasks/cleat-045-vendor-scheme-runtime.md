# cleat-045 纳入并更名 C# 元层

- 里程碑：M2′　依赖：cleat-017　状态：未开始
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
