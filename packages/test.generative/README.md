# test.generative（cleat 包）

`clojure.test.generative`（`defspec`：按参数的类型提示生成输入、反复运行被测函数与校验）在 cleat 上的版本（cleat-191），源码取自 ClojureCLR 的
[clr.test.generative](https://github.com/clojure/clr.test.generative)（1.1.2），EPL-1.0（epl-v10.html），未改动。
依赖 data.generators（packages/data.generators）与 tools.namespace（packages/tools.namespace，runner 用它找目录里的命名空间）。

- 源码：`packages/test.generative/src`（连同两个依赖的 src）放上加载路径；包：先装两个依赖，再 `cleat -T:build install`（包程序集 `Cleat.TestGenerative`）。
- 测试：`cleat -I packages/test.generative/src -I packages/data.generators/src -I packages/tools.namespace/src -I packages/test.generative/test packages/test.generative/test/run.clj [msec]`
  ——上游的测试是 defspec，test/run.clj 用上游 runner 的 `run-suite` 在 2 个线程上各跑 msec 毫秒（缺省 2000；上游按系统属性 / 环境变量
  `clojure.test.generative.msec`，缺省 10000），22 个 spec，全部通过时退出码为 0。
- test_clojure 里的 defspec 由 `tools/cts.clj` 按固定种子各跑 100 次（计数可重复，不按时间）。
