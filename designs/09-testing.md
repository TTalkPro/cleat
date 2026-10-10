# 09 符合性与测试

## 1. 符合性判据（00 D16）

| 套件 | 来源 | 形态 | 用途 |
|---|---|---|---|
| **语料** `corpus.edn` | jolt `test/chez/corpus.edn`（EPL-2.0，复制到 `tests/conformance/`，保留来源说明）；约 5000 条 `{:suite :label :actual :expected :portability}`，`:expected` 由 JVM Clojure 取证 | 按 `=` 比较值；`:throws` | **主判据**。`:portability :common` 必须全过；`:jvm` 跳过；另加 `:clr` 行（我们自己写的 CLR 互操作用例） |
| clojure-test-suite | jank `jank/clojure-test-suite`（子模块 `tests/clojure-test-suite/`） | `clojure.test`，按命名空间 | 库覆盖；逐命名空间基线（与 jolt 相同：优于基线也要更新基线） |
| test_clojure | ClojureCLR `Clojure.Tests/clojure/test_clojure/`（EPL） | `clojure.test` | CLR 互操作、reader、打印、数值细节 |
| 已知偏离表 | `tests/conformance/known-divergences.edn` | 分类白名单（`:legend`） | 门禁只在**新增**或**过期**偏离时失败 |

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
| 2026-10-10 | cleat-053 | Clojure 镜像 `cleat.boot.dll`（`GenerateBootImage`） | 构建约 18 ~ 20 秒；dll 1.8 MB，Lcil 14 MB，2308 个类型，2189 个顶层形式；不动点 B == C |

## 4. 运行方式

- `dotnet test`（全部 C# 测试 + 以进程方式驱动 `Cleat.Host` / `Cleat.Stage0` 的 Clojure 测试，每个套件一个进程）；`--filter Category=Bootstrap` / `Conformance`。
- `tools/build.cs conformance [--target llvm]`。
