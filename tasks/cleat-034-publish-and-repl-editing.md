# cleat-034 自包含发布与 REPL 行编辑

- 里程碑：M7　依赖：cleat-033　状态：未开始
- 设计依据：IronScheme `designs/07-tooling-and-embedding.md`（做法沿用）

## 工作内容
1. `dotnet publish` 自包含（boot 镜像与预编译库随发布）；`cleat` 启动脚本。
2. REPL 行编辑：历史、Tab 补全（Var、命名空间、关键字）、参数提示、多行输入、Ctrl+C / Ctrl+D。
3. `clojure.main` 的 `-r`、`--report`。

## 验收标准
- 发布目录在无 SDK 的机器（或容器）上可运行 REPL 与脚本。
