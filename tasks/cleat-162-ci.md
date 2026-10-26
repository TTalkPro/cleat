# cleat-162 GitHub Actions：PR 与 nightly 两层、种子守卫

- 里程碑：M19　依赖：无　状态：未开始
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
