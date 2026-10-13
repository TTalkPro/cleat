# cleat-070 错误 phase 对齐 JVM、cleat-run 的 SIGTERM、包的新版本

- 里程碑：后续　依赖：cleat-069　状态：进行中

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
