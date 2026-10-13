# 17 运行时的惰性启动：让 .NET 直接使用 cleat 生成的类型

状态：**设计**（2026-10-13，任务 [cleat-067](../tasks/cleat-067-lazy-runtime-startup.md)；排在 tools.build 包 cleat-066 之后）。

## 1. 问题

AOT 产物 `.clj.dll` 是标准的 .NET 程序集，`gen-class` / `deftype` / `defrecord` / `proxy` 生成的是真实的 CLR 类型（cleat-043），
C# 可以按名字、按签名引用它们。但 C# 程序**不先启动 cleat 运行时**就用这些类型会失败：类型的代码要用 Var、关键字、命名空间，
模块的常量表要经 fasl 解码 Clojure 数据，而这些都要求运行时已经启动——登记原语、`RT.Init`、加载 Scheme 镜像
`cleat.scheme.boot.dll`、装入驱动库、登记钩子、加载 Clojure 镜像 `cleat.boot.dll`。现在这一步只能显式做：宿主 `cleat`
（Cleat.Host 的 `Start`）或嵌入 API `CleatRuntime.Init`（Cleat.Hosting，cleat-035）。

JVM 上没有这个问题：gen-class 类的静态初始化引用 `clojure.lang.RT`，`RT` 的静态初始化随即加载 `clojure.core`，只要
`clojure.jar` 在类路径上，Java 代码直接 `new` 就行；`clojure.java.api.Clojure.var(…)` 也会顺带启动运行时。

目标是 cleat 也做到：C# 项目引用 `.clj.dll` 与 cleat 的运行时程序集（连同镜像文件），**第一次碰到 cleat 生成的类型时运行时自己启动**。

## 2. 现状要点

- 宿主与嵌入走同一条启动路径（`Program.Start` / `CleatRuntime.InitBody`），镜像按 `AppContext.BaseDirectory` 与各级 `src/boot/` 找。
- `Cleat.Core` 不引用 `Cleat.Scheme` 与 `Cleat.Hosting`（依赖方向是 Hosting → Scheme → Core），所以生成的代码能直接调到的只有 `Cleat.Core`。
- 展开器与编译器递归深，要大栈：宿主主线程 512 MB；`CleatRuntime` 把全部调用排到一条 512 MB 栈的工作线程上串行执行。
- AOT 模块的常量放在模块的 `$Constants` 类型里，类型初始化器经 fasl 解码；模块里任何生成的代码第一次用常量时触发它。
- gen-class 类型的初始化器 intern 各 Var，`:load-impl-ns` 时 require 实现命名空间（cleat-043）。

## 3. 设计

### 3.1 触发点：模块常量表的类型初始化器

在发射器生成的 `$Constants..cctor` 开头插入一次调用 `Cleat.Core.Runtime.RuntimeBootstrap.EnsureStarted(typeof($Constants).Assembly)`。
这一处覆盖 AOT 模块里的全部生成类型（gen-class、deftype、defrecord、proxy、fn 类）：它们的代码第一次用到常量前，运行时已经起来；
fasl 解码本身也在其后。运行时已启动时代价是一次 volatile 读。gen-class 类型的初始化器另外也调一次（它在 intern Var 之前，
不一定先用到常量）。

### 3.2 `RuntimeBootstrap`（Cleat.Core）

- `EnsureStarted(Assembly caller)`：已启动或正在启动（宿主自己的启动过程、Clojure 镜像初始化期间 `Loader.BootImageInitializing`）
  时直接返回；否则加锁启动，只做一次。
- **启动器的发现**：Cleat.Core 不能直接调 Hosting，于是按名字 `Assembly.Load("Cleat.Hosting")`，反射调用
  `CleatRuntime.Init(options)`；宿主与 Hosting 启动时把自己登记为"已启动"，生成的代码在宿主里运行时不会二次启动。
  找不到 Hosting 时抛出说明清楚的异常（缺哪个程序集、镜像在哪里找过）。
- **选项**：镜像先在调用方程序集所在目录找，再按现有规则（`AppContext.BaseDirectory`、`CLEAT_SCHEME_BOOT` / `CLEAT_BOOT`）；
  调用方程序集所在目录加进加载路径（同目录的其他 `.clj.dll` 因此可 require）。也可由应用显式先调 `CleatRuntime.Init` 定制。

### 3.3 线程与栈

调用方线程（比如 ASP.NET 的请求线程）栈小，展开与编译不能在上面做。启动本身已经经 Hosting 的工作线程完成；需要处理的是启动之后、
仍在调用方线程上发生的加载：模块入口里的 `require`、gen-class 初始化器 require 实现命名空间时从源码编译。设计为加载器的一个钩子：
`RuntimeHooks.OnLoaderThread(Func<object>)`——Hosting 把它设为"排到工作线程执行并等待"，宿主设为直接执行（宿主的线程都有大栈）。
`Loader.TryLoad` 走源码（`LoadFile` / 编译）时经它；载入 dll 只跑入口，仍在调用方线程。

**风险**：类型初始化器在调用方线程上持锁时，工作线程若要初始化同一个类型会死锁（CLR 的 cctor 死锁检测只覆盖同一线程链）。
需要实测：gen-class 实现命名空间里引用该 gen-class 类型本身的常见写法、`:load-impl-ns` 与模块入口交错的情形；必要时把
"require 实现命名空间"推迟到类型初始化器之外（第一次调用方法时）。

### 3.4 部署形状

C# 项目要带上：`Cleat.Core.dll`、`Cleat.Scheme.dll`、`Cleat.Hosting.dll`、两个镜像、驱动库目录 `cleat.scheme.clojure/`、
应用自己的 `.clj.dll`（连同 `.ifc`）。由 tools.build 包（cleat-066）产出这一目录形状（相当于 JVM 的 uberjar），C# 项目以
`<Reference>` + `<None CopyToOutputDirectory>` 引用；之后可考虑打成 NuGet 包。

### 3.5 顺带：嵌入 API 的惰性初始化

`CleatRuntime.Var` / `Require` / `Eval` 在未初始化时自动以缺省选项 `Init`，相当于 `clojure.java.api.Clojure`。

## 4. 不做

- 不让非 AOT（源码加载、REPL 求值）产生的动态程序集被外部引用——它们本来就只活在进程里。
- 不改函数类的调用约定；从 C# 调普通 Clojure 函数仍经 `CleatRuntime.Var(…).invoke(…)`，gen-class 才是给 .NET 用的稳定表面。
- 不做 AOT 产物的裁剪（trimming）与 NativeAOT：互操作靠反射元数据，镜像需要运行时代码生成。

## 5. 验收

- 一个只引用上述程序集与镜像、**不调用 `CleatRuntime.Init`** 的 C# 控制台程序：`new my.ns.Greeter().Greet("x")`、gen-class
  的静态方法、`defrecord` 实例的字段与 `ILookup`、gen-class 的 `Main` 作为入口都能用，并发首次调用只启动一次。
- 宿主与嵌入的现有行为、启动时间、基准不退化；镜像不动点与全量测试通过；ILVerify 通过（含插入的调用）。
