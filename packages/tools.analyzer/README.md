# tools.analyzer（cleat 包）

`clojure.tools.analyzer`（上游可移植部分 1.2.2）与 `clojure.tools.analyzer.clr`（[tools.analyzer.clr](https://github.com/clojure/tools.analyzer.clr) 1.4.0）
在 cleat 上的版本（cleat-167），EPL-1.0。core.async 的 `go` 宏用它；依赖 [Cleat.ToolsReader](../tools.reader/README.md)。

## 与上游的差异（`;;; CLEAT-167` 注释处）

- 不依赖 core.memoize：反射成员的缓存（`lru` / `memo-clear!`）换成 `clojure.tools.analyzer.clr.utils` 里的小缓存（同样上限 2048）。
- 不导入 cleat 没有的 `clojure.lang.Compiler`。
- cleat 的特殊形式（cleat-184）：`proxy-class*`、`gen-class*` 解析成不透明节点（`:proxy-class` / `:gen-class`，emit-form 原样输出），`await*` 报错（只在 `^:async` fn 里有效）；
  `convertible?` 遇到解析不出的形参类型（泛型方法的 `T[]`）按不可转换（原来抛 NullReferenceException）。上游测试 `analyze-proxy` 恢复。

测试：`cleat -I packages/tools.reader/src -I packages/tools.analyzer/src -I packages/tools.analyzer/test packages/tools.analyzer/test/run.clj`（44 个测试、371 个断言）。
