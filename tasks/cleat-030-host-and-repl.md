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
