# cleat-097 刷新 clojure-test-suite 与 test_clojure 基线

- 里程碑：M9　依赖：cleat-095　状态：完成

## 背景
`tests/cts-baseline.edn`（jank 的 clojure-test-suite：2026-10-11，6866 过 / 31 fail / 6 error）与 `tests/test-clojure-baseline.edn` 都是 M8 之前
在 Scheme 编译器上测的；自举后的 Clojure 编译器还没有对着这两套大基线验收。

## 工作内容
1. `tools/build.cs cts cts` / `cts test-clojure` 在当前编译器上重跑，与基线比较：新通过的下调基线（`--write`），回归逐条修。
2. 剩下的失败逐条分类（宿主差异 / 未实现 / 编译器缺陷），缺陷开后续任务或就地修。

## 验收标准
- 两条基线与当前结果一致（门禁通过）；每个失败在基线文件的注释或偏离说明里有归类。

## 结论（2026-10-15）

**自举后的编译器与 Scheme 编译器的结果逐命名空间完全一致**：`cts` 247 个命名空间 6866 过 / 31 fail / 6 error，`test-clojure` 357 个测试
3670 过 / 36 fail / 14 未加载——两条基线 `baseline: OK`，没有回归，也没有新通过的。

**失败分类**（逐条跑过）

clojure-test-suite（jank；`#?(:cljr …)` 分支按 ClojureCLR 的行为写，cleat 的 reader 带 `:cljr` 特性所以走这些分支）：
| 命名空间 | 数 | 归类 |
|---|---|---|
| byte / short / int / float / double? / num | 1+1+1+4+3+1 | `(int 0)` 等按设计返回 long，`float` 返回 double（CLR 原生数值，designs/02；ClojureCLR 返回 Int32 / Single） |
| str | 12 | `:cljr` 分支期望 `(str 0.0)` = "0"、`(str true)` = "True"（ClojureCLR 直接 ToString）；cleat 照 JVM 打印 "0.0" / "true" |
| eq / not-eq | 2+3 | `:cljr` 分支期望含 NaN 的集合不相等；cleat 同 JVM（clojure.org/guides/equality：集合里的 NaN 可以相等） |
| case | 1 | `(case ##NaN ##NaN …)` 在 cleat 命中（Equals 认 NaN 相等），JVM 不命中——与上一条同源，不改 |
| bigdec / repeat / ends-with? / starts-with? / split-lines | 3+2+1+1+1 | jank 特有的期望（`(bigdec true)`、`(repeat true :a)`、`(str/ends-with? nil "")`、`(str/split-lines "")` = []）；JVM 抛错或给 `[""]` |
| shuffle | 1 | **真缺陷**：`(shuffle 1)` 返回 `[]`（`ArrayList(int)` 当容量）→ 本任务修为抛 InvalidCastException，同 JVM |

test_clojure（ClojureCLR）：
| 命名空间 | 数 | 归类 |
|---|---|---|
| rt | 18 | `*warn-on-reflection*` 的反射警告：cleat 不输出（未实现；见 cleat-098 后续） |
| fn | 8 | 期望 clojure.spec 的 "did not conform to spec" 错误；cleat 没有 spec，报 "Parameter declaration a should be a vector" |
| math | 6 | ClojureCLR 的 `clojure.math/round` 返回 double；cleat 同 JVM 返回 long |
| pprint | 3 | `*print-meta*` 下 `:column` 差 1（ClojureCLR 的 reader 记 quote 之后的列；cleat 与 JVM 相同）、print-table 列宽（ClojureCLR 的 double 打印）、promise 的打印形式 |
| java-interop | 1 | `System.String/.length` 不带目标：测试要求 cause 是 ArgumentException；cleat 的编译错误是一个 ExpanderException（error-model，同偏离表的 `:error-model`）。措辞顺手改成 ClojureCLR 的 "Malformed method expression"，但类型不同所以仍失败 |
| 14 个未加载 | — | 依赖 `clojure.lang.Compiler`、`java.lang.reflect`、ClojureCLR 的测试支持程序集等（基线里的 `:load-error`） |

修了 shuffle 之后重跑：`cts` 6867 过 / 30 fail / 6 error（shuffle 转为通过），`test-clojure` 不变（36 fail），基线以 `--write` 更新；
偏离表删去两条 `:strictness` 的 shuffle 行。
反射警告（rt 的 18 条）值得作为独立任务实现。

