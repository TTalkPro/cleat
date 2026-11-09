# test.check（cleat 包）

`clojure.test.check`（属性测试与生成器）在 cleat 上的版本（cleat-166），源码取自 ClojureCLR 的
[clr.test.check](https://github.com/clojure/clr.test.check)（1.1.4），EPL-1.0（epl-v10.html）。spec.alpha 的生成器依赖它。

- 源码：`packages/test.check/src` 放上加载路径；包：`cleat -T:build install`（包程序集 `Cleat.TestCheck`），`{:deps {Cleat.TestCheck {:nuget/version "1.1.4"}}}`。

## 与上游的差异（`;;; CLEAT-166` 注释处）

- `shrink-long` 对 `Int64/MinValue` 不调 `Math/Abs`（.NET 的会抛 OverflowException，Java 的原样返回）——ClojureCLR 移植版自身的缺陷。
- 测试里 `gen/byte` 的断言：cleat 把装箱的整数统一成 Int64（整数装箱模型，见 tests/conformance/cleat-divergences.edn），
  生成的 byte 是 0..255 的整数而不是装箱的 System.Byte。

测试：`cleat -I packages/test.check/src -I packages/spec.alpha/src -I packages/test.check/test packages/test.check/test/run.clj`
（104 个测试；断言数随随机种子略有不同）。
