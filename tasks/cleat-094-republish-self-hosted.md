# cleat-094 自寄宿后的新版本与 cleat.nanopass 文档

- 里程碑：M8（阶段 E）　依赖：cleat-093　状态：完成
- 设计依据：[19](../designs/19-clojure-self-hosting.md)、[cleat-080](cleat-080-republish-after-skeg-port.md)

## 目标
发布不含 Scheme 的运行时与工具，`cleat.nanopass` 作为用户可用的库有文档。

## 工作内容
1. 新版本：Cleat.Runtime / Cleat.Cli 1.1.0（运行时契约变了，次版本号升），ToolsNamespace、ToolsBuild、Nrepl、Neorepl 按依赖顺序重编重发；示例、README、designs/18、`NuGetColdLoadTests` 同步。
2. 照 cleat-080 的方法验证（本地目录源、隔离包缓存与 `DOTNET_CLI_HOME`、两个示例、jack-in），通过后推 BaGetter。
3. `cleat.nanopass` 的用户文档与示例（定义语言、写 pass、模板写法与上游的对照表）。

## 验收标准
- 两个示例对着 BaGetter 实跑通过；冷加载（C# / F# 引用包）不加载编译器程序集。

## 结论（2026-10-15）

完成。

| 包 | 旧 | 新 |
|---|---|---|
| Cleat.Runtime / Cleat.Cli（`CoreInfo.Version`） | 1.0.5 | 1.1.0（运行时契约变了：Scheme 元层删除，只剩 Cleat.Core / Cleat.Emit / cleat / Cleat.Hosting 四个程序集，runtime/ 里是镜像、编译器包与随附库） |
| Cleat.ToolsNamespace | 1.5.3 | 1.5.4 |
| Cleat.ToolsBuild（依赖 ToolsNamespace 1.5.4） | 0.10.19 | 0.10.20 |
| Cleat.Nrepl | 1.8.2 | 1.8.3 |
| Cleat.Neorepl（依赖 Nrepl 1.8.3、ToolsBuild 0.10.20） | 0.1.3 | 0.1.4 |

两个示例（WebServer、SqlTable）、各包 README、designs/18、`NuGetColdLoadTests` 改用新版本。

- 本地目录源验证（隔离的 `NUGET_PACKAGES` / `DOTNET_CLI_HOME`）：`tools/build.cs pack` 的 Runtime / Cli 与四个 Clojure 包
  （`-T:build install :local-feed`，按依赖顺序）；SqlTable 的 `-M:run`、`-M:test`（4 个测试）、`-T:build uber`；WebServer 的 `-T:build prep`、
  `-M:test`（4 个测试）、`-M:cli`、`-X:greet`、`dotnet build host`。`NuGetColdLoadTests` 与 `PackageTests` 9/9（C# / F# 冷加载、neorepl 从源 restore、
  示例的主机与 jack-in）。
- 同一批 nupkg 推到 BaGetter；用全新的包缓存在示例副本里对着 BaGetter 跑同样的步骤（`dotnet tool restore` 装 Cleat.Cli 1.1.0；SqlTable 运行与测试、
  WebServer 测试、cli、主机构建；`-Spath -M:neorepl` 解析到 Neorepl 0.1.4）。
- `cleat.nanopass` 用户文档：`src/clojure/cleat/nanopass/README.md`（定义语言、写 pass、模式与模板写法、与上游的对照表；第二层特性指向 cleat-095），
  README 与 designs/README 链接到它。
- 注意：`packages/*` 没有 nuget.config，`-T:build install` 用缺省源（`~/.cleat/local-feed` + nuget.org）restore 依赖，所以推送前要先把新的
  Cleat.Runtime.nupkg 放进 `~/.cleat/local-feed`（否则 tools.build 的 restore 找不到 Cleat.Runtime 1.1.0）。

