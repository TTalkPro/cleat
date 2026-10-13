# cleat-066 tools.build 包（packages/tools.build）

- 里程碑：后续　依赖：cleat-067　状态：未开始
- 设计依据：上游 [tools.build](https://github.com/clojure/tools.build)（`~/workspace/tools.build`，提交 `aef6def`）；
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
