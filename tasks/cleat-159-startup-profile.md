# cleat-159 启动剖析与目标值

- 里程碑：M19　依赖：无　状态：未开始
- 设计依据：[29 §1 第 2 条 / §3.1](../designs/29-usability-and-engineering.md)

## 问题

本机（2026-10-26）`cleat -e nil` 需要 0.9 ~ 1.3 s。`CLEAT_TIME=1` 显示：Clojure 镜像 671 ms，编译器包 193 ms。
目前还不知道 671 ms 里 JIT、类型加载、命名空间初始化、常量池 / fasl 反序列化各占多少，160 / 161 的范围定不下来。

## 工作内容

1. `CLEAT_TIME` 扩展为分段计时：程序集加载、模块初始化（按 `boot-order.edn` 条目）、常量池、编译器包、宿主命令行解析。
   不设置时不输出、不计时（与现状一致）。
2. `dotnet-trace`（已安装）采集 `-e nil`、`-e '(require (quote clojure.pprint))'`、`-r` 到首个提示符三种场景，
   按 JIT / 类型加载 / GC / 用户代码分桶。
3. 对照组：`DOTNET_TieredCompilation=0`、`DOTNET_TC_QuickJitForLoops`、`DOTNET_ReadyToRun=0`（关掉框架 R2R 作为反证），
   确认 JIT 的份额。
4. 在 notes 增加"启动画像"一节，给出 160 / 161 的目标值。暂定 `-e nil` ≤ 0.4 s、require pprint ≤ 0.6 s，
   以本任务的画像为准修订。

## 验收

- 画像可以复现：命令、环境、5 次中位与极差都写进 notes。
- 160 / 161 的目标值和预计收益按桶写清楚。
- 只改计时代码与文档；改到 `src/clojure` 时重走种子。
