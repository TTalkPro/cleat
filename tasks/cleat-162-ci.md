# cleat-162 GitHub Actions：PR 与 nightly 两层、种子守卫

- 里程碑：M19　依赖：无　状态：进行中
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
