# core.async（cleat 包）

`clojure.core.async` 在 cleat 上的版本（cleat-167），源码取自 ClojureCLR 的 [clr.core.async](https://github.com/clojure/clr.core.async)（1.9.865），EPL-1.0（epl.html）。
`go` 宏用 tools.analyzer.clr 分析 go 块再变换成状态机，所以依赖 [Cleat.ToolsAnalyzer](../tools.analyzer/README.md) 与 [Cleat.ToolsReader](../tools.reader/README.md)。

- 源码：`-I packages/tools.reader/src -I packages/tools.analyzer/src -I packages/core.async/src`；包：先 install tools.reader、tools.analyzer，再
  `cleat -T:build install`（`Cleat.CoreAsync`），项目里 `{:deps {Cleat.CoreAsync {:nuget/version "1.9.865"}}}`。
- channel / buffer / `go` / `<!` `>!` / `alts!` / `timeout` / `pipeline` / `mult` / `pub` / `thread` 都可用；go 块用 .NET 线程池，`thread` 走专用线程。

## 与上游的差异（`;;; CLEAT-167` 注释处）

- go 块里漏出的异常照 JVM 缺省的未捕获异常处理器打印到 `*err*`（`Exception in thread …`）；原来在 .NET 线程池里会让进程直接退出。
- 宏 `&env` 的值是 cleat 的 `Cleat.Core.Expand.LocalBinding`（只有 `:tag` 提示）。
- timeout 线程（`impl/timers.clj` 的 `timeout-worker`，cleat-185）：在锁里读最早的项、等待时间不小于 0。上游的写法在最早的项恰好过期时会算出负的等待：
  `WaitOne` 按整毫秒截断，(-2, -1] ms 成了 -1 即无限等待（之后不再有 timeout 调用时所有 timeout 通道都不关），更小的在该线程上抛出、进程退出。上游 clr.core.async 同样有此问题。

go 块里可以直接写 `proxy`（cleat 的 proxy 展开成自己的特殊形式 `proxy-class*`，tools.analyzer.clr 把它当不透明节点；方法体是普通 fn，捕获的 go 局部跨停顿点照常保存，cleat-184）。
不能用 `await`（只在 `^:async` fn 里直接有效），报清楚的错误。cleat 专有的测试在 `test/clojure/core/async/cleat_go_proxy_test.clj`。

测试：`cleat -I packages/tools.reader/src -I packages/tools.analyzer/src -I packages/core.async/src -I packages/core.async/test packages/core.async/test/run.clj`
（ClojureCLR 移植的上游测试 51 个与 cleat 专有的 8 个，共 59 个测试、351 个断言）。
