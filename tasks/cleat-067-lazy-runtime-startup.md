# cleat-067 包程序集、运行时 NuGet 包与惰性启动

- 里程碑：后续　依赖：cleat-043、cleat-063　状态：完成
- 设计依据：[designs/17](../designs/17-lazy-runtime-startup.md)。

## 背景

讨论（2026-10-13）：`.clj.dll` 是标准程序集，gen-class 等是真实 CLR 类型，但 .NET 程序不先显式启动 cleat 运行时就不能用；JVM 上
gen-class 类的静态初始化会经 `clojure.lang.RT` 自动拉起运行时。用户随后决定：包用 NuGet 分发（兼容 BaGetter 等自建仓库），
每个包编成一个程序集，去掉源码旁的 `.clj.dll` 预编译缓存，并把本任务排在 tools.build（cleat-066）之前，使发布的 NuGet 包能被
C# / F# 项目直接冷加载。

## 工作内容

1. **包程序集**（designs/17 §2）：编译驱动按 Clojure 镜像的样板把一个包的全部命名空间编成一个程序集，每个命名空间一个入口；
   程序集级特性 `CleatPackage` / `CleatNamespace` 作索引；接口文件与（可选的）源码作嵌入资源。
2. **加载器**（§2.3）：`require` 先查已登记包程序集的索引，再查源码；启动时与程序集加载时登记带索引的程序集，`-I` 目录里的 dll 也登记；
   去掉源码旁 `.clj.dll` 的查找、新旧比较与 `StaleModuleException` 回退；`compile` 改为编包程序集；随附 AOT 库改编进一个包程序集。
3. **惰性启动**（§4）：`RuntimeBootstrap.EnsureStarted`；`$Constants..cctor` 与 gen-class 初始化器开头调用它；`RuntimeHooks.OnLoaderThread`；
   `CleatRuntime.Var` / `Require` / `Eval` 自动 `Init`；镜像与驱动库按程序集名 / 嵌入资源找到（开发布局照旧）。
4. **运行时 NuGet 包**（§3）：`Cleat.Runtime` 的打包（`dotnet pack`）；Clojure 包的 nupkg 形状（依赖 `Cleat.Runtime`）。
5. **测试**：nrepl / neorepl 编成包程序集后两包测试通过；C# 与 F# 测试项目经本地 NuGet 源引用 `Cleat.Runtime` 与测试用 Clojure 包，
   不调用初始化 API 使用 gen-class 与 defrecord，`CleatRuntime.Var` 调函数，并发首次调用只启动一次。

## 验收标准

- designs/17 §6 全部满足；全量测试与镜像不动点通过；启动时间与基准不退化。

## 结论（2026-10-13）

完成。取舍与细节记在 [designs/17 §7](../designs/17-lazy-runtime-startup.md)。

- **包程序集**：宿主 `--compile-package out.dll [--package 名] [--root 目录]… ns…` 在一个进程、一个单元里编一个包；`(compile 'a.b)`
  编出 `a.b.dll`（这个文件与编译期间从源码加载到的文件）。nrepl 编成 `Nrepl.dll`（28 个命名空间）、neorepl 编成 `Neorepl.dll`（16 个），
  加载路径上只有包程序集时两包测试全部通过（nREPL 249 个测试、neorepl 86 个）；neorepl 的 CLI 启动约 1.1 秒（从源码约 7 秒）。
  同进程连着编多个命名空间引用 `eval$N` 内存类型的问题（cleat-063）随之消失。
- **加载器**：`require` 先查包索引（按元数据读 `CleatNamespace` 特性，键是加载路径），再查源码；源码旁 `.clj.dll` 的查找、新旧比较与
  `StaleModuleException` 回退都去掉了（对旧镜像编译的包程序集报错要求重编）。随附的 4 个 AOT 库编进 `src/boot/Cleat.Clojure.Libs.dll`
  （`tools/build.cs compile-libs`），宿主登记镜像旁的它。
- **整包共享常量表的副作用**：解码 Var 常量会提前建出还没加载的命名空间，`requiring-resolve` 因此拿到未绑定的 Var（neorepl 的 REPL
  命令全部失败）。改为**待定命名空间**：`find-ns` / `all-ns` 看不到，`in-ns` / `ns` / `create-ns` 或其中的 Var 第一次 def 时转正。
- **惰性启动**：`RuntimeBootstrap.EnsureStarted`（Cleat.Core，按名字反射调 `CleatRuntime.Init`）；包程序集带 Lcil 的 `(lazy-start)`，
  发射器在每个类型初始化器开头调用它。`CleatRuntime` 的 `Eval` / `Require` / `Var` 自动初始化，`Var` 先加载命名空间；
  源码加载经 `RuntimeHooks.OnLoaderThread` 排到 512 MB 栈的工作线程。冷加载的并发首次使用经 `RT.REQUIRE_LOCK` 串行化
  （gen-class 初始化器与 `CleatRuntime.Var` 同时加载同一个命名空间时只加载一次）。
- **运行时 NuGet 包**：`src/Cleat.Runtime`（不在解决方案里），`tools/build.cs pack [-o 目录] [--version 版本]`：lib/net10.0 是运行时程序集，
  镜像、驱动库、随附库在 `runtime/`，由 `buildTransitive/Cleat.Runtime.targets` 复制到引用方的输出目录。
- **C# / F# 能引用的元数据**：PersistedAssemblyBuilder 把框架类型都写成引用 `System.Private.CoreLib`，C# 编译报 CS0012。发射器保存
  dll 时按共享框架里门面程序集的类型转发把 TypeRef 改指契约程序集（`System.Runtime`、`System.Collections` …；`ContractRetargeting`）。
- **验收测试**：`NuGetColdLoadTests`——pack 出 `Cleat.Runtime`，测试用 Clojure 包（gen-class 带 `:main`、defrecord、函数）编成包程序集
  并打成 nupkg，放进本地目录源；C# 与 F# 控制台项目只引用该包、不调用初始化 API：gen-class 的构造函数 / 实例方法 / 静态方法 / `Main`、
  defrecord、`CleatRuntime.Var` 都可用，三个线程并发首次使用后命名空间只加载了一次。`PackageTests.BothFromPackageAssemblies`
  从包程序集跑两包测试。包程序集过 ILVerify；全量 607/607。
- **没做**：源码作为嵌入资源（`:file` 仍是构建时路径）；`cleat.edn` 的别名式启动（如 `cleat -X:repl`）不在本任务，留给 tools.build 一层。
