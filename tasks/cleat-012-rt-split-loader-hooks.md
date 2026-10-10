# cleat-012 RT 拆分、加载器与运行时钩子

- 里程碑：M1　依赖：cleat-007、cleat-009、cleat-010　状态：完成
- 设计依据：[02 §6、§9](../designs/02-runtime.md)

## 目标
`RT` 只剩值操作与初始化；加载与编译经钩子。

## 工作内容
1. `RT.cs` 复制改写：去 `Microsoft.Scripting.Hosting`、`Compiler.*`；`load`/`loadFile`/`classForName` 的编译器部分移到 `Cleat.Core.Runtime.Loader`，通过 `RuntimeHooks.Compile`/`LoadSource` 回调。
2. `RuntimeHooks`：`PrintMethod`、`Compile`、`LoadSource`、`Macroexpand`（展开器登记）、`Err`；`$set-hook!` 原语。
3. `Loader`：加载路径（`CLEAT_LOAD_PATH`、`-I`）、`.clj.dll` 与源码的新旧比较、`[CleatModule]` 入口调用、内存程序集 `Run`。
4. 启动流程：登记原语 → 建 `clojure.core`/`cleat.kernel` → （有 boot 镜像）加载镜像 →（无）等待 stage0 从源码加载。
5. 测试：钩子未登记时的错误退路；加载路径解析。

## 验收标准
- `Cleat.Core` 不引用任何编译器类型；初始化在无 boot 镜像时也能完成。

## 结论（2026-10-10）

完成。
- `RuntimeHooks`（`src/Cleat.Core/Runtime/RuntimeHooks.cs`）：类型化的槽 `LoadSource` / `Compile`（`(reader, sourcePath, sourceName) → 最后一个形式的值`）、`Eval`、`Macroexpand1`、`PrintMethod`、`ReportError`、`OnInit`；`Set(name, fn)` 接受 Clojure `IFn`（或直接给委托）并包成委托，名字 `load-source` / `compile` / `eval` / `macroexpand-1` / `print-method` / `report-error` / `on-init`，供 cleat-011 的 `$set-hook!` 原语调用；`Require(hook, name)` 未登记时抛 `InvalidOperationException("runtime hook 'load-source' is not registered (no boot image loaded?)")`。
- `Loader`（`src/Cleat.Core/Runtime/Loader.cs`）：`LoadPath`（`CLEAT_LOAD_PATH` + 入口程序集目录 + `AppContext.BaseDirectory` + 当前目录，`AddPath` 给 `-I`）；`LoadModule(path, inMemory)`（`~/` = 运行时基目录，读 `[CleatModule]`，调 `public static object 入口()`）与 `Run(Assembly)`（内存 eval 程序集）；`TryLoad` 按目录顺序找 `rel.cljr/.cljc/.clj` 与 `rel(/→.).{cljr,cljc,clj}.dll`，dll 不比源码旧时走 `LoadModule`，否则开 `StreamReader` 交给 `LoadSource` 钩子（`*compile-files*` 为真时 `Compile` 钩子），外层绑定 `*ns*` / `*warn-on-reflection*` / `*unchecked-math*`，内层绑定 `*file*`（全路径）/ `*source-path*`（名字）；之后退到 `*ns-load-mappings*` 与嵌入资源；找不到抛带搜索路径的 `FileNotFoundException`。`LoadedAssemblies` 登记表 + `AssemblyLoadContext.Default.Resolving`（先查登记表，再在 `LoadPath` 上找 `名字.dll`），`Type.GetType("T, x.y.clj")` 能找到已载入的模块。`*loaded-libs*` 仍由 core.clj 的 `load-lib` 维护，这里不碰。
- `RT.cs`（改动均标 `// CLEAT-012:`）：7 处 cleat-005 存根全部接上——`load-file` → `Loader.LoadFile`，`RT.load` → `Loader.TryLoad`，`LoadScript` → `Loader.LoadFile`，`Compile`/`TryLoadFromEmbeddedResource` 删除（并入 Loader）；静态构造去掉 `Clojure.Source.dll` 探测与 `load("clojure/core")` 引导；`LoadSpecCode` / `PostBootstrapInit` / spec 开关删除（cleat 无 spec）；`RT.Init()` 幂等：建 `clojure.core` / `user`，`*ns*` 根指向 `user`，触发 Loader 静态初始化，调 `RuntimeHooks.OnInit`。原来 Compiler.cs 里的 Var 补进 RT：`*compile-files*`、`*compile-path*`、`*compiler-options*`、`*source-path*`、`*file*`（`*warn-on-reflection*`、`*unchecked-math*` 本来就在；`*loaded-libs*`、`*print-length*`、`*print-level*`、`*print-namespace-maps*` 由 core.clj 自己 def）。`classForName` 的 `Compiler.FindDuplicateType` 改为先查 `Loader.FindType`；`RT.FindFile` 的搜索路径并入 `Loader.LoadPath`。
- 启动顺序写在 `Loader.cs` 顶部注释：登记原语（cleat-011）→ `RT.Init()` →（有镜像）`Loader.LoadModule("~/cleat.boot.dll")`，镜像入口登记钩子 /（无镜像）stage0 自己登记钩子再从源码加载 → REPL（cleat-030）。
- 测试 `LoaderTests`（9 个）：钩子未登记的明确错误；`Set` 包装 `IFn` 与拒绝未知名字；`Load("x/y")` 把文件内容、全路径、`x/y.clj` 交给钩子并绑定 `*file*` / `*source-path*`、`*ns*` 事后恢复，`load-file` Var 与 `RT.load` 同路；`*compile-files*` 切到 `Compile` 钩子；用 `LcilEmitter.Save` 生成更新的 `x.y.clj.dll` 时跑模块入口而不是钩子（源码更新后反过来），`Type.GetType("Entry, x.y.clj")` 可解析；`LoadModule` / `Run` 对缺属性、缺文件的报错；缺库的 `FileNotFoundException` 含搜索路径与下划线提示；`AddPath` / `~/` 解析；`RT.Init` 两次无副作用且 `*ns*` 根为 `user`。
- 验收："`Cleat.Core` 不引用任何编译器类型"：`grep Compiler\.` 只剩注释；"无 boot 镜像也能初始化"：`RT.Init()` 不依赖任何钩子。待接：cleat-011 的 `$set-hook!` 原语调 `RuntimeHooks.Set`；cleat-015 / cleat-028 登记 `load-source` / `compile` / `eval`；cleat-022 的接口文件 `.ifc` 与镜像 id 核对在 `LoadModule` 旁加。
