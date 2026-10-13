# cleat-071 cleat 宿主作为 dotnet tool（Cleat.Cli），内置 -M / -T

- 里程碑：后续　依赖：cleat-066、cleat-069、cleat-070　状态：完成
- 设计依据：[designs/18](../designs/18-tools-build.md) §5、§10。

## 背景

用户指出（2026-10-13）：`cleat-run` / `cleat-build` 是几段 sh / cmd 脚本，靠仓库里的相对路径找 tools.build 的源码，通用功能应该是
.NET 的工具。用户选定"宿主即工具"：cleat 宿主打成 dotnet tool 包 `Cleat.Cli`（命令 `cleat`），把 Clojure CLI 的两个入口做进宿主，删掉脚本。

## 设计

- **`cleat -M:别名… 参数…`**（`clj -M`）：宿主经 tools.build 的 `create-basis`（别名取自命令行）算出加载路径，**在同一进程里**加进
  加载路径，再按别名的 `:cleat-opts` + `:main-opts` + 其余参数重新解析命令行运行（如 `-m neorepl.main …`）。不再起子进程。
- **`cleat -T:命名空间 函数 :键 值…`**（`clj -T`）：项目根目录（当前目录）进加载路径，调 `命名空间/函数`（`-T:build pack` 即 build.clj 的
  `build/pack`），键值对按 EDN 读成参数 map——即原来的 `cleat-build`。
- **tools.build 从哪来**：工具包里随附 `Cleat.ToolsBuild` / `Cleat.ToolsNamespace` 的包程序集（Clojure 镜像旁的 `tools/` 目录，比镜像旧时不用，
  与随附库相同）；开发布局下用仓库的 `packages/tools.build/src`、`packages/tools.namespace/src`。都没有时报错说明。
- **工具包**：`tools/build.cs pack` 同时打 `Cleat.Runtime` 与 `Cleat.Cli`（`PackAsTool`，命令 `cleat`；镜像、驱动库、随附库、tools.build
  在包里与宿主同目录）。安装：`dotnet tool install -g Cleat.Cli`，或项目里 `.config/dotnet-tools.json` 钉版本后 `dotnet tool restore`、
  `dotnet cleat …`。源与 BaGetter 照 `nuget.config`。
- **示例**：`.config/dotnet-tools.json` 引用 `Cleat.Cli`；README 改用 `dotnet cleat -T:build …` / `dotnet cleat -M:neorepl`；主机的 MSBuild 目标
  调 `$(CleatCommand) -T:build compile …`（缺省 `dotnet cleat`）。
- 删掉 `packages/tools.build/bin/cleat-run*`、`cleat-build*`，文档与测试改用 `cleat -T` / `-M`。

## 验收标准

- 从本地目录源（同 BaGetter）安装 `Cleat.Cli` 为本地工具，示例全流程（`-T:build test`、`dotnet build host`、`-M:neorepl` 连主机、jack-in）可用；
  推到 BaGetter 后实跑同样可用；全量测试通过。

## 结论（2026-10-13）

完成。

- **宿主**：`cleat -M[:别名…] 参数…` 经 tools.build 的 `deps-args`（`create-basis`）算出加载路径，加进本进程，再按别名的
  `:cleat-opts` / `:main-opts` + 其余参数重新解析命令行运行；`cleat -T:ns 函数 :键 值…` 当前目录进加载路径，调 `ns/函数`。
  tools.build 用镜像旁 `tools/` 里随附的包程序集（比镜像旧时不用），或开发布局下仓库的源码，都没有时报错说明。
- **工具包**：`tools/build.cs pack` 先把 tools.namespace、tools.build 编进 `src/boot/tools/`，再打 `Cleat.Runtime` 与 `Cleat.Cli`
  （`PackAsTool`，命令 `cleat`，镜像、驱动库、随附库、tools.build 与宿主同目录）。打包用 `-p:PackageVersion`：全局的 `-p:Version`
  会传给被引用的项目，把 `Cleat.Core` 等的程序集版本改成 1.0.1.0，用它们编出的包程序集就要求 1.0.1.0，和运行时包里的 1.0.0.0 对不上
  （实跑时主机报 `Could not load … Cleat.Core, Version=1.0.1.0`）。
- **示例**：`.config/dotnet-tools.json` 钉 `Cleat.Cli 1.0.1`；README 改为 `dotnet tool restore`、`dotnet cleat -T:build …`、`dotnet cleat -M:neorepl`；
  主机的 MSBuild 目标调 `$(CleatCommand) -T:build compile …`（缺省 `dotnet cleat`，示例在 cleat 仓库里时用仓库构建出的 cleat）。
- **删掉** `packages/tools.build/bin/cleat-run*`、`cleat-build*`；包的 `build.clj` / `cleat.edn` 注释、tools.build 与 neorepl 的 README、designs/18 §5
  改用 `cleat -T` / `-M`。
- **BaGetter**：`Cleat.Cli 1.0.1` 已推（镜像与 BaGetter 上的 `Cleat.Runtime 1.0.1` 逐字节相同）。在 `examples/WebServer` 用真实的 BaGetter 实跑：
  `dotnet tool restore`、`dotnet cleat -T:build test`、`dotnet build host`（经 `dotnet cleat` 编 Clojure）、主机服务、`dotnet cleat -M:neorepl`
  连进主机热替换、jack-in 都通过，对 `dotnet cleat` 发 SIGTERM 后不留进程。
- **测试**：`NuGetColdLoadTests` 的包构建改用 `cleat -T:build install`，示例测试改为 `dotnet tool restore` 后全程 `dotnet cleat`。
- 全量：失败的只有两个错误输出 golden（usage 多了 -M / -T 两行），更新后通过——其余 608 个通过。
- **另记**：工具包里带了几个用不上的文件（`primgen`、`stage0` 的启动器等，来自项目引用），不影响使用，以后可以精简。
