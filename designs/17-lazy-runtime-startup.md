# 17 包程序集、运行时 NuGet 包与惰性启动：让 C# / F# 直接引用 cleat 的包

状态：**实现**（2026-10-13，任务 [cleat-067](../tasks/cleat-067-lazy-runtime-startup.md)；先于 tools.build 包 cleat-066）。实现中的取舍见 §7。

## 1. 问题与目标

AOT 产物现在是每个命名空间一个 `.clj.dll`（ClojureCLR 的命名：`a/b.clj` → `a.b.clj.dll`），写在源码旁边，`require` 时与源码比新旧。
它有三个问题：

1. **不能被 .NET 冷加载**。生成的类型（gen-class、deftype、defrecord、proxy）是真实 CLR 类型，但用它们之前必须先显式启动 cleat
   运行时（登记原语、`RT.Init`、Scheme 镜像、驱动库、钩子、Clojure 镜像），只有宿主 `cleat` 与 `CleatRuntime.Init`（cleat-035）做这件事。
   JVM 上 gen-class 类的静态初始化经 `clojure.lang.RT` 自动拉起运行时，Java 代码直接 `new` 就行。
2. **粒度不对**。nrepl 加 neorepl 编出 44 个程序集；NuGet 包、C# / F# 项目引用的单位应该是包。JVM 上一个包是一个 jar。
3. **同进程连着编译多个命名空间不行**。每个文件一个编译单元，后编的单元引用前面那个求值时生成的内存类型（`eval$N` 程序集）。

用户决定（2026-10-13）：包用 **NuGet** 分发并兼容 **BaGetter** 等自建仓库；**每个包编成一个程序集**；**去掉源码旁的预编译缓存**
（开发时一律从源码加载，预编译只在构建时）；先做本任务，使发布到 NuGet 的包能被 C# / F# 项目直接冷加载，再做 tools.build（cleat-066）。

目标：

- C# / F# 项目 `dotnet add package Nrepl`（以及 cleat 运行时包），不调用任何初始化 API，直接使用 gen-class 生成的类型；
  也能经 `CleatRuntime.Var("ns", "f")` 调普通 Clojure 函数（相当于 `clojure.java.api.Clojure`）。
- cleat 宿主与 Clojure 代码 `require` 已引用（NuGet 解析到）的包程序集里的命名空间。

## 2. 包程序集

### 2.1 形状

一个包（如 nrepl）的全部命名空间在**一个进程、一个编译单元**里编成**一个程序集**，名字是包的程序集名（如 `Nrepl.dll`，由构建配置给出）。
同包里的跨命名空间类型引用都在本程序集内解析，第 1 节问题 3 不复存在。

程序集里每个命名空间一个初始化入口（静态方法，体是该文件的顶层形式，与现在单个 `.clj.dll` 的入口相同），外加程序集级的索引特性：

```csharp
[assembly: Cleat.Core.CleatPackage("nrepl")]                         // 包名（可省）
[assembly: Cleat.Core.CleatNamespace("nrepl.server", "nrepl.server$ns", "Init")]  // 命名空间 → 入口类型与方法
[assembly: Cleat.Core.CleatNamespace("nrepl.transport", …)]
```

常量表照旧按单元生成（`$Constants`，fasl 编码）；接口文件（`.ifc`，跨单元直接链接与内联用）作为嵌入资源放进程序集，不再是旁边的文件。
源码可选地作为嵌入资源（`clojure.repl/source`、nREPL 的 lookup、neorepl 的 `,edit` 需要文件位置；`:file` 元数据记为包内的相对路径）。

### 2.2 编译

以构建 Clojure 镜像的机制为样板（`clojure-compile-boot`：`boot-build` 下被加载的文件的形式都进同一个单元）：在一个进程里按依赖顺序
加载包的命名空间，记录每个顶层形式属于哪个文件，最后整个单元编成一个程序集，按文件分出各命名空间的入口。入口之间的依赖由形式自身表达：
文件 A 的 `(ns A (:require B))` 在运行时 require B，经索引跑 B 的入口——命名空间仍是惰性、按需初始化的，不会因为引用了包就初始化全部命名空间。

### 2.3 加载

`Loader.TryLoad`（`require` / `load`）的顺序改为：

1. **已登记的包程序集的索引**里有这个命名空间 → 跑它的入口（每个命名空间只跑一次，`:reload` 再跑）。
2. 加载路径上的源码（`.cljr` `.cljc` `.clj`）。
3. `*ns-load-mappings*`、嵌入资源（照旧）。

包程序集何时"登记"：运行时启动时扫描当前进程已加载的程序集与应用的依赖清单（`AppContext` 的 deps.json 里的运行时程序集），
带 `CleatNamespace` 特性的登记索引（只读特性，不加载类型）；之后 `AssemblyLoadContext.Default.Resolving` / 程序集加载事件里登记新来的。
宿主的 `-I` 目录里的 `*.dll` 也登记（开发时引用本地构建的包）。

**去掉**：源码旁 `.clj.dll` 的查找与新旧比较、`StaleModuleException` 回退、`compile` 写 `*compile-path*` 下单个命名空间 dll
（`compile` 改为编出包程序集，由 tools.build 调用；单个命名空间的 `(compile 'ns)` 成为只含一个命名空间的包）。随附命名空间
（`clojure.core.reducers` 等现在的 4 个 AOT 库）改编进一个随运行时发布的包程序集。

## 3. 运行时 NuGet 包

包 `Cleat.Runtime`（暂名）：`lib/net10.0/` 下 `Cleat.Core.dll`、`Cleat.Scheme.dll`、`Cleat.Hosting.dll`、Scheme 镜像
`cleat.scheme.boot.dll`、Clojure 镜像 `cleat.boot.dll`、随附库的包程序集；驱动库（`cleat.scheme.clojure/` 的 fasl 与清单）作为
`Cleat.Scheme` 或单独程序集的嵌入资源。镜像本身就是程序集，按程序集名经依赖清单找到，不再按文件路径找（宿主的开发布局——
`src/boot/` 与环境变量——仍然支持）。

Clojure 的包（如 `Nrepl`）的 nupkg 依赖 `Cleat.Runtime`（版本区间）与它自己的 Clojure 包依赖；`lib/net10.0/` 里只有它的包程序集。
打包与推送（nuget.org、BaGetter）由 tools.build 的 `pack` / `push` 做（cleat-066）。

## 4. 惰性启动

### 4.1 触发点

发射器在包程序集里两处插入 `Cleat.Core.Runtime.RuntimeBootstrap.EnsureStarted(typeof(…).Assembly)`：

- 每个单元的 `$Constants..cctor` 开头——覆盖程序集里全部生成代码（gen-class、deftype、defrecord、proxy、fn 类）：它们第一次用常量之前
  运行时已经起来，fasl 解码也在其后；
- gen-class 类型的初始化器开头（它在 intern Var 之前，不一定先用到常量）。

运行时已启动时代价是一次 volatile 读。gen-class 类型的初始化器随后 require 它的实现命名空间（`:load-impl-ns`），经 2.3 跑包里的入口。

### 4.2 `RuntimeBootstrap`（Cleat.Core）

- `EnsureStarted(Assembly caller)`：已启动或正在启动（宿主 / Hosting 的启动过程、镜像初始化期间）时返回；否则加锁只启动一次。
- Cleat.Core 不引用 Hosting（依赖方向 Hosting → Scheme → Core），所以按名字 `Assembly.Load("Cleat.Hosting")` 并反射调用
  `CleatRuntime.Init`；宿主与 Hosting 启动时登记"已启动"。找不到 Hosting 或镜像时抛出说明清楚的异常（缺什么、在哪里找过）。
- `CleatRuntime.Var` / `Require` / `Eval` 未初始化时自动以缺省选项 `Init`。

### 4.3 线程与栈

展开与编译递归深，要大栈；`CleatRuntime` 已把它的调用排到 512 MB 栈的工作线程。惰性启动经同一条路，启动之后仍在调用方线程上发生的
加载（gen-class 初始化器 require 实现命名空间、命名空间入口里的 require）若要从**源码**编译，经加载器钩子 `RuntimeHooks.OnLoaderThread`
排到工作线程；跑包程序集里的入口不需要（已编译）。打包发布的场景下命名空间都在包程序集里，源码编译只出现在开发时。

**风险**：类型初始化器在调用方线程持锁时，工作线程若要初始化同一个类型会死锁。需实测 gen-class 的实现命名空间引用该类型本身、
`:load-impl-ns` 与入口交错的情形；必要时把 require 实现命名空间推迟到第一次调用方法时。

## 5. 不做

- 不让源码加载 / REPL 求值产生的动态程序集被外部引用。
- 不改函数类的调用约定；从 C# / F# 调普通 Clojure 函数经 `CleatRuntime.Var(…).invoke(…)`，gen-class 是给 .NET 的稳定表面。
- 不做裁剪（trimming）与 NativeAOT：互操作靠反射元数据，运行时要生成代码。

## 6. 验收

- 包程序集：nrepl、neorepl 各编成一个程序集；宿主经 `-I` 引用它们时 `require` 走包程序集，两包的测试通过；neorepl 的 CLI 启动约 1.5 秒内。
- 冷加载：一个只引用 `Cleat.Runtime` 与某个 Clojure 包的 C# 项目和一个 F# 项目，**不调用任何初始化 API**：gen-class 的实例方法、
  静态方法、`Main` 入口、defrecord 实例都能用，`CleatRuntime.Var` 能调普通函数；并发的首次调用只启动一次。
- 本地打包：`dotnet pack` 出 `Cleat.Runtime` 与测试用 Clojure 包的 nupkg，经本地目录源（与 BaGetter 同一机制）被测试项目 restore。
- 宿主与嵌入的现有行为、启动时间、基准不退化；镜像不动点与全量测试通过；ILVerify 通过。

## 7. 实现记录（cleat-067）

- **索引的键是加载路径**（`nrepl/server`，与 `Loader.TryLoad` 收到的相同），不是命名空间名：`load` 的非命名空间文件（如 `clojure/core_print`）
  也能进包。特性写成 `CleatNamespace("nrepl/server", "#Module", "Load<i>")`。
- **入口**：整个包一个单元（每个顶层形式一个 `Init<k>`），编译驱动改写 Lcil：`Initialize`（CleatModule 入口）只做镜像核对与预链接，
  加载器对每个程序集跑一次；每个文件一个 `Load<i>` 依次调它的 `Init<k>`。Scheme 编译器的 `generate-unit` 不变（镜像不动点不受影响）。
- **常量表整包共享**：解码 `(var ns name)` 会 intern Var。为了不让还没加载的命名空间提前出现（`requiring-resolve`、`find-ns` 会看错），
  这些 Var 所在的命名空间建成**待定**的（`Namespace.findOrCreatePending`）：不在 `all-ns` / `find-ns` 里，`in-ns` / `ns` /
  `create-ns`（`findOrCreate`）时同一个对象转正，Var 的身份不变。
- **惰性启动的触发点**：Lcil 的程序集子句 `(lazy-start)` 让发射器在**每个**类型初始化器开头调 `RuntimeBootstrap.EnsureStarted()`，
  没有类型初始化器的类型（接口除外）补一个——`$Constants`、gen-class、deftype / defrecord、fn 类一律覆盖，不必区分。
- **接口文件**作为嵌入资源 `cleat.ifc` / `cleat.inl`（Lcil 的 `(resource "名字" "文件")`）；嵌入的不核对镜像 id。
- **登记**：进程里已加载与之后加载的程序集（AssemblyLoad）、TPA（第一次查找时扫一次，跳过框架目录与 `Cleat.*` / `System.*` /
  `Microsoft.*`）、`Loader.AddPath` 的目录里的 `*.dll`（按元数据读，不载入）。同一个键先登记者赢。`*compile-files*` 为真时不用包程序集。
- **`compile`**：`(compile 'a.b)` 把这个文件与编译期间从源码加载到的文件编成包程序集 `a.b.dll`（`*compile-path*` 或源码根目录）；
  宿主 `--compile-package out.dll [--package 名] [--root 目录]… ns…` 编一个包（只收 `--root` 下的文件，缺省全部 `-I`）。
- **随附库**：`tools/build.cs compile-libs` 编出 `src/boot/Cleat.Clojure.Libs.dll`，宿主登记镜像旁的它；比 Clojure 镜像旧时不登记
  （镜像重建后没有重编，这些命名空间从源码加载）——没有了"对旧镜像编译则回退源码"，这是唯一保留的新旧比较。
- **源码嵌入**（§2.1 的"可选"）没有做：`:file` 元数据仍是构建时的路径。
- **neorepl 的 jack-in** 经 `PackageIndex.Locate("nrepl/cmdline")` 找到 nREPL 的包程序集目录交给服务进程的 `-I`。
