# Cleat.Scheme

Scheme 元层的 C# 运行时（designs/15 §1）：过程、符号、数值塔、记录与条件、端口、reader、CLR 互操作、fasl、运行时钩子。

## 来源

从 IronScheme 仓库（https://github.com/IronScheme/IronScheme ，本地 /home/david/workspace/IronScheme）的
`src/IronScheme.Core/` 纳入，提交 `26386793ee27f478a2e69ccdc17bc10a3a134944`（2026-10-10），
命名空间与程序集 `IronScheme.Core` → `Cleat.Scheme`。

IronScheme 以 BSD 3-Clause 许可发布：Copyright (c) 2007-2024, Llewellyn Pritchard；2026 年的新运行时
Copyright (c) 2026 the IronScheme contributors。许可全文见本目录 LICENSE-BSD.txt（仓库根目录 NOTICE 也有条目）。

## 相对 IronScheme.Core 的改动（cleat-045）

- 删除：`AsyncFrame.cs`、`Tasks.cs`（async-lambda 与 task 原语）、`Interop/Ffi.cs`（ffi-callout / pinvoke-call / ffi-callback）、
  `ClrHelpers.cs` 的 FFI 转换辅助、`Interop.cs` 的 Task 装箱、`ConstantPool.cs`（用 `Cleat.Core.Runtime.ConstantPool`）。
- `Parameters.cs`：`ParamSlot` 不再区分 async 帧。
- `ModuleLoader.cs`：删 `SchemeModuleAttribute`，入口属性改用 `Cleat.Core.CleatModuleAttribute`。
- `Fasl.cs`：联合编码（IronScheme 的标签 + cleat 的 Clojure 标签），magic `CLFASL`。
- 逃逸续延（`Control.cs` 的 `call/cc`、`EscapeContinuation`、`ContinuationException`）与处理器栈（`ExceptionHandlers`、
  `$current-handlers`）已删除（cleat-047，designs/15 §3）：`raise` 抛 `SchemeException(条件)`，捕获用核心形式 `$try/catch`
  （stage0 的 `TryCatch` 节点、编译器的 `try-catch` 节点 → Lcil `try`/`catch`）。

## cleat 新增：`Clojure/`（cleat-050）

Clojure 在 Scheme 元层上运行所需的 C# 部分（命名空间 `Cleat.Scheme.Clojure`，由 `src/scheme/clojure/syntax.sls` 使用）：
- `ClojureFn`：`fn*` 的值，`clojure.lang.AFunction` 包着展开器生成的 Scheme 过程（固定参数子句的 case-lambda + 变长子句），
  ArityException 用 Clojure 的类名规则。
- `ClojureOps`：Clojure 代码的运行时操作（IFn 调用、Var 读写、集合字面量、Reflector 互操作、case* 分派等），都是 `Procedure`，
  因为 Scheme 的 `clr-call` 会把返回的 long 转成 fixnum、枚举转成符号（值边界，designs/13 §7）。
- `HostSyntax`：展开期的类型名 / 类型提示决议与 deftype* / reify* 的接口方法匹配（规则照 C# 展开器）。
