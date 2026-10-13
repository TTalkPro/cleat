# cleat-070 错误 phase 对齐 JVM、cleat-run 的 SIGTERM、包的新版本

- 里程碑：后续　依赖：cleat-069　状态：完成

## 背景

cleat-069 收尾时（2026-10-13）：

1. 用户要求删掉 BaGetter 上的 `Cleat.ToolsBuild 0.10.14` 重推（它不含推送之后补的两处修正）。BaGetter 配置的是
   `PackageDeletionBehavior=Unlist`、`AllowPackageOverwrites=false`，同版本不能重推；用户定换版本 0.10.15。
2. 实跑中发现：neorepl `eval` 一个编译错误时报 `No matching clause: :clojure.error/compile-syntax-check`。对照 JVM Clojure
   （`~/workspace/clojure`，`aef2e692`）：`Compiler.PHASE_*` 是不带命名空间的关键字（`:compile-syntax-check`、`:macroexpansion` …），
   `clojure.main/ex-triage` / `ex-str` 按它们分派；cleat 用 `ExpanderException` 替换 `CompilerException` 时（CLEAT-017）把值写成了
   `:clojure.error/compile-syntax-check` 等，宏展开写成了 `macroexpand`（JVM 是 `macroexpansion`），`core_deftype.clj` 的
   "fields must be symbols" 写成了 macro-syntax-check（JVM 的 4 参数 `CompilerException` 缺省 compile-syntax-check）。
3. `cleat-run -M` 被 SIGTERM 时子进程成了孤儿（`NuGetColdLoadTests` 的 jack-in 服务因此留在后台）。

用户准许修 `core.clj` 带来的不兼容（Clojure 镜像变了，已发布的包程序集对不上），并要求发新版本重推 BaGetter。

## 工作内容

1. `ExpanderException` 的 phase 常量不带命名空间（`macro-syntax-check`、`macroexpansion`、`compile-syntax-check`）；`core.clj` 的
   `throw-if` 与 `core_deftype.clj` 的字段检查用 `:compile-syntax-check`（与 JVM 相同）。
2. `cleat-run -M`：子进程继承标准流；收到 SIGTERM 转给子进程并等它退出。
3. 新版本：`Cleat.Runtime 1.0.1`（`CoreInfo.Version`，新包声明 `Cleat.Runtime >= 1.0.1`）、`Cleat.Nrepl 1.8.1`、`Cleat.Neorepl 0.1.1`、
   `Cleat.ToolsBuild 0.10.16`、`Cleat.ToolsNamespace 1.5.2`；示例改引用它们。先在本地目录源上验证同一批 nupkg，再推 BaGetter，再用 BaGetter 实跑示例。

## 结论（2026-10-13）

完成，分两次提交。

- **错误 phase**：对照 JVM Clojure 改正后，neorepl / nREPL 显示编译错误不再抛 "No matching clause"；全量 610/610。
  另记：`clojure.main/ex-str` 的输出比 JVM 多 `(ExpanderException)` 与重复的位置前缀（`ExpanderException` 没有像 `CompilerException`
  那样把原因包成 cause），只影响显示，留待以后。
- **BaGetter**：`Cleat.ToolsBuild 0.10.14` 已 unlist、推了 0.10.15；改 `core.clj` 之后 Clojure 镜像变了，又发了一批对着新镜像编译的新版本：
  `Cleat.Runtime 1.0.1`、`Cleat.ToolsNamespace 1.5.2`、`Cleat.ToolsBuild 0.10.16`、`Cleat.Nrepl 1.8.1`、`Cleat.Neorepl 0.1.1`
  （nrepl / neorepl 的版本号在上游版本上加修订号）。先在本地目录源上用同一批 nupkg 验证示例全流程，再推送；之后在 `examples/WebServer`
  用真实的 BaGetter 实跑：测试、`dotnet build host`、主机服务、neorepl 连进主机热替换、jack-in 都通过，basis 解析到的全是新版本。
  旧版本仍在 BaGetter 上（与旧运行时彼此一致，示例已改引用新版本）。
- **测试**：`NuGetColdLoadTests` 与 `PackageTests`（8 个）通过。
- **之后**：用户定把 `cleat-run` / `cleat-build` 脚本换成 dotnet tool（宿主即工具，见 cleat-071）。
