# cleat-034 自包含发布与 REPL 行编辑

- 里程碑：M7　依赖：cleat-033　状态：完成
- 设计依据：IronScheme `designs/07-tooling-and-embedding.md`（做法沿用）

## 工作内容
1. `dotnet publish` 自包含（boot 镜像与预编译库随发布）；`cleat` 启动脚本。
2. REPL 行编辑：历史、Tab 补全（Var、命名空间、关键字）、参数提示、多行输入、Ctrl+C / Ctrl+D。
3. `clojure.main` 的 `-r`、`--report`。

## 结果（2026-10-11）

### 发布
- `tools/build.cs publish [-r RID] [-o 目录]`：默认 `artifacts/publish/cleat-<RID>/`。先刷新 AOT 库（compile-libs），再 `dotnet publish --self-contained`，随发布复制 `cleat.scheme.boot.dll`、`cleat.scheme.clojure/`（驱动库 fasl）、`cleat.boot.dll` 与 `cleat.boot.ifc`、AOT 库与接口文件。发布目录本身在加载路径上（`AppContext.BaseDirectory`），宿主的查找逻辑无需改动；不裁剪（互操作靠反射元数据）。`cleat` 可执行文件即启动入口（apphost，无需包装脚本）。
- 宿主设 `InvariantGlobalization=true`：发布在无 ICU 的 Linux 发行版可跑（运行时只用 InvariantCulture）。
- **验收**：发布目录在无 .NET、无 ICU 的 ubuntu:22.04 容器中运行 REPL、`-e`、脚本与随发布的 AOT 库全部通过；`PublishedDirectoryRunsStandalone` 冒烟测试（发布 → 运行）入套件。

### REPL 行编辑
- `src/Cleat.Host/LineEditor.cs`（做法与结构沿用 IronScheme iron-038 的实现，词法按 Clojure 改写：逗号是空白、`{}` 计入括号、字符字面量 `\c`、无块注释）：行内编辑（Home/End、Ctrl+A/E/B/F/K/U/W/L、按词移动与删词）、多行输入（括号未闭合回车换行，续行提示 `  `）、历史（`~/.cleat_history`，上下键浏览）、Tab 补全（公共前缀 → 再按列出候选）、行尾参数提示（暗色）、右括号匹配高亮、Ctrl+C 取消 / 空行 Ctrl+D 退出。Unix 上用 ANSI 相对移动定位光标（不查询终端），Windows 用控制台 API。
- 标准输入或输出被重定向时退回原来的逐行 REPL（测试与管道不变）；终端不支持所需控制时也回退。
- 补全与提示（`Program.ReplServices`）：Var（当前命名空间的映射、`ns/name` 限定形式）、命名空间名（require 用）、关键字（`Keyword.All()` 新增的存活关键字枚举）；提示取 Var 的 `:arglists`。词法函数（`IsComplete` 等）由 `LineEditorTests` 单元测试。

### clojure.main 对齐
- `--report stdio|stderr`：错误报告的目标（默认 stderr，`stdio` 写 *out*），宿主与 `Driver.ReportError` 都遵守。
- `-r` / `--repl` 已有（先执行 -e / -i 再进 REPL），本轮验证。

## 验收标准
- 发布目录在无 SDK 的机器（或容器）上可运行 REPL 与脚本。——通过（见上）。
