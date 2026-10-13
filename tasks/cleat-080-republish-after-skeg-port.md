# cleat-080 移植 skeg 之后的新版本，重推 BaGetter

- 里程碑：后续　依赖：cleat-079　状态：完成

## 背景

用户（2026-10-14）：发新版本重推 BaGetter。cleat-075 ~ 079 改了 Scheme 与 Clojure 的 boot 镜像，包程序集按镜像绑定（cleat-070 的经验），
BaGetter 上的 Cleat.* 包都对不上现在的仓库；cleat-076 修的 neorepl 会话缓存并发读也还没发布。

## 工作内容

| 包 | 旧 | 新 |
|---|---|---|
| Cleat.Runtime / Cleat.Cli（`CoreInfo.Version`） | 1.0.4 | 1.0.5 |
| Cleat.ToolsNamespace | 1.5.2 | 1.5.3 |
| Cleat.ToolsBuild（依赖 ToolsNamespace 1.5.3） | 0.10.18 | 0.10.19 |
| Cleat.Nrepl | 1.8.1 | 1.8.2 |
| Cleat.Neorepl（依赖 Nrepl 1.8.2、ToolsBuild 0.10.19） | 0.1.2 | 0.1.3 |

两个示例（WebServer、SqlTable）、README、designs/18、`NuGetColdLoadTests` 改用新版本。

## 结论（2026-10-14）

完成。

- 先打进本地目录源（`tools/build.cs pack`，四个 Clojure 包按依赖顺序 `-T:build install`），隔离的包缓存与 `DOTNET_CLI_HOME` 下验证两个示例：
  SqlTable 的 test、uber 与启动脚本、jack-in 里查 SQLite；WebServer 的 test、`dotnet build host`、`cli` 子命令、`-M:cli`、`-X:greet`、
  主机服务与 neorepl 连进主机热替换处理函数。`NuGetColdLoadTests` 与 `PackageTests` 9/9。
- 同一批 nupkg 推到 BaGetter，用新的包缓存在仓库的示例目录对着 BaGetter 实跑同样的步骤，都通过，不留进程。
