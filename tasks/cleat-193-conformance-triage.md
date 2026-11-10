# cleat-193 余下失败的逐项分诊与 M23 收口

- 里程碑：M23　依赖：cleat-189 ~ 192　状态：完成（2026-11-10）

## 问题

189 ~ 192 之后还剩的失败：CTS 现在 29 fail / 6 error（集中在 `str` 12、float / double 的打印 7、bigdec 3、byte / short / int 的边界、
string 的 starts-with? / ends-with? / split-lines），test_clojure 25 fail / 4 error（def 6、fn 8、math 6、pprint 3、protocols 1 + 4 error、java-interop 1），
加上 189 ~ 191 新跑起来的命名空间暴露的失败。偏离表（cleat-divergences.edn）521 行里 `:host` 408 行是"不模拟 Java 类库"的既定决策，不在此列。

## 工作内容

1. 每个失败：修（cleat 的缺陷）、记偏离（有意的差异：整数装箱为 Int64、Java 类库等，写明类别与原因），或标为上游测试的问题。
2. 数值打印（float / double / bigdec）与 `str` 对照 JVM Clojure 1.12 逐项核对。
3. 基线（cts-baseline.edn、test-clojure-baseline.edn）与偏离表更新；README 的测试规模表、CHANGELOG 同步。
4. M23 收口：r7rs 复核（改了编译器或运行时时，56 × 5 两台同轮配对，无可声明回退）。

## 验收

- 没有"原因未知"的失败；门禁通过；r7rs 无可声明回退。

## 补记（cleat-189 之后）

- test_clojure 失败 25 → 43：新增 18 个全是 special 的"did not conform to spec"（宏 spec 检查只在加载 clojure.core.specs.alpha 后做，cleat-166 的决定，
  designs/03 §4.2）。分诊时决定是维持偏离，还是改成 JVM 那样首次宏展开时惰性加载 spec（编译约慢 24%，见 README"已知限制"）。
- cleat-191 之后 test_clojure 失败 / 错误 54 / 9，处置见 cleat-191 的表；待 193 决定的：`*unchecked-math* :warn-on-boxed`（numbers 2 个）；
  另可考虑把 ClojureCLR Clojure.Tests.Support 的 C# 源码移成 tests/ 下的测试支撑项目（解开 attributes、clr.interop、try-catch、param-tags）。

## 结果

**处置表**：`tests/failure-dispositions.edn`——两个套件里每个有失败 / 错误的命名空间一个条目（类别集合 + 原因）；`tools/cts.clj` 每次运行核对
（`dispositions: OK`），有失败而没有条目、或条目对应的命名空间已全过，都让门禁失败。于是"原因未知"的失败进不了基线。类别：
`:follows-jvm`（cleat 照 JVM，套件期望 ClojureCLR 的行为）、`:integer-box-model`、`:nan-constant-identity`、`:spec-not-autoloaded`、
`:platform-test`（只在 Windows 上成立的测试）、`:clojureclr-only`（JVM 的同一测试没有这条断言）。

**修的**（`ClojureCompilerTests.Triage193`，`data/triage_193.clj`）：

| 失败 | 原因 | 修 |
|---|---|---|
| CTS bigdec：`(bigdec nil)` 不抛 | 反射调用对 null 选到接受 null 的 `BigDecimal.Create` 重载，返回坏掉的对象（打印时才 NRE） | `bigdec` 对 nil 抛 NullReferenceException（JVM 的 NPE） |
| test_clojure java-interop CLJ-2914 | `(String/.length)` 缺目标的编译错误没有原因 | 原因是 ArgumentException（JVM 的 IllegalArgumentException） |
| test_clojure protocols："No method of interface … (The protocol method may have been defined before and removed.)" | cleat 在运行期才报 "No implementation of method" | 分析器在协议方法的调用点核对协议的 :method-map，同 JVM 的 InvokeExpr |
| test_clojure pprint：promise 打印 `#object[clojure.core$promise$reify__…]` | reify 的类名用命名空间 | 用外层 fn 的类名（去掉末尾 __N，JVM 的 trimGenID） |
| test_clojure numbers warn-on-boxed（2 个） | 没实现 | `*unchecked-math* :warn-on-boxed` 时静态决议到 Numbers 的、有 Object 形参的方法打印 "Boxed math warning"（JVM 的 isBoxedMath，例外表照 @WarnBoxedMath(false)） |

**决定**：
- 宏的 spec 检查**维持**不自动加载（cleat-166）：spec 是独立的包，运行时里没有，做不到 JVM 那样首次宏展开时加载；加载后编译约慢 24%。
  test_clojure special / def / fn 的 32 个失败记 `:spec-not-autoloaded`。
- 数值打印 / `str`：CTS 的 `:cljr` 分支期望 ClojureCLR 的 `"1"` / `"True"` / `5.47E+18`；cleat 照 JVM（cleat-032 起的既定决策，designs/03 §4），逐项核对过与 JVM 1.12 一致，记 `:follows-jvm`。
- 含 `##NaN` 的字面量集合相等：常量共享装箱的 NaN、集合相等的同一性捷径（JVM 也有）使它们相等；clojure.org 的相等性指南也说"有时相等"。记偏离，不改常量共享。
- ClojureCLR 测试支撑程序集（attributes、clr.interop、try-catch、param-tags）：不在本任务做，留作后续（要把 ClojureCLR 的 C# 测试支撑源码移成测试项目）。

**基线**：CTS 6868 / 29 / 6 → 6868 / 28 / 6（bigdec nil 修好）；test_clojure 18190 / 54 / 9 → 18195 / 49 / 9。

**r7rs 复核**（M23 改了编译器：类型推断、常量、reify 命名、限定方法）：基线 = 7595c9b（M21 提交、M23 之前），新 = 当前工作树；56 × 5 两台同轮配对
（`bench/r7rs/pairbuild.py`，判定 `m16sum.py`：5% / 同向 5 轮）。**无可声明回退**：

| | 111（4 vCPU） | 13 |
|---|---|---|
| 配对比值中位的几何平均 | x0.984 | x0.974 |
| 同向 5/5 变快（> 5%） | puzzle x0.546、matrix x0.922、simplex x0.926、tail x0.940、bv2string x0.955、read1 x0.962 | puzzle x0.525、fib x0.718、mbrot x0.945、bv2string x0.959 |
| 同向 5/5 变慢 > 5% | nucleic x1.076 | 无（最接近：maze x1.048，111 上 x0.984） |

nucleic（只在 111 上）：本机复现（内部计时 5.5 s 对 5.05 s）后查明**执行的代码相同**——nucleic 自己编出的 IL 新旧编译器逐条相同（除计时外的一处 flush）；
镜像里它热路径上的 14 个 core 函数（nth、vector、first / rest / cons、=、算术、比较、seq、count）IL 逐条相同，镜像只差 31 个类（打印、bigdec、promise、reify 改名、
静态决议的打印调用），都不在它的路径上。只去掉 192 的 Var :tag（全镜像只改 11 处打印相关调用、但让之后所有方法的位置移动）就让计时翻转、同一构建内计时双峰——
是方法布局 / JIT 分层的效应，不是代码回退；13 上同一基准 x0.981。记为布局波动，不计回退。

## 门禁（2026-11-10，13）

- Core 116 / Compiler 50 / Host 300 全过（Host 17 分 26 秒）
- image-set --twice --ilverify：全部通过
- conformance --gate：521 / 521，OK
- CTS：6868 / 28 / 6，dispositions OK，基线 OK
- test_clojure：62 个命名空间，709 测试，18195 / 49 / 9，dispositions OK，基线 OK
