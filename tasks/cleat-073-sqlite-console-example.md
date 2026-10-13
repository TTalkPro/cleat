# cleat-073 控制台示例 SqlTable（Clojure 为主，依赖 SQLite NuGet）与 NuGet 包的原生库

- 里程碑：后续　依赖：cleat-072　状态：完成

## 背景

用户（2026-10-13）：在 examples 里建一个以 Clojure 为主的控制台应用，依赖 SQLite 的 NuGet 包，读一个文件加一行 SQL，输出表格。

试做时发现：`Microsoft.Data.Sqlite` 的托管程序集经 basis 都能找到，但它的原生库 `e_sqlite3`（`SQLitePCLRaw.lib.e_sqlite3` 的
`runtimes/<rid>/native/`）找不到——basis 只收集 `runtime` 资产，.NET 也只在框架目录与调用方程序集目录里找原生库。任何带原生库的 NuGet 包都会遇到。

## 工作内容

1. **basis 的原生资产**（tools.build `create-basis`）：`project.assets.json` 里包的 `runtimeTargets` 中 `assetType` 为 `native`、RID 适用于
   当前平台（如 `linux-x64`，再退到 `linux`、`unix`）的目录也进这个包的 `:paths`（加载路径）。
2. **加载器找原生库**（Cleat.Core `Loader`）：默认加载上下文的 `ResolvingUnmanagedDll` 在加载路径的目录里按平台的命名规则找
   （`libX.so` / `X.so`、`X.dll`、`libX.dylib`）。
3. **示例 `examples/SqlTable`**：`cleat.edn` 依赖 `Microsoft.Data.Sqlite`（nuget.org）；`dotnet cleat -M:run 文件 "SQL"`：`.csv` 导入内存数据库里以
   文件名命名的表（数值列按整数 / 小数存），`.db` / `.sqlite` 直接只读打开；结果画成表格。示例数据、clojure.test、`build.clj`（test、uber）、README。
4. 新版本：`Cleat.Runtime` / `Cleat.Cli` 1.0.3、`Cleat.ToolsBuild` 0.10.17、`Cleat.Neorepl` 0.1.2（jack-in 用 create-basis），验证后推 BaGetter。

## 结论（2026-10-13）

完成。

- **原生库**：`create-basis` 把包的 `runtimeTargets` 里适用于当前平台的 `native` 目录放进加载路径（RID 依次为运行时的 RID、`<os>-<arch>`、
  `<os>`、`unix`、`any`，取第一个有条目的）；`Loader` 在默认加载上下文的 `ResolvingUnmanagedDll` 里按平台命名在加载路径的目录里找。
  `uber` 照常把这些目录的文件复制进 `lib/`，脱离 cleat 的目录也能加载 `libe_sqlite3.so`。designs/18 §3 与 tools.build 的 README 记了这条。
- **示例 `examples/SqlTable`**：`csv`（RFC 4180）、`db`（CSV 导入内存库，列类型 INTEGER / REAL / TEXT，空字段 NULL；其他文件只读打开；
  无结果集的语句给影响行数）、`table`（画框，数字右对齐，中日韩字符占两格）、`main`（用法错误退出 2，SQL 错误退出 1）；clojure.test 4 个测试；
  `build.clj` 的 test / compile / uber / clean；`-M:run`、`-M:test`、`-M:neorepl`（jack-in 的服务里也能用 SQLite）；README。
- **版本**：`CoreInfo.Version` 1.0.3；`tools/build.cs pack` 的 `Cleat.Runtime` 也缺省用 `CoreInfo.Version`（原先取 csproj 的 `<Version>`，
  打出了 1.0.2）；`Cleat.Runtime` / `Cleat.Cli` 1.0.3、`Cleat.ToolsBuild` 0.10.17、`Cleat.Neorepl` 0.1.2（依赖 ToolsBuild 0.10.17，
  否则 jack-in 的服务缺原生库目录）。镜像没变，其余已发布的包照用。WebServer 示例改引用新版本。
- **发布**：先用同一批 nupkg 在本地目录源上验证两个示例（SqlTable：tool restore、`-M:run`、`-T:build test`、`uber` 后的 `run`、jack-in 里查询；
  WebServer：test、`dotnet build host`、`-M:cli`、`cli` 子命令），再推到 BaGetter，用新的全局包目录在仓库的示例目录实跑同样的步骤，都通过，不留进程。
- **测试**：`NuGetColdLoadTests.ConsoleExampleQueriesCsvWithSqlite`（`-M:run` 的表格、`-T:build test`、uber 目录里有原生库并能脱离 cleat 运行）；
  fixture 改为 1.0.3 / 0.10.17 / 0.1.2；全量 611/611。
