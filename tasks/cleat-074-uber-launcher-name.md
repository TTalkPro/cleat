# cleat-074 uber 的启动脚本以应用命名（`:launcher`，缺省为目录名）

- 里程碑：后续　依赖：cleat-073　状态：完成

## 背景

用户（2026-10-13）问 `./target/sqltable/run` 为什么叫 `run` 而不是 `sqltable`。上游的 `uber` 产出 uber jar（`java -jar app.jar`，jar 名就是
应用名），没有启动脚本；cleat 的移植把 jar 换成目录（.NET 没有可直接运行的单文件 jar），补了启动脚本，名字写死为 `run` / `run.cmd`。
放进 PATH 或有几个应用时这个名字没用。用户同意按建议改。

## 工作内容

1. tools.build `uber`：新参数 `:launcher`（启动脚本名），缺省为 `:uber-dir` 的目录名（`target/sqltable` → `sqltable` / `sqltable.cmd`）；
   名字与根目录的运行时文件（`cleat`、`cleat.dll` …，不分大小写）或 `lib` 同名、为空、含路径分隔符时报错，提示用 `:launcher`。
2. 测试：缺省名、`:launcher`、`:uber-dir` 末尾的斜杠、没有 `:main` 时不写、各种冲突。
3. 示例 SqlTable 的 `build.clj` 注释与 README、`NuGetColdLoadTests`、designs/18、tools.build 的 README。
4. 新版本：`Cleat.ToolsBuild` 0.10.18；`Cleat.Cli` 随附 tools.build，`CoreInfo.Version` 1.0.4（`Cleat.Runtime` 同号，内容不变）。
   `Cleat.Neorepl` 0.1.2 不变（依赖 ToolsBuild ≥ 0.10.17，用不到 `uber`）。

## 结论（2026-10-13）

完成。

- `uber` 的启动脚本名取 `:launcher`，缺省为 `:uber-dir` 的目录名（末尾的斜杠不影响）：SqlTable 是 `target/sqltable/sqltable` 与 `sqltable.cmd`。
  名字为空、含路径分隔符，或与根目录的运行时文件 / `lib` 同名（不分大小写；如 `target/cleat` 的缺省名 `cleat`）时，在复制任何文件之前报错。
- tools.build 的测试 39 个 127 条断言；`NuGetColdLoadTests.ConsoleExampleQueriesCsvWithSqlite` 改跑 `sqltable`；全量 611/611。
- 版本：`Cleat.ToolsBuild` 0.10.18，`Cleat.Runtime` / `Cleat.Cli` 1.0.4（Runtime 内容不变）；两个示例改引用 1.0.4。
  先在本地目录源上验证两个示例（SqlTable 的 test、uber 与新启动脚本、jack-in 里查 SQLite；WebServer 的 test、`dotnet build host`、`cli` 子命令），
  再推 BaGetter，用新的全局包目录在仓库的示例目录实跑同样的步骤，都通过。
