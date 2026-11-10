# cleat-188 1.2.0：发布产物与发布清单

- 里程碑：M22　依赖：cleat-187　状态：未开始

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
