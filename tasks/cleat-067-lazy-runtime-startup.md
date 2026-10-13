# cleat-067 运行时惰性启动：.NET 直接使用 cleat 生成的类型

- 里程碑：后续　依赖：cleat-043、cleat-066　状态：未开始
- 设计依据：[designs/17](../designs/17-lazy-runtime-startup.md)。

## 背景

讨论（2026-10-13）：`.clj.dll` 是标准程序集，gen-class 等是真实 CLR 类型，但 .NET 程序不先显式启动 cleat 运行时就不能用它们；
JVM 上 gen-class 类的静态初始化会经 `clojure.lang.RT` 自动拉起运行时。用户决定单独立任务改造生成类型的初始化，让 CleatRuntime
惰性启动；设计写进 designs，排在 tools.build（cleat-066）与 REPL 相关开发之后。

## 工作内容

1. `Cleat.Core.Runtime.RuntimeBootstrap.EnsureStarted`：只启动一次；宿主 / Hosting 启动与镜像初始化期间不重入；按名字找
   `Cleat.Hosting` 并调 `CleatRuntime.Init`；镜像先在调用方程序集目录找，该目录加进加载路径。
2. 发射器：AOT 模块的 `$Constants..cctor` 与 gen-class 类型初始化器开头调用它（designs/17 §3.1）。
3. 加载器钩子 `RuntimeHooks.OnLoaderThread`：Hosting 把源码加载 / 编译排到大栈工作线程；宿主直接执行（§3.3），并实测 cctor 死锁的情形。
4. `CleatRuntime.Var` / `Require` / `Eval` 未初始化时自动 `Init`（§3.5）。
5. 部署形状：用 tools.build 包产出带运行时程序集、镜像、驱动库与应用 `.clj.dll` 的目录（§3.4）。
6. 测试：一个不调用 `Init` 的 C# 项目使用 gen-class（实例方法、静态方法、`Main`）与 defrecord；并发首次调用只启动一次。

## 验收标准

- designs/17 §5 全部满足；全量测试与镜像不动点通过；启动时间与基准不退化。
