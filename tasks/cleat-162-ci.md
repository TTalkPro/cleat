# cleat-162 GitHub Actions：PR 与 nightly 两层、确定性守卫（原种子守卫）

- 里程碑：M19　依赖：无　状态：进行中（2026-11-05：本地验收完成，剩 push 后在 GitHub Actions 上的实测）
- 设计依据：[29 §1 第 4 条 / §4](../designs/29-usability-and-engineering.md)

## 问题

仓库没有 CI（没有 `.github/workflows`）。Host 套件约 20 分钟，门禁全靠手工跑；
"改了 `src/clojure` 却忘了重生成种子"只能等下次全量测试才发现。

## 工作内容

1. `.github/workflows/pr.yml`：push / PR 触发；`actions/checkout` 用 `submodules: recursive`；安装 .NET 10 SDK；
   `dotnet build -c Release`；`dotnet test` 跑 Core 全量与 Host 的 `Category("Host")`、`Category("Packages")`。目标 ≤ 15 min。
2. **种子守卫**（PR 层）：diff 中有 `src/clojure/**` 改动而 `src/boot/seed/*.gz` 没变时失败，提示运行 `clojure-seed`。
3. `.github/workflows/nightly.yml`：定时 + `workflow_dispatch`；`Category("Bootstrap")`（自举不动点、种子逐字节）、
   `Category("Conformance")` 与 `conformance --run --gate`、`cts cts`、`cts test-clojure`、ilverify。
4. 失败时上传 conformance 报告、CTS 差异、TestResults 为 artifact；NuGet 缓存按 `Directory.Build.props` 与 csproj 哈希。
5. 性能不进 CI（共享 runner 的噪声远超 151 的地板）。

## 验收

- 在一个故意漏掉种子的分支上，种子守卫失败；正常分支两层都是绿的。
- PR 层实测时长写进任务书；超过 15 min 时列出最慢的测试与拆分方案。
- README 增加 CI 徽章与"本地复现 CI"的命令。

## 进展（2026-11-02）

- `.github/workflows/pr.yml`：push(main) / PR 触发，`checkout submodules: recursive`（fetch-depth: 0 供守卫
  diff）、.NET 10 SDK、NuGet 缓存（`Directory.Build.props` / `Directory.Solution.props` / `**/*.csproj` 哈希）、
  Release 构建、Core 全量、Host 按 `Category=Host|Category=Packages` 过滤；种子守卫按
  `git diff --name-only origin/<base>...HEAD` 判定，`src/clojure/**` 变了而 `src/boot/seed/*.gz` 没变即失败
  并在错误信息里给出 `clojure-seed` 命令；失败时上传 TestResults trx。
- `.github/workflows/nightly.yml`：定时（UTC 20:20）+ `workflow_dispatch`；`Category=Bootstrap|Category=Conformance`
  （自举不动点 / 种子逐字节 / conformance 门禁）、`conformance --run --gate --report`、`cts cts`、
  `cts test-clojure`、`dotnet-ilverify` 校验 `src/boot/cleat.boot.dll` 与 `cleat.compiler.dll`；失败时上传
  TestResults 与 conformance 报告。性能不进 CI（29 §9）。
- **种子守卫本地模拟通过**：临时分支只改 `src/clojure` 时守卫 FAIL，同补 `src/boot/seed/*.gz` 时 PASS；
  模拟后已删分支，种子文件逐字节恢复。
- **PR 层本机实测**（3d1a497，热缓存）：build 2:49 + Core 128 用例 5 s + Host 过滤 258 用例
  **21:43，合计 ≈ 24.7 min，超出 ≤ 15 min 目标**。Host 侧的慢项集中在少数工程级集成测试
  （158 全量 trx：BothFromPackageAssemblies ~4.5 min、Nrepl ~3.5 min、ExampleProject ~3.2 min、
  Neorepl ~3.0 min、PublishedDirectory ~2.6 min——合计 ~17 min 占过滤套件的八成）。
  **拆分方案（待用户裁决）**：把这五项挪进独立的 `Category("Engineering")` 移到 nightly（PR 层回到 ~8 min），
  或 PR 层拆两个并行 job；不删测试本身。
  注意共享 runner（2 vCPU）通常比本机慢，push 后要以 GH Actions 实际时长复核。
- README 加了两层徽章、"CI 与本地复现"表与逐条命令。
- 待收：push 后确认两层绿、PR 种子守卫在 GH 上实测失败、GH 实际时长写回。

## 182 之后的收尾（2026-11-05）

**背景**：cleat-182 删除了种子与自举（编译器改 C#，designs/30）。种子守卫随之换成确定性守卫（`DeterminismTests`，`Category("Determinism")`：
从零两步重编镜像与构建产物逐字节相同、src/boot 是不动点）；PR 层的构建也不再从种子发射 stage1，C# 编译器编镜像只要十几秒。

1. **分类改名 `Bootstrap` → `Engineering`**：182 之后这个分类里只剩发布（`PublishedDirectoryRunsStandalone`）与 NuGet 冷加载 /
   示例工程（`NuGetColdLoadTests`），与自举无关。**修了 182 的一个疏漏**：182 把 nightly 的过滤改成 `Determinism|Conformance`，
   这些测试因此谁也不跑了；现在 nightly 是 `Determinism|Engineering|Conformance`。
2. **PR 层拆成两个并行 job**（matrix，`fail-fast: false`，各自构建，不删任何测试）：
   - `core-host`：Core / Compiler 全量、`Category=Determinism|Category=Host`；
   - `packages`：`Category=Packages`（packages/ 下各包自带的套件，单项最慢的一组）。
   测试步骤加 `--no-build`（原来每步 `dotnet test` 都要再走一遍增量构建）；失败产物按 job 分名上传，并加上 Compiler 的 TestResults。
3. **PR 层实测**（本机 Debian-VM01 8 核，热 NuGet 缓存，两个 job 先后跑以各自计时）：

   | job | 构建 | 测试 | 合计 |
   |---|---|---|---|
   | core-host | 21 s | 252 s（Core 114、Compiler 49、Host 251） | **≈ 4.6 min** |
   | packages | 21 s | 329 s（6 项：BothFromPackageAssemblies 120 s、Nrepl 80 s、Nanopass 51 s、Neorepl 48 s、ToolsBuild 28 s …） | **≈ 5.8 min** |

   PR 墙钟 ≈ 5.8 min（原单 job 24.7 min：种子发射 + Clojure 编译器编镜像的构建 2:49，Clojure 编译器下的 Host 过滤套件 21:43）。
   共享 runner（2 vCPU）预计慢 1.5 ~ 2 倍，仍在 15 min 内；以 push 后的实际时长为准。
4. **确定性守卫的失败路径**（本地模拟）：把 `src/boot/cleat.boot.inl` 翻转一个字节，`BuildFromScratchReproducesTheBuildImage` 与
   `BuildImageIsAFixedPoint` 都失败；恢复原文件（SHA256 `14a67709…`）后 4 个确定性测试全过。
5. 文档：README 的 CI 表与本地复现命令、designs/29 §4 的两层表与守卫说明改到现状。
6. 顺带（用户在 REPL 里报的）：Tab 补全不认别名——`(require '[clojure.string :as cstr])` 之后 `cstr/` 补不出东西。`ReplServices.Complete`
   的限定部分现在先按当前命名空间的别名决议、候选保留用户写的前缀，只列目标命名空间自己的公有 Var（原来连 refer 进来的 clojure.core 函数与私有 Var 都列）；
   无限定前缀也补别名（`cst` → `cstr/`，补出别名时不加空格）。参数提示同样认别名，并修了 `:arglists` 只认向量的老缺陷（defn 写的是列表，
   所以 defn 的函数从来没有提示）。测试 `HostProcessTests.ReplCompletionResolvesAliases`（经新加的 `Program.ReplCompletions` / `ReplHint`）；
   pty 实测 `(cst⇥` → `(cstr/`、`up⇥` → `(cstr/upper-case`，提示 `[s]`。

**剩下的**（需要 push）：两层在 GitHub Actions 上转绿、实际时长写回本节；nightly 手动触发一次（`workflow_dispatch`）确认 Engineering 在 runner 上能跑
（NuGet 冷加载要 `dotnet pack` 与本地目录源）。
