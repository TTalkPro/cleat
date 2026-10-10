# cleat-035 嵌入 API 与错误黄金文件

- 里程碑：M7　依赖：cleat-033　状态：未开始
- 设计依据：[09 §2](../designs/09-testing.md)

## 工作内容
1. `Cleat.Core.Hosting`：`Cleat.Init()`、`Eval(string)`、`Require(ns)`、`Var(ns, name).invoke(...)`；示例 `examples/WebServer`（Kestrel 最小接口，Clojure 写处理函数）。
2. 错误黄金文件 `tests/errors/{read,expand,macro,runtime,cli}/`：输入 + 期望输出；错误信息风格参照 jank `doc/error-message-style-guide.md` 与 Clojure 1.10+ 的 `ex-triage`。

## 验收标准
- 示例可运行；黄金文件测试进入 `dotnet test`。
