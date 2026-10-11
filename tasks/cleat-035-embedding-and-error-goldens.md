# cleat-035 嵌入 API 与错误黄金文件

- 里程碑：M7　依赖：cleat-033　状态：完成
- 设计依据：[09 §2](../designs/09-testing.md)

## 工作内容
1. `Cleat.Core.Hosting`：`Cleat.Init()`、`Eval(string)`、`Require(ns)`、`Var(ns, name).invoke(...)`；示例 `examples/WebServer`（Kestrel 最小接口，Clojure 写处理函数）。
2. 错误黄金文件 `tests/errors/{read,expand,macro,runtime,cli}/`：输入 + 期望输出；错误信息风格参照 jank `doc/error-message-style-guide.md` 与 Clojure 1.10+ 的 `ex-triage`。

## 结果（2026-10-11）

### 嵌入 API
- 新项目 `src/Cleat.Hosting`（命名空间 `Cleat.Core.Hosting`，类 `CleatRuntime`——沿用 IronScheme `SchemeRuntime` 的命名；名为 `Cleat` 的类无法在 `Cleat.*` 命名空间树之外引用，会被根命名空间 `Cleat` 遮住）：
  - `Init(CleatOptions)`：与宿主完全同一路径（`Cleat.Host.Program.Start`）加载 Scheme 镜像、驱动库与 Clojure 镜像；绑定 `user` 命名空间与 `*command-line-args*`。
  - `Eval(string)`（读入全部形式、返回最后值）、`Require(ns)`、`Var(ns, name)`（Var 的值作 `IFn`，`invoke(…)` 调用）、`Print(object)`。
  - 展开器与编译器不并发、需要大栈：全部调用经锁排到一条 512 MB 栈的工作线程串行执行（Kestrel 的并发请求因此安全）；Clojure 异常原样传出。
- 示例 `examples/WebServer`：Kestrel 最小接口在 C# 侧，处理函数是 Clojure（`handlers.clj`：纯函数、C# 数组参数、atom 计数）；随机端口，`--urls` 可覆盖。**验收**：进程内测试（`tests/Cleat.Examples.Tests`）HTTP 驱动各端点，8 个并发 `/stats` 的 atom 计数恰为 1..8（串行化不重不漏）。

### 错误黄金文件
- cleat-055 已建 `tests/errors/{read,expand,macro,runtime}` 四类 30 例并入套件；本任务补 **`cli/` 五例**（未知选项、`-e` 缺表达式、脚本不存在、`-m` 无 `-main`、命名空间找不到）——`args.txt` 是命令行参数；机器相关路径在运行器里归一化为 `<host>` / `<case>` / `<repo>` 后逐字比较。`ErrorGoldenTests` 扩展为五类（37 项测试）。

## 验收标准
- 示例可运行；黄金文件测试进入 `dotnet test`。——通过（全部 583 项测试，含 Examples 1 项与五类错误黄金文件）。
