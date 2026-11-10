# cleat-187 1.2.0：版本号、包清单与依赖审查

- 里程碑：M22　依赖：cleat-162（CI 在 GitHub 上跑绿）　状态：进行中（本地部分完成，待门禁与 162 的 CI 结果）

## 问题

M19 ~ M21 都没有发版：运行时 / 工具包仍是 1.1.0（`CoreInfo.Version`），而这三个里程碑换了编译器（C#）、加了 6 个新包（nanopass、spec.alpha、
test.check、tools.reader、tools.analyzer、core.async）、改了包程序集的格式（cleat-186 的 `CleatBootImageAttribute`）。包之间的依赖版本
（`packages/*/cleat.edn` 的 `:nuget/version`）是各自移植时写的，没有一起核对过。

## 工作内容

1. 版本号：`Cleat.Runtime` / `Cleat.Cli`（`CoreInfo.Version`）定为 1.2.0；各包的版本按"上游版本 + cleat 修订"的既有规则核对，
   有 cleat 补丁的（tools.analyzer、core.async、tools.reader 等）是否需要修订号。
2. 依赖审查：`packages/*/cleat.edn` 与 `build.clj` 里的包依赖、运行时版本下限（`:runtime-version`）一致；画出包依赖图写进 notes。
3. 1.1.0 → 1.2.0 的兼容说明：1.1.0 编的包程序集在 1.2.0 上会怎样（镜像 id 不同 → StaleModuleException / 不登记），写进 CHANGELOG 的升级说明。
4. CHANGELOG 的"未发布"改为 1.2.0（日期），README 的版本与安装说明同步。
5. 用 `tools/build.cs pack` 打出全部包，在空目录里从本地源冷装：C# / F# 引用运行时、`dotnet tool install Cleat.Cli`、各包 restore 后跑一遍上游测试。

## 验收

- 全部包能从本地源冷装并运行；依赖图与版本表进 notes；CHANGELOG / README 一致；全量门禁通过。

## 结果

**版本**：`CoreInfo.Version`、`Cleat.Runtime.Package` 的缺省版本 → 1.2.0（`Cleat.Cli` 跟 CoreInfo）。包按"上游版本 + 为新运行时重编时末位加一"（cleat-094 的规则）：
1.1.0 时发布过的四个重编为 nrepl 1.8.4、neorepl 0.1.5、tools.namespace 1.5.5、tools.build 0.10.21（依赖、示例、测试、README 里的引用一并改）；
M19 ~ M23 新增的八个没发布过，以上游版本首发。版本表、规则与依赖图写进 [notes/packages.md](../notes/packages.md)。

**依赖审查**：包之间的 `:nuget/version` 一致。发现运行时依赖只写了下限（nuspec 的 `version="1.1.0"` = ≥ 1.1.0）：1.1.0 编的包会被 NuGet 解析到更新的运行时，
加载时才 StaleModuleException。改为 **`Cleat.Runtime [x.y.z]` 精确版本**（tools.build 的 pack），冲突在 restore 时就报；包之间仍写下限（运行时的精确版本已保证一致）。
StaleModuleException 的消息加上运行时版本与"换为这个版本编的包"的提示。

**1.1.0 → 1.2.0 的兼容说明**：写进 CHANGELOG（"从 1.1.0 升级"：包要重编 / 换版本、精确版本依赖、M20 的删除项、对齐 JVM 的行为变化）；"未发布"改为 1.2.0（2026-11-10）。

**冷装**（`~/cold187.sh`，13；独立的本地源、NuGet 缓存与 dotnet CLI 目录，只连本地源）：
- `tools/build.cs pack` 打 Cleat.Runtime / Cleat.Cli 1.2.0；`dotnet tool install Cleat.Cli` 从本地源装上并运行。
- 12 个包各自 `cleat -T:build install` 进本地源（按依赖顺序，用装好的 Cleat.Cli）：全部成功。
- 每个包一个空项目（cleat.edn 只依赖这个包，从本地源 restore），用装好的 cleat 跑上游测试：**9 / 12 全过**（tools.namespace、nanopass、tools.reader、
  tools.analyzer、core.async、test.check、spec.alpha、data.generators、test.generative）。其余三个的失败都是测试对仓库布局的假设：
  tools.build 2 个（`git log` 要仓库、uber 期望缺 launcher 时报错而装好的工具带 launcher），nrepl 6 个、neorepl 8 个（起子进程服务器时用 `-I <包目录>/src`，
  只依赖包的项目里没有）——这些在仓库里的 PackageTests（源码与包程序集两遍）照常通过。

**冷装查出并修的**：
- **包里没有 Clojure 源码**：装好的 cleat 里 `(source when)` 找不到源码、nREPL 补全 / sanitize-meta 拿不到 `clojure/core.clj` 的文件。Cleat.Cli 与 Cleat.Runtime
  现在把 src/clojure 打进镜像旁的 `clojure-src/`，Startup 发现它时加上加载路径（JVM 的 clojure.jar 带源码）。
- **test.check 用 tools.build 编不出包**：上游的 `clojure_test/cljs.cljc` 声明的命名空间与路径不符（只给 ClojureScript），compile-clj 按名找不到；build.clj 给 `:ns-compile` 去掉它。
- **cleat-193 的 `:warn-on-boxed` 误报**：test.check 打包时满屏 "Boxed math warning"——cleat 的分析器看不到 let / loop 局部的原始类型（JVM 按初值看得到）；
  这类局部作实参时不报（无提示的 fn 形参、调用结果照报，test_clojure 的两个用例仍过）。

## 门禁（2026-11-10，13）

- Core 116 / Compiler 50 / Host 300 全过（Host 20 分 24 秒，含 NuGet 冷加载：运行时包按 CoreInfo.Version 精确依赖）
- image-set --twice --ilverify：全部通过
- conformance --gate：521 / 521，OK
- CTS / test_clojure：dispositions OK，基线 OK

剩下：162 在 GitHub Actions 上的结果（仓库私有，本机没有 gh，需用户确认）。
