# data.generators（cleat 包）

`clojure.data.generators`（随机数据生成器：标量、集合、字符串、日期……按 `*rnd*` 可重复）在 cleat 上的版本（cleat-191），源码取自 ClojureCLR 的
[clr.data.generators](https://github.com/clojure/clr.data.generators)（1.1.0），EPL-1.0（epl-v10.html），未改动。test.generative 与 test_clojure 的若干测试用它。

- 源码：`packages/data.generators/src` 放上加载路径；包：`cleat -T:build install`（包程序集 `Cleat.DataGenerators`），`{:deps {Cleat.DataGenerators {:nuget/version "1.1.0"}}}`。
- 测试：`cleat -I packages/data.generators/src -I packages/data.generators/test packages/data.generators/test/run.clj`（3 个测试、100 个断言）。
