# cleat-066 tools.build 包（packages/tools.build）

- 里程碑：后续　依赖：cleat-062、cleat-063　状态：未开始
- 设计依据：上游 [tools.build](https://github.com/clojure/tools.build)（`~/workspace/tools.build`，提交 `aef6def`）。

## 背景

移植 neorepl 时（cleat-063）发现包的预编译必须按依赖顺序、每个命名空间一个进程地 `compile`（同一进程里连着编译时，后编的
会引用前面那个求值时生成的内存类型）。用户决定（2026-10-13）照 Clojure 官方的构建方案，把 tools.build 移植成一个包来做这件事，
不往 `tools/build.cs` 里加命令。

## 工作内容（初步，开工时与用户确认）

上游的 API（`clojure.tools.build.api`）分三类，按 cleat 的情形取舍：

1. **原样可移植**：`delete` `copy-file` `copy-dir` `write-file` `process` `git-process` `git-count-revs` `zip` `unzip`
   `resolve-path` `set-project-root!`——文件与进程操作换成 System.IO / System.Diagnostics。
2. **按 cleat 改写**：
   - `compile-clj`：上游用 tools.namespace 按 ns 依赖排序后在一个子进程里依次 `compile`；cleat 要每个命名空间一个子进程（理由见上），
     产物是源码旁边（或 `:class-dir`）的 `.clj.dll` / `.ifc` / `.inl`。排序用移植过来的 tools.namespace 的 find / parse / dependency 部分。
   - `create-basis`：上游经 tools.deps 解析 deps.edn 与 Maven；cleat 没有依赖解析，basis 来自项目的 `cleat.edn`（`:paths`，以及
     本地包目录这样的依赖），给出加载路径。
   - `java-command` → 运行 cleat 宿主的命令行（`cleat-command`）。
   - `jar` / `uber`：产出可部署的目录或 zip：应用的 `.clj.dll`、源码（可选）、加载路径上的依赖包；`uber` 另带 cleat 运行时程序集、
     两个镜像与驱动库（designs/17 §3.4 的部署形状，cleat-067 要用）。
3. **待定**：`javac`（对应 C#：`dotnet build` 一个 csproj？）、`write-pom` / `install`（Maven 本地仓库；对应 NuGet 包？还是不做）。

## 验收标准

- 移植的上游测试通过；`packages/nrepl`、`packages/neorepl` 能用它预编译（neorepl 的 CLI 启动从约 7 秒降到约 1.4 秒）；全量测试通过。
