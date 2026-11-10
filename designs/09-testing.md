# 09 符合性与测试

## 1. 符合性判据（00 D16）

| 套件 | 来源 | 形态 | 用途 |
|---|---|---|---|
| **语料** `corpus.edn` | jolt `test/chez/corpus.edn`（EPL-2.0，复制到 `tests/conformance/`，保留来源说明）；约 5000 条 `{:suite :label :actual :expected :portability}`，`:expected` 由 JVM Clojure 取证 | 按 `=` 比较值；`:throws` | **主判据**。`:portability :common` 必须全过；`:jvm` 跳过；另加 `:clr` 行（我们自己写的 CLR 互操作用例） |
| clojure-test-suite | jank `jank/clojure-test-suite`（子模块 `tests/clojure-test-suite/`） | `clojure.test`，按命名空间 | 库覆盖；逐命名空间基线（与 jolt 相同：优于基线也要更新基线） |
| test_clojure | ClojureCLR `Clojure.Tests/clojure/test_clojure/`（EPL） | `clojure.test` | CLR 互操作、reader、打印、数值细节 |
| 已知偏离表 | `tests/conformance/cleat-divergences.edn`（cleat-032；jolt 的 `known-divergences.edn` 只作分类用词的参照） | 分类白名单（`:legend`） | 门禁只在**新增**或**过期**偏离时失败 |

认证流程照搬 jolt：`regen-corpus` 不在本仓库运行（我们没有 JVM）；语料视为冻结契约，新增用例直接写 `:expected`，并在偏离表里注明未经 JVM 取证。

## 2. 测试分层

| 层 | 内容 | 何时 |
|---|---|---|
| 运行时单测（C#，xunit） | 集合、数值、Var 绑定、reader、`Reflector` 决议、fasl 往返、`IFn` arity 错误 | M1 |
| 展开器单测 | 每个特殊形式的 Lcore 快照；错误信息 | M2 |
| stage0 | 手写 Lcore 小程序；`core.clj` 加载后的语料 `:common` 子集 | M2 |
| 发射器 | 手写 Lcil → dll → 运行；`deftype` 接口；`switch`；try/finally；互操作决议 | M3 |
| nanopass | 上游 `tests/` 翻译；展开确定性 | M4 |
| pass 快照 | 真实 Lcore 每个 pass 后的 unparse | M4 |
| 单命名空间编译 | 编译并加载 `clojure.string` 等 | M4 |
| 自举 | stage0 → 1 → 2 → 3；ILVerify | M5、M6 |
| 符合性 | 语料全量、clojure-test-suite、test_clojure 基线 | M6 |
| 错误黄金文件 | reader / 展开 / 宏 / 运行时错误的输出（jolt `test/errors/` 形态） | M7 |
| LLVM | 语料 `:common` 子集在原生可执行文件上运行；与 .NET 结果比较 | M8 |

## 3. 验收基线

M6 建立并记入本文：语料通过数 / 偏离数；clojure-test-suite 逐命名空间通过数；test_clojure 通过数；自举耗时与镜像大小。之后的任务不得降低。

已记入的基线：

| 日期 | 来源 | 项 | 值 |
|---|---|---|---|
| 2026-10-10 | cleat-053 | 语料 `:common`（`dotnet run tools/build.cs -- conformance --run`，宿主 + Clojure 镜像，`=` 比较） | 4842 行：通过 3913（80.8%），不等 179，异常 749，超时 1 |
| 2026-10-10 | cleat-032 | 语料 `:common`（同上；每行在新的 `user` 命名空间，`--gate` 按 `tests/conformance/cleat-divergences.edn`） | 4842 行：通过 **4281（88.4%）**，不等 98，异常 462，超时 1；其余 561 行（560 个 id）全部分类：`:host/jvm-api` 310、`:corpus/jolt-value` 98、`:host/regex` 39、`:host/exception-type` 28、`:not-yet` 27、`:host/class-name` 17、`:host/numeric-types` 10、`:upstream/clojureclr` 7、`:error-model` 6、其余 19。去掉 `:host/*` 与 `:corpus/jolt-value`（宿主相关或取证值不是 JVM 的）后的 4327 行通过 98.9% |
| 2026-10-10 | cleat-032 | clojure-test-suite（`tools/cts.clj cts`，247 个命名空间，`tests/cts-baseline.edn`） | 247 个全部加载运行；247 个 deftest，断言通过 6865、失败 32、错误 6 |
| 2026-10-10 | cleat-032 | test_clojure（`tools/cts.clj test-clojure`，`tests/test-clojure-baseline.edn`） | 85 个命名空间：跳过 32（外部库 test.generative / test.check、ClojureCLR 的测试支持程序集、辅助命名空间）；运行 53 个，其中 15 个加载失败（`clojure.lang.Compiler`、反射警告、gen-class 等）；339 个 deftest，断言通过 3443、失败 34、错误 144（pprint 124） |
| 2026-10-10 | cleat-053 | Clojure 镜像 `cleat.boot.dll`（`GenerateBootImage`） | 构建约 18 ~ 20 秒；dll 1.8 MB，Lcil 14 MB，2308 个类型，2189 个顶层形式；不动点 B == C |
| 2026-11-10 | cleat-189 | test_clojure（`clojure.lang.Compiler` 门面之后） | 85 个命名空间：跳过 33（compilation 需 test.generative、param-tags 需测试支持程序集，移入跳过）；运行 52 个，其中 5 个加载不了；439 个 deftest，断言通过 4223、失败 43、错误 4（special 的 18 个失败是宏 spec 检查未自动加载，designs/03 §4.2） |
| 2026-11-10 | cleat-192 | test_clojure（互操作打磨之后） | 85 个命名空间：跳过 35（clearing 上游无有效测试、compilation2 要 ClojureCLR 的 AOT 文件名，移入跳过）；运行 50 个，其中 1 个加载不了（api，cleat-190）；445 个 deftest，断言通过 4282、失败 43、错误 4（array-symbols 44、method-thunks 15 全过） |
| 2026-11-10 | cleat-190 | test_clojure（`clojure.clr.api.Clojure` 之后） | 85 个命名空间：跳过 36（api 剩 test.generative 的 defspec，移入跳过，交 cleat-191）；运行 49 个，**全部能加载**；445 个 deftest，断言通过 4282、失败 43、错误 4 |
| 2026-11-10 | cleat-191 | test_clojure（测试用库进加载路径之后） | 85 个命名空间：跳过 23（只剩辅助命名空间、ClojureCLR 测试支撑程序集、上游无有效测试、ClojureCLR 专有的类型与 AOT 文件名）；运行 62 个，全部能加载；709 个 deftest（含 test.generative 的 defspec，按固定种子各 100 次），断言通过 18190、失败 54、错误 9 |
| 2026-11-10 | cleat-193 | clojure-test-suite / test_clojure（分诊之后） | CTS 247 个命名空间：断言 6868 通过、失败 28、错误 6；test_clojure 62 个：18195 / 49 / 9。每个有失败的命名空间在 `tests/failure-dispositions.edn` 有类别与原因（`tools/cts.clj` 核对：缺条目或条目对应的命名空间已全过都算门禁失败） |

## 4. 运行方式

- `dotnet test`（全部 C# 测试 + 以进程方式驱动 `Cleat.Host` / `Cleat.Stage0` 的 Clojure 测试，每个套件一个进程）；`--filter Category=Bootstrap` / `Conformance`。
- `tools/build.cs conformance --run [--gate] [--results tests/conformance/cleat-results.edn]`（语料）、`tools/build.cs cts cts|test-clojure [--write]`（两个 clojure.test 套件；与基线不同——更好或更差——都失败，`--write` 有意地更新基线）；`dotnet test --filter Category=Conformance` 跑这三个门禁（cleat-032）。
- `tools/build.cs conformance --target llvm`（搁置）。
