# tools.namespace（cleat 移植，部分）

[tools.namespace](https://github.com/clojure/tools.namespace)（上游提交 `28d3f57`）中 tools.build 的 `compile-clj` 用到的部分，移植到 cleat：

| 命名空间 | 移植 |
|---|---|
| `clojure.tools.namespace.dependency` | 原样（`max-number` 加 `:cljr` 分支） |
| `clojure.tools.namespace.track` | 原样 |
| `clojure.tools.namespace.parse` | 用 `clojure.core/read`（同样的选项 map）代替 tools.reader；`clj-read-opts` 的平台特性是 `:cljr` |
| `clojure.tools.namespace.file` | System.IO；文件是 `FileInfo`（也接受路径字符串）；Clojure 扩展名加 `.cljr` |
| `clojure.tools.namespace.find` | 只保留目录的搜索（类路径与 jar 在 CLR 上没有对应物）；读不了的文件跳过（cleat 的读者抛普通 CLR 异常，不是 tools.reader 的 `:reader-exception`） |

没有移植：`dir`、`reload`、`repl`、`move`。

```sh
cleat -I packages/tools.namespace/src -I packages/tools.namespace/test packages/tools.namespace/test/run.clj
```

上游测试 `dependency_test`、`parse_test`、`find_test` 移植在 `test/`（读者换成 CLR 的，读者条件的例子用 `:cljr`）。许可证同上游（EPL 1.0）。
