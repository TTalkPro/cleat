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

## cleat 新增：`Clojure/`（cleat-050 ~ cleat-052）

Clojure 在 Scheme 元层上运行所需的 C# 部分（命名空间 `Cleat.Scheme.Clojure`，由 `src/scheme/clojure/*.sls` 使用）：
- `ClojureOps`：编译后的 Clojure 代码经编译器的 `$clj-call` 节点直接调用的静态方法（集合字面量、Reflector 互操作、`case*`
  的下标、deftype* 的类型登记与值转换，参数与返回都是 object）；以及展开期用的 `Procedure`（宏调用、元数据与集合遍历）——
  不用 Scheme 的 `clr-call`，因为它会把返回的 long 转成 fixnum、枚举转成符号（值边界，designs/13 §7）。
  `fn*` 的值是编译器生成的 `AFunction` / `RestFn` 子类（cleat-051，原来的 `ClojureFn` 包装已删除）。
- `HostSyntax`：展开期的类型名 / 类型提示决议与 deftype* / reify* 的接口方法匹配、类型布局（规则照 C# 展开器）。
- `Driver`（cleat-052）：编译驱动 `(clojure driver)` 的 C# 一侧——简单形式的小解释器（不生成程序集）、读形式、带位置执行
  （异常不包装，位置记在 `Exception.Data`）、顶层 do、把 Scheme 过程包成 `Cleat.Core.Runtime.RuntimeHooks` 的委托、
  Clojure 风格的错误文本、`.clj.dll` 的输出路径与接口文件 `.ifc`。
- `ClojureHost`（cleat-052）：宿主装入驱动的 Scheme 库——读 `src/boot/cleat.scheme.clojure/cleat-host.manifest`
  （`clojure/build-host.sps` 构建时生成的 fasl），延后库（编译器、nanopass）在第一次 AOT 编译时装入；不可用时退回源码。
