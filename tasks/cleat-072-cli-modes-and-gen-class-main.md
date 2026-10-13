# cleat-072 Clojure CLI 的执行模式（-X、-T 别名、-Sdeps / -Spath / -P）与带 -main 的 gen-class 示例

- 里程碑：后续　依赖：cleat-071　状态：完成

## 背景

用户（2026-10-13）：

1. 问示例的 C# 为什么分成 `csharp/WebCore` 与 `host`，能否合并——回答：顺序是 C# → Clojure → C#（Clojure 对着 C# 的接口编译，主机对着
   Clojure 编出的类型编译），一个 C# 项目做不到；保持两个项目，README 写明原因。
2. 要一个带 `-main` 的 gen-class 例子。
3. 贴了 Clojure CLI 执行模式的说明（`-M` 跑 `-main`、`-X` 调函数传 map、`-T` 与项目类路径隔离的工具、`-A` 已弃用、`-Sdeps` / `-Spath` / `-P`）。
   对照 cleat-071：`-X` 没有；`-T:build` 被当作命名空间而不是 `cleat.edn` 的别名，也没有和项目依赖隔离；`-S…` / `-P` 没有。

## 工作内容

1. 宿主：`-X[:别名…] [ns/fn] :键 值…`（项目的 basis；别名的 `:exec-fn` / `:exec-args` / `:ns-default`；键可以是路径）；
   `-T:别名` / `-T别名`（工具别名的 `:deps` / `:paths`，缺省 `["."]`，不带项目的依赖；没有这个别名时名字就是命名空间——`-T:build`
   直接调 build.clj）；`-Sdeps EDN`、`-Spath`、`-P`；`-A` 报错提示。逻辑在 tools.build 的 `clojure.tools.build.main`（`exec-plan` / `exec!` /
   `deps-args`），宿主只解析选项、委托。
2. 开发布局下不登记随附的 tools.build 包程序集（`src/boot/tools/` 是 pack 的产物，会落后于源码）。
3. 示例：`src/web/cli.clj`（`(:gen-class :name Web.Cli :main true)`，`-main` 是小命令行，另有给 `-X` 的 `greet`）；别名 `:cli`、`:greet`；
   C# 主机的 `cli` 子命令调 `Web.Cli.Main`；测试与 README（第 5 节、为什么两个 C# 项目）。
4. `CoreInfo.Version` 1.0.2：`Cleat.Cli` / `Cleat.Runtime` 1.0.2 推到 BaGetter（镜像不变，已发布的 Clojure 包照用），示例改引用 1.0.2。

## 结论（2026-10-13）

完成。

- **执行模式**（designs/18 §5 有完整的表）：`-M` 照旧；新增 `-X[:别名…] [ns/fn] :键 值…`（项目的 basis，别名的 `:exec-fn` / `:exec-args` /
  `:ns-default`，键可以是路径）；`-T:别名` / `-T别名` 改为照 CLI 的语义——工具别名的 `:deps` / `:paths`（缺省 `["."]`），不带项目的依赖与路径，
  没有这个别名时名字就是命名空间（`-T:build` 照旧调 build.clj）；`-Sdeps`、`-Spath`、`-P`；`-A` 报错提示改用 `-M` / `-X`。
  tools.build 的测试加了 `exec-plan` 与宿主各模式的用例（`test_main`，3 个测试 18 条断言）。
- **开发布局**下不再登记随附的 tools.build 包程序集：它们是 `pack` 的产物，落后于源码时 `-X` / `exec!` 会调到旧代码（实际遇到过）。
- **示例**：`src/web/cli.clj`（gen-class `Web.Cli`，`:main true`），`dotnet cleat -M:cli …`、`dotnet cleat -M -m web.cli …`、C# 主机的
  `cli` 子命令（`Web.Cli.Main`）三种跑法；`-X:greet`（`:exec-fn` / `:exec-args`）；测试 `cli_test`；README 第 5 节，以及第 3 节说明 C# 为什么
  分成两个项目（C# → Clojure → C# 的编译顺序）。
- **版本**：`CoreInfo.Version` 1.0.2；`Cleat.Runtime 1.0.2` 与 `Cleat.Cli 1.0.2` 推到 BaGetter（镜像与 1.0.1 逐字节相同，已发布的 Clojure 包照用），
  示例改引用它们。推送前用同一批 nupkg 在本地目录源上验证示例全流程；推送后在 `examples/WebServer` 用真实的 BaGetter 实跑：`dotnet tool restore`、
  `-T:build test`、`dotnet build host`、`-M:cli`、`-X:greet`、`cli` 子命令、主机服务、neorepl 连进主机热替换都通过，不留进程。
- **测试**：示例的端到端测试加了 `-M:cli`、`-M -m`、C# 的 `Web.Cli.Main`、`-X:greet` 的断言；usage 的 golden 更新；全量 610/610。
