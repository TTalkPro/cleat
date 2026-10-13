# cleat-067 包程序集、运行时 NuGet 包与惰性启动

- 里程碑：后续　依赖：cleat-043、cleat-063　状态：未开始
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
