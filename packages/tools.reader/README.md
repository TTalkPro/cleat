# tools.reader（cleat 包）

`clojure.tools.reader`（含 edn、reader-types、source-logging）在 cleat 上的版本（cleat-167），源码取自 ClojureCLR 的
[clr.tools.reader](https://github.com/clojure/clr.tools.reader)（1.5.2），EPL-1.0。tools.analyzer.clr 依赖它。

## 与上游的差异（`;;; CLEAT-167` 注释处）

- 读构造器形式（`#my.Record[…]` / `#my.Record{…}`）用 cleat 的 `Cleat.Core.Runtime.Reflector/New` / `Call`（ClojureCLR 是 `clojure.lang.Reflector`）。

测试：`cleat -I packages/tools.reader/src -I packages/tools.reader/test packages/tools.reader/test/run.clj`（51 个测试、889 个断言）。
