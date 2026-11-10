# cleat-188 1.2.0：发布产物与发布清单

- 里程碑：M22　依赖：cleat-187　状态：产物与清单就绪（2026-11-10），待用户推送与 CI 结果

## 问题

发版要对外推送（NuGet 源、git tag），属于用户执行的动作；这里准备好可复现的产物与逐步清单。

## 工作内容

1. 发布清单（`notes/release-checklist.md`）：门禁（全量 + nightly 绿）、版本核对、打包、产物校验（sha256、包内文件列表、镜像 id 一致）、
   推送目标（BaGetter / nuget.org，由用户定）、tag 与 GitHub Release 文案、回退办法。
2. 产物：`artifacts/release-1.2.0/` 下全部 nupkg、自包含发布（`publish`，各 RID 按需）与校验文件；发布目录里镜像与随附库的镜像 id 一致（cleat-186）。
3. 冒烟：在干净环境（新用户目录、无 `~/.cleat/local-feed`）从产物安装并跑 README 的入门步骤。
4. 推送与 tag 由用户执行；执行后把结果记进本任务。

## 验收

- 清单与产物齐备，冒烟通过；用户确认推送后关闭。

## 结果（2026-11-10）

- **清单**：[notes/release-checklist.md](../notes/release-checklist.md)——门禁（本地全量 + PR / nightly 绿 + r7rs）、版本核对、打包、产物校验、冒烟、推送顺序、tag 与
  GitHub Release 文案、回退。
- **`publish` 补齐**（tools/build.cs）：发布目录与 Cleat.Cli 工具包同布局——加上随附的 `tools/`（tools.build / tools.namespace，`-T:build` 用）与
  `clojure-src/`（cleat-187 的源码）；`tools/` 随发布对着当前镜像重编（原来只重编随附库）。
- **产物**（`~/release188.sh` 一步做出，`artifacts/release-1.2.0/`，不进仓库）：
  - `nuget/`：Cleat.Runtime、Cleat.Cli 1.2.0 与 12 个包（共 14 个，5.0 MB），nuspec 对运行时是精确版本 `[1.2.0]`；
  - `publish/`：自包含发布 linux-x64（37 MB）、win-x64（38 MB）、osx-arm64（34 MB）；
  - `SHA256SUMS`：17 个文件，`sha256sum -c` 全部 OK。
- **镜像 id 一致**：linux-x64 发布目录、Cleat.Cli 与 Cleat.Runtime 包里，`cleat.boot.dll` 与 `Cleat.Clojure.Libs.dll`、`tools/*.dll` 记的镜像 id 都是
  `4fae6203-39ee-7cf1-1138-2a16b4cf109a`（三行 OK）。
- **冒烟**（新 HOME、新 NuGet 缓存与 dotnet CLI 目录，没有 `~/.cleat/local-feed`）：`dotnet tool install -g Cleat.Cli` 从产物目录装上；`cleat -e` → 3；
  脚本带参数；REPL 求值并 `(source when)` 打出源码；`cleat -M -m hello.core x` 的小项目；解开 linux-x64 自包含发布直接运行。全部通过。
- 本地 nightly 层（Determinism / Engineering / Conformance 共 12 个）在 13 上全过；GitHub Actions 的 nightly 有 1 个失败（用户贴的日志没有用例名，待查）。

## 待用户

- GitHub Actions：nightly 失败的用例名与错误（定位后修）；PR 层的结果。
- 推送（NuGet 源由用户定）与 `v1.2.0` tag、GitHub Release；执行后记进本任务。
