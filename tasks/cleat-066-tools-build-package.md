# cleat-066 tools.build 包（packages/tools.build）

- 里程碑：后续　依赖：cleat-067　状态：完成
- 设计依据：[designs/18](../designs/18-tools-build.md)；上游 [tools.build](https://github.com/clojure/tools.build)（`~/workspace/tools.build`，提交 `aef6def`）；
  [designs/17](../designs/17-lazy-runtime-startup.md) 的包程序集与 NuGet 形状。

## 背景

移植 neorepl 时（cleat-063）发现包的预编译需要专门的构建步骤。用户决定（2026-10-13）照 Clojure 官方的构建方案，把 tools.build
移植成一个包来做，不往 `tools/build.cs` 里加命令；依赖与发布用 NuGet（兼容 BaGetter 等自建仓库），并能用 `dotnet build` 直接构建
C# / F# 项目。包程序集与运行时 NuGet 包由 cleat-067 先做好，本任务在其上提供构建 API。

## 工作内容

上游的 API（`clojure.tools.build.api`）按 cleat 的情形取舍：

1. **原样移植**：`delete` `copy-file` `copy-dir` `write-file` `process` `git-process` `git-count-revs` `zip` `unzip`
   `resolve-path` `set-project-root!`——文件与进程操作换成 System.IO / System.Diagnostics。
2. **按 cleat 改写**：
   - `create-basis`：项目的 `cleat.edn`（`:paths`、`:deps`）；`:deps` 里的 NuGet 依赖（如 `{Nrepl {:nuget/version "1.8.0"}}`）经
     NuGet 解析与下载（生成临时项目交给 `dotnet restore`，源与凭据按 `nuget.config`，因此兼容 BaGetter 与私有源），本地包目录依赖照用；
     basis 给出加载路径与要登记的包程序集。
   - `compile-clj`：编出包程序集（cleat-067 的编译驱动），命名空间顺序用移植过来的 tools.namespace 的 find / parse / dependency 部分。
   - `java-command` → `cleat-command`：运行 cleat 宿主的命令行。
   - `javac` → **`dotnet-build`**：对 C# / F# 项目（`.csproj` / `.fsproj`）运行 `dotnet build`，产物进 basis（Clojure 代码可以调用）。
   - `jar` → **`pack`**：把包程序集（与可选的源码）打成 nupkg，依赖写 `Cleat.Runtime` 与 `:deps` 里的 NuGet 包（相当于 `write-pom` + `jar`）。
   - `install` → 推到本地 NuGet 源目录；新增 **`push`**：`dotnet nuget push` 到 nuget.org 或 BaGetter（源与 API key 按参数 / `nuget.config`）。
   - `uber`：可部署目录（应用的包程序集、依赖包、运行时程序集与镜像），对应 designs/17 §3 的形状。
3. **测试**：移植上游测试；nrepl、neorepl 用它编包程序集、打 nupkg；一个 C# 与一个 F# 项目经本地源 restore 这些包并冷加载。

## 验收标准

- 移植的上游测试通过；`packages/nrepl`、`packages/neorepl` 用它构建与打包；推送到本地 BaGetter（或本地目录源）后能被 restore；全量测试通过。

## 结论（2026-10-13）

完成。设计与实现记录见 [designs/18](../designs/18-tools-build.md)（§9 是实现中的取舍）。

- **packages/tools.build**：上游 API 按 designs/18 §4 移植——文件、zip、进程、git 任务同名同参数（路径是字符串）；`create-basis` 读
  `cleat.edn`，`:nuget/version` 依赖由 `dotnet restore` 解析（nuget.config 照常生效，兼容 BaGetter；本地源作附加源），`:local/root`
  是本地 cleat 项目；`cleat-command`、`compile-clj`（编包程序集，`:bindings` / `:filter-nses` 照旧）、`dotnet-build`、`pack`（直接写 nupkg）、
  `install`（本地源 `~/.cleat/local-feed`）、`push`（`dotnet nuget push`）、`uber`（可部署目录与启动脚本）。构建脚本由
  `clojure.tools.build.main` / `bin/cleat-build` 运行（相当于 `clj -T:build`）。上游测试移植 37 个测试 106 条断言（jar 改目录、Java 改 C#，
  反射捕获不移植），NuGet 部分用存根运行时包与临时全局包目录，不连网。
- **packages/tools.namespace**：`dependency` `track` `parse` `file` `find`（只有目录）移植，上游测试 24 个 55 条断言。
- **nrepl / neorepl**：各加 `cleat.edn` 与 `build.clj`；neorepl 经 NuGet 依赖 Nrepl。`NuGetColdLoadTests` 里用真的 `Cleat.Runtime`
  （tools/build.cs pack）把两包编译、打包、装进本地源，只依赖 Neorepl 的项目 restore 到 Neorepl 与 Nrepl（`Cleat.Runtime` 不进加载路径）并运行
  neorepl；C# / F# 冷加载的测试包也改由 tools.build 打。
- **cleat 本身的改动**：加载器按类型名在加载路径目录的 `*.dll` 里找公有类型（`Loader.FindTypeOnLoadPath`，`RT.classForName` 的最后一站），
  `dotnet build` 的产物与 NuGet 里的 C# 库放进 `-I` 即可在 Clojure 里直接用；宿主公开运行着的镜像与驱动库路径（uber 复制运行时用）；
  编译驱动的包根可以是文件；`.gitignore` 放行 `packages/*/bin/`（cleat-063 的 neorepl 启动脚本当时被 `[Bb]in/` 规则漏掉，随本任务提交）。全量 610/610。
- **没做**：`write-pom` 与 Maven / git 依赖；neorepl jack-in 按 basis 解析用户项目的 `:deps`；别名式启动 `cleat -X:alias`。
