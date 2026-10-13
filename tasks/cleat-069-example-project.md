# cleat-069 完整的示例项目（examples/WebServer）

- 里程碑：后续　依赖：cleat-066、cleat-067、cleat-068　状态：完成
- 设计依据：[designs/17](../designs/17-lazy-runtime-startup.md)、[designs/18](../designs/18-tools-build.md)。

## 背景

用户要求（2026-10-13）：把 examples 里的项目改成一个完善的项目，重点展示
1. 用 **BaGetter**（自建 NuGet 仓库）获取依赖；
2. 用 **neorepl** 交互地启动与开发；
3. 用 **`dotnet build`** 混合 C# 代码；
4. **gen-class** 生成 .NET 直接可用的类型，供 C# 端使用。

用户同意把 cleat 的包推到本机的 BaGetter（`localhost:5000`，key 在 `~/apps/bagetter/.env`）并实跑一遍；仓库里的自动测试用本地目录源代替 BaGetter（同一机制，不连网）。

## 设计

示例 `examples/WebServer` 是一个独立的 cleat 项目（不在 Cleat.slnx 里，不继承仓库的 Directory.Build.*）：

| 部分 | 作用 |
|---|---|
| `nuget.config` | BaGetter（`http://localhost:5000/v3/index.json`）与 nuget.org |
| `cleat.edn` | `:paths ["src" "target/dotnet"]`；`:deps` 里的 `Cleat.Nrepl`（运行时内嵌 nREPL）从 BaGetter 取；别名 `:neorepl`（`Cleat.Neorepl`，交互开发）、`:test` |
| `csharp/WebCore` | C# 库：接口 `IHandlers`、工具类 `Slug`；Clojure 直接用它的类型 |
| `src/web/handlers.clj` | gen-class `Web.Handlers` 实现 `WebCore.IHandlers`；处理函数经 Var 间接，REPL 里重定义立即生效 |
| `build.clj` | tools.build：`prep`（dotnet-build WebCore）、`compile`（compile-clj → `target/classes/Web.Handlers.dll`）、`test`、`pack`、`push`、`uber` |
| `host/WebServer.csproj` | ASP.NET 主机：从 BaGetter 引用 `Cleat.Runtime`、`Cleat.Nrepl`；`dotnet build` 时经 MSBuild 目标调 cleat 编 Clojure，引用编出的程序集，直接 `new Web.Handlers()`（惰性启动，不调初始化 API）；`WEB_NREPL_PORT` 设置时进程内起 nREPL，neorepl 连上去热替换 |
| `test/` | clojure.test |

为此补的基础设施：

- **`cleat-run -M:别名`**（tools.build 的 `clojure.tools.build.main`）：相当于 `clj -M:alias`——按别名建 basis，运行 `cleat -I … <:main-opts> 参数…`（终端照常继承）。
- **neorepl 的 jack-in 用 basis**：项目有 `cleat.edn` 时经 tools.build 的 `create-basis`（别名取 `cleat.edn` 的 `:neorepl/aliases`）算服务的加载路径——项目的 NuGet 依赖、C# 构建输出都在上面；nREPL 自己的位置照旧。
- **tools.build、tools.namespace 打成 NuGet 包** `Cleat.ToolsBuild`、`Cleat.ToolsNamespace`；`Cleat.Neorepl` 依赖 `Cleat.ToolsBuild`（jack-in 用）。

## 验收标准

- 推到本机 BaGetter 后，示例按 README 从 BaGetter restore、`dotnet build`、运行、`cleat-run -M:neorepl` 交互、连上运行中的主机热替换都可用。
- 自动测试（本地目录源）：示例的主机构建与 HTTP 行为、jack-in 的服务里能用 C# 类型与项目代码；全量测试通过。

## 结论（2026-10-13）

完成。示例 `examples/WebServer` 按 README 用本机 BaGetter 实跑通过：

- **推到 BaGetter 的包**（`localhost:5000`）：`Cleat.Runtime 1.0.0`、`Cleat.ToolsNamespace 1.5.1`、`Cleat.ToolsBuild 0.10.14`、
  `Cleat.Nrepl 1.8.0`、`Cleat.Neorepl 0.1.0`。先在本地目录源上用同一批 nupkg 把示例完整验证一遍再推（BaGetter 上的版本不可覆盖）。
- **实跑**：`cleat-build test`（2 个测试 8 条断言）；`dotnet build host` 一条命令编 WebCore、Clojure 包程序集 `Web.Handlers.dll`、C# 主机；
  主机的四个接口由 gen-class 的 `Web.Handlers` 服务（返回 C# 记录 `Greeting`，调 C# 的 `Slug`），没有初始化调用；
  `cleat-run -M:neorepl eval -p <端口>` 连进主机进程内的 nREPL 重定义 `greeting-text`，主机立即返回新内容；
  `cleat-run -M:neorepl server` 的 jack-in 服务加载路径是 `src`、`target/dotnet`、`test` 与 `Cleat.Nrepl`。
- **自动测试**：`NuGetColdLoadTests.ExampleProjectBuildsServesAndDevelopsInteractively`（本地目录源代替 BaGetter）覆盖以上全部；
  `Cleat.Examples.Tests` 与旧示例一起从解决方案里去掉。fixture 的回退包目录改为过滤过的用户缓存（不含 `cleat.*`），
  用户缓存里从 BaGetter 装过的包不会盖过 fixture 构建的。
- **实跑中发现并修的缺陷**：
  - 运行时扫描应用依赖时按 `Cleat.` 前缀跳过程序集，改名后的 `Cleat.Nrepl` 在 C# 主机里找不到（cleat-067 的 `PackageIndex`）——只跳过运行时自己的程序集；
  - basis 只用 `packageFolders` 的第一个目录，从回退目录解析到的包路径错误（tools.build 的 `create-basis`）；
  - NuGet 只按标了 `allowInsecureConnections` 的源名字推到 http:// 源：`push` 加 `:configfile`，示例按名字 `bagetter` 推；
  - cleat 宿主是不变全球化模式，`string.Normalize` 不分解：示例的 `Slug` 不依赖规范化，REPL 与主机结果一致。
- **已推到 BaGetter 的 `Cleat.ToolsBuild 0.10.14` 不含后两项修正**（`:configfile`、回退目录）：它们在推送之后才改。
  示例与 README 的流程不受影响（构建脚本用仓库里的 tools.build 源码；没有回退目录时不触发）；要让 BaGetter 上的包也带上，需要删掉重推或换版本号。
- 全量 610/610（`Cleat.Examples.Tests` 去掉，示例的验收并入 `Cleat.Host.Tests`）。
