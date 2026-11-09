# spec.alpha（cleat 包）

`clojure.spec.alpha`、`clojure.spec.gen.alpha`、`clojure.spec.test.alpha` 与 `clojure.core.specs.alpha` 在 cleat 上的版本（cleat-166）。
源码取自 ClojureCLR 的 [clr.spec.alpha](https://github.com/clojure/clr.spec.alpha)（0.6.250，对应上游 spec.alpha 0.6.249）与
[clr.core.specs.alpha](https://github.com/clojure/clr.core.specs.alpha)（0.6.133），EPL-1.0（epl-v10.html）。

## 用法

- 源码：把 `packages/spec.alpha/src` 放上加载路径；包：`cleat -T:build install`（包程序集 `Cleat.SpecAlpha`），项目里
  `{:deps {Cleat.SpecAlpha {:nuget/version "0.6.250"}}}`。
- `s/def`、`s/valid?`、`s/conform`、`s/explain`、`s/fdef` + `stest/instrument` 都可用；生成器（`s/gen`、`s/exercise`、`stest/check`）
  与上游一样延迟加载 `clojure.test.check`：要用时再加上 [Cleat.TestCheck](../test.check/README.md)。
- **宏的 spec 检查**：加载了 spec 之后，宏展开前按宏的 `:args` spec 核对（`s/fdef` 过的宏；`defn` / `let` / `ns` 等 core 宏在
  `(require 'clojure.core.specs.alpha)` 之后生效），不合时报 `Syntax error macroexpanding clojure.core/let at (…)` 加 spec 的解释，与 JVM 相同。
  `CLOJURE_SPEC_SKIP_MACROS=true` 关掉。没加载 spec 时没有任何检查与开销。`s/assert` 由 `CLOJURE_SPEC_CHECK_ASSERTS=true` 或 `(s/check-asserts true)` 打开。

## 与上游的差异（`;;; CLEAT-166` 注释处）

- `clojure.lang.Compiler/demunge` → `clojure.lang.Munge/demunge`（cleat 的名字修饰在 Munge）。
- instrument 找调用者时把 `doInvoke` 也算作 Clojure 帧：cleat 的变长函数体在 `doInvoke` 里（JVM 1.9+ 另有 `invokeStatic`）。
- 测试 `instr_test.clj` 期望的 `:var-scope` 跟着 ClojureCLR 改过的命名空间名（`instr-test`）。

测试：`cleat -I packages/spec.alpha/src -I packages/test.check/src -I packages/spec.alpha/test packages/spec.alpha/test/run.clj`（13 个测试、174 个断言）。
