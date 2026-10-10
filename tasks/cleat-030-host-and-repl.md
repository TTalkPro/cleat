# cleat-030 宿主 Cleat.Host 与 REPL

- 里程碑：M5　依赖：cleat-022、cleat-012　状态：未开始
- 设计依据：[02 §9、§11](../designs/02-runtime.md)

## 工作内容
1. `Cleat.Host`：初始化运行时 → 加载 `cleat.boot.dll`（`--boot` 指定，默认自动找 `src/boot/`）→ `clojure.main` 语义的命令行（`-m`、`-e`、`-i`、脚本、`--`）；`-I` 加载路径；`CLEAT_LOAD_PATH`。
2. 简单 REPL（逐行、`*1`/`*e`、`:repl/quit`）；行编辑留到 cleat-034。
3. 退出码、未捕获异常的打印（`clojure.stacktrace` 风格）。
4. 测试：进程方式驱动 `Cleat.Host` 的冒烟测试。

## 验收标准
- 用 stage1 启动 REPL，`(require 'clojure.string)`（源码加载 → 编译）成功。

## 结论（2026-10-10，部分）

骨架完成，等 boot 镜像 / stage0 提供 `eval` 钩子后即可实际运行。
- `Cleat.Host.Program`：`clojure.main` 风格命令行（`--boot` / `CLEAT_BOOT` / 自动找 `src/boot/cleat.boot.dll`；`-I`；`-e` / `-i` 按顺序；`-m ns args…`；脚本；`-`；`-r`；无动作则 REPL）；启动顺序 登记原语 → `RT.Init` → 加载镜像并设置 `BootInterface.CurrentImageId` → 动作；`*command-line-args*` 绑定。
- REPL：逐形式读—`RuntimeHooks.Eval`—打印，`*1` `*2` `*3` `*e`，`:repl/quit`，错误打印异常链（`CLEAT_TRACE_ERRORS` 打印栈），`ReportError` 钩子可接管。
- 测试 `HostTests`（3 项，用假的 eval 钩子）：参数解析、REPL 输出与历史、钩子缺失的错误。
- 未完成：用 stage1 启动并 `(require 'clojure.string)` 的验收（依赖 cleat-029）；行编辑在 cleat-034。

## 进度
- 2026-10-10（cleat-052）：宿主接上 Scheme 镜像与驱动库，REPL / 脚本 / `-e` / `-i` / `-` / `load-file` / `RT.load` 实际可用（不依赖 clojure.core）；启动顺序改为两镜像（`--scheme-boot` 与 `--boot`），见 cleat-052 结论。错误打印改由 Scheme 登记的 `report-error`（Clojure 风格）。剩余验收 `(require 'clojure.string)` 需要 core.clj 的 `require`：cleat-053 完成后在 `tests/Cleat.Host.Tests` 加一项 REPL 里 `(require 'clojure.string)` 的进程测试即可把本任务改为完成（行编辑仍在 cleat-034）。
