# cleat

[![pr](https://github.com/TTalkPro/cleat/actions/workflows/pr.yml/badge.svg)](https://github.com/TTalkPro/cleat/actions/workflows/pr.yml)
[![nightly](https://github.com/TTalkPro/cleat/actions/workflows/nightly.yml/badge.svg)](https://github.com/TTalkPro/cleat/actions/workflows/nightly.yml)

Cleat — Clojure made fast to the CLR.

cleat 是 .NET 上的 Clojure 实现：运行时与编译器都用 C# 写（编译器是 nanopass 风格的多遍编译器，逐层下降后用 ILGenerator 直接发射），
编出的代码是普通的 .NET 程序集。Clojure 镜像由编译器从源码直接编出，没有种子、不需要自举，构建只依赖 .NET SDK。
（cleat-091 ~ 181 期间编译器用 Clojure 自己写、靠文本种子自举；cleat-182 删除，见 [designs/30](designs/30-csharp-compiler.md)。）

- **语义以 JVM Clojure 1.12 为准**，用三套外部测试做符合性判据：jolt 的 JVM 取证语料、jank 的 clojure-test-suite、ClojureCLR 的 test_clojure。
- **不依赖 DLR**：互操作（反射调用、类型提示、静态决议）是运行时自己做的；数值语义按 CLR 的类型来（见下文"与 Clojure 的偏离"）。
- **真实的 .NET 类型**：`deftype` / `defrecord` / `gen-class` / `proxy` 生成的是普通类，C# / F# 可以直接 `new`、实现接口、作为库引用。
- **Clojure CLI 的工作方式**：`cleat.edn` 描述项目，依赖是 NuGet 包，`cleat -M` / `-X` / `-T` 与 `clj` 对应，tools.build、nREPL、neorepl 都已移植。
- 只支持 **net10.0**。CIL 是唯一的后端；LLVM 后端的设计保留（[designs/06](designs/06-llvm-backend.md)），实现搁置。

## 快速开始

```sh
git clone --recurse-submodules <repo>     # tests/clojure-test-suite 是子模块
cd cleat
dotnet build -c Release                   # 编 C# 项目，再由编译器从 src/clojure 两步编出 Clojure 镜像（约 12 秒）
dotnet test  -c Release --no-build        # Core + Compiler + Host 三套测试（Host 套件约 17 分钟，含镜像确定性与 ILVerify）

src/Cleat.Host/bin/Release/net10.0/cleat -r                     # REPL
src/Cleat.Host/bin/Release/net10.0/cleat -e '(println (+ 1 2))'  # 求值
src/Cleat.Host/bin/Release/net10.0/cleat script.clj a b         # 跑脚本
```

作为 dotnet tool 使用（命令 `cleat`，含镜像、编译器与随附库）：

```sh
dotnet run tools/build.cs -- pack -o artifacts/nuget           # 打 Cleat.Runtime（运行时）与 Cleat.Cli（工具）两个 NuGet 包
dotnet tool install -g Cleat.Cli --add-source artifacts/nuget
cleat -r
```

完整的项目示例见 [examples/WebServer](examples/WebServer/README.md)（ASP.NET 主机 + Clojure 处理逻辑 + gen-class + neorepl）和
[examples/SqlTable](examples/SqlTable/README.md)。

## 命令行

```
cleat [--boot dll] [-I dir]... [-e expr]... [-i file]... [-m ns args... | script args... | -r | -]
cleat [-Sdeps EDN] [-Spath | -P] -M[:alias...] [options and args...]   按 cleat.edn 运行项目，同 clj -M
cleat [-Sdeps EDN] -X[:alias...] [ns/fn] [:key value]...               以映射调用函数，同 clj -X
cleat -T:alias function [:key value]...                                 工具函数（-T:build 用 build.clj），同 clj -T
cleat -I src/clojure --compile-boot out/cleat.boot.dll                  编 Clojure 镜像（构建用）
cleat -I src... --compile-package out/Name.dll [--root dir]... ns...    编包程序集（tools.build 的 compile-clj 用）
```

`cleat.edn` 的写法与 `deps.edn` 相同，依赖写 `{:nuget/version "…"}`，由 `dotnet restore` 解析（[designs/18](designs/18-tools-build.md)）。

## 仓库布局

| 目录 | 内容 |
|---|---|
| `src/Cleat.Core/` | 运行时（C#），一个程序集三个命名空间：`clojure.lang`（集合、数值、Var、Namespace、reader，改写自 ClojureCLR，EPL）、`Cleat.Core.Runtime`（加载器、互操作与静态决议、Fasl、包程序集索引、启动）、`Cleat.Core.Compiler`（类型提示决议、驱动入口） |
| `src/Cleat.Compiler/` | 编译器（C#，[designs/30](designs/30-csharp-compiler.md)）：`Analyzer`（Clojure 形式 → L0）、`Ir`（L0 ~ L3 节点、打印 / 读入、校验）、`Passes`（前中端各遍）、`Lower`（L4 表示、L5 线性 CIL）、`Emit`（ILGenerator 发射、持久化程序集、常量表）、`Driver`（eval / load / compile 钩子）；`Cleat.Compiler.Generators` 是 IR 节点的 Source Generator |
| `src/Cleat.Host/` | 宿主 `cleat`：命令行、REPL、`-M/-X/-T`、镜像与编译器的加载、`--compile-boot` / `--compile-package` |
| `src/Cleat.Hosting/` | 嵌入 API `Cleat.Core.Hosting.CleatRuntime`（Init / Eval / Require / Var，惰性启动）：C# / F# 程序宿主 cleat 时用；与命令行宿主共用 `Cleat.Core.Runtime.Startup` 启动（[designs/17](designs/17-lazy-runtime-startup.md)） |
| `src/Cleat.Runtime.Package/` | NuGet 包 `Cleat.Runtime` 的打包项目（无代码）：Core、Hosting、Compiler 程序集加镜像与随附库，给 C# / F# 项目引用 |
| `src/clojure/clojure/` | `clojure.core` 与随附命名空间（string、set、walk、edn、pprint、test、repl、main、zip、data、instant、clr.io …），改写自 Clojure / ClojureCLR（EPL） |
| `src/boot/` | `boot-order.edn`（镜像的加载顺序）；构建生成的 `cleat.boot.dll` 与随附库不入库（[docs/boot.md](docs/boot.md)） |
| `packages/` | 移植的库：`tools.build`、`tools.namespace`、`nrepl`、`neorepl`、`nanopass`（nanopass 框架的 Clojure 移植，[用户文档](packages/nanopass/README.md)，MIT）、`spec.alpha`（含 core.specs，[说明](packages/spec.alpha/README.md)）、`test.check`、`core.async`（[说明](packages/core.async/README.md)，依赖 `tools.analyzer`、`tools.reader`），各自是 cleat 项目，打成 NuGet 包 |
| `tests/` | `Cleat.Core.Tests`、`Cleat.Compiler.Tests`、`Cleat.Host.Tests`（NUnit）；`conformance/`（jolt 语料与偏离表）、`clojure-test-suite/`（子模块）、`test_clojure/`、两份基线 `cts-baseline.edn` / `test-clojure-baseline.edn` |
| `tools/` | `build.cs`（维护命令，见下）、`cts.clj`（两套测试的基线运行器）、`conformance.clj`（语料运行与门禁） |
| `designs/`、`tasks/`、`TASK.md` | 设计文档（[索引](designs/README.md)）、任务文件、任务与里程碑索引 |
| `bench/` | 基准集（[bench/README.md](bench/README.md)） |

## 构建

`dotnet build` 由编译器从源码两步编出镜像（[docs/boot.md](docs/boot.md)）：

```
宿主不加载镜像（--no-boot），编译器按 boot-order.edn 编 src/clojure ──▶ obj/cs-stage1/cleat.boot.dll
在 stage1 上再编一遍 ──▶ src/boot/cleat.boot.{dll,pdb,ifc,inl}（Clojure 镜像）
```

编译器的输出与起步镜像无关，所以第二遍就是最终结果；构建两次逐字节相同（G5，`DeterminismTests` 与 PR CI 的确定性守卫核对）。
改了 `src/clojure` 不需要任何额外步骤。整套产物的确定性与 ILVerify：

```sh
dotnet run tools/build.cs -- image-set --twice --ilverify
```

`tools/build.cs` 的其他命令：`cts cts|test-clojure [--write]`、`conformance --run [--gate]`、`bench`、`compile-libs`、`compile-tools`、`pack`、`publish`（自包含发布）、`r2r`（ReadyToRun 快照）、`ilverify`；
每个命令的说明在 [tools/build.cs](tools/build.cs) 开头的注释里。

## 测试与基线

| 套件 | 规模（2026-11-05） | 怎么跑 |
|---|---|---|
| Cleat.Core.Tests / Cleat.Compiler.Tests / Cleat.Host.Tests | 116 / 50 / 289 | `dotnet test Cleat.slnx -c Release` |
| jolt 语料（`:common` 行） | 失败行全部登记在 [cleat-divergences.edn](tests/conformance/cleat-divergences.edn)（521 行，各带类别与原因） | `dotnet run tools/build.cs -- conformance --run --gate` |
| clojure-test-suite | 247 个命名空间，断言 6868 通过 / 29 失败 / 6 错误 | `cleat tools/cts.clj cts` |
| test_clojure（ClojureCLR） | 54 个命名空间（31 个跳过），384 个测试，断言 3877 通过 / 25 失败 / 4 错误 | `cleat tools/cts.clj test-clojure` |
| packages/ 下各包的上游测试 | nrepl、neorepl、tools.build、tools.namespace、nanopass、spec.alpha（174 断言）、test.check、tools.reader（889）、tools.analyzer（370）、core.async（342），源码与包程序集各一遍 | `dotnet test tests/Cleat.Host.Tests -c Release --filter Category=Packages` |

后两套逐命名空间与基线比较，差异即失败；有意改变时用 `--write` 重写基线，失败的分类写在对应任务文件里（[cleat-097](tasks/cleat-097-refresh-baselines.md)）。

### CI 与本地复现

CI 分两层（[cleat-162](tasks/cleat-162-ci.md)、[designs/29 §4](designs/29-usability-and-engineering.md)）：

| 层 | 触发 | 内容 |
|---|---|---|
| [pr](.github/workflows/pr.yml) | push / PR | 两个并行 job，各自 Release 构建：`core-host`（Core 与 Compiler 全量、确定性守卫 `Category("Determinism")`——从零两步编出的镜像与构建产物逐字节相同、不动点——与 Host 的 `Category("Host")`）、`packages`（`Category("Packages")`，packages/ 下各包的测试） |
| [nightly](.github/workflows/nightly.yml) | 每日定时 / 手动 | `Category("Determinism")` + `Category("Engineering")`（发布、NuGet 冷加载、示例工程）+ `Category("Conformance")`、`conformance --run --gate`、CTS 与 test_clojure 基线、`image-set --twice --ilverify`（整套产物的 G5 与 G3） |

本地复现 CI：

```sh
dotnet build Cleat.slnx -c Release -v quiet -nologo
dotnet test tests/Cleat.Core.Tests -c Release --no-build                                                        # PR core-host
dotnet test tests/Cleat.Compiler.Tests -c Release --no-build
dotnet test tests/Cleat.Host.Tests -c Release --no-build --filter "Category=Determinism|Category=Host"
dotnet test tests/Cleat.Host.Tests -c Release --no-build --filter "Category=Packages"                            # PR packages
dotnet test tests/Cleat.Host.Tests -c Release --no-build --filter "Category=Determinism|Category=Engineering|Category=Conformance"   # nightly
dotnet run tools/build.cs -- conformance --run --gate
dotnet run tools/build.cs -- cts cts && dotnet run tools/build.cs -- cts test-clojure
dotnet tool install -g dotnet-ilverify
dotnet run tools/build.cs -- image-set --twice --ilverify
```

性能基准不进 CI：共享 runner 的噪声远超 [cleat-151](tasks/cleat-151-noise-floor-and-full-rerun.md) 标定的地板，判定只在本机 / 111 上做（[designs/29 §9](designs/29-usability-and-engineering.md)）。

## 与 Clojure 的偏离

cleat 不模拟 Java 类库：`java.*` 类、`.getMessage` / `.length` 一类的 Java 成员名没有对应物，用 CLR 的写法（`.Message`、`.Length`、`Int64/MaxValue`、`clojure.clr.io`）。
JVM 异常类名有一张别名表（`ClassCastException` → `InvalidCastException` 等）。数值按 CLR 的规则，但整数装箱后一律是 `Int64`、浮点是 `Double`（`(class (int 1))`、`(class (byte 1))` 都是 `Int64`；`Int32`、`Byte` 等窄类型只出现在原始局部、数组与宿主方法签名里，偏离表的 integer-box-model 一行）；`(str true)` 是 `"True"`。
`*warn-on-reflection*` 为真时，分析期决议不了的宿主调用按 ClojureCLR 的措辞打印反射警告。细节见 [designs/03 §4](designs/03-language-core.md) 与偏离表。
实验开关见下节"已知限制"。

## 已知限制

- **Java 类库不模拟**（上一节）：移植 JVM 的库要把 Java 成员名、`java.*` 类换成 CLR 的写法；不做兼容垫片（[cleat-168](tasks/cleat-168-jvm-compat-evaluation.md) 取消）。
  ClojureCLR 的库通常能直接用，少数要改：直接调 ClojureCLR 编译器内部（`clojure.lang.Compiler`、`Compiler$LocalBinding`）的。
  `(by-ref x)` 实参（CLR 的 ref / out 形参）支持（[cleat-183](tasks/cleat-183-by-ref-arguments.md)）：x 须是局部（let / loop / fn 形参、catch 绑定、deftype 的可变字段）。
- **整数装箱为 Int64、浮点为 Double**（上一节）：`(instance? Byte x)` 之类对装箱值不成立。
- **栈跟踪少帧**：.NET 的栈跟踪不含被 JIT 内联的帧，小函数的帧可能不出现；`-g`（或 `CLEAT_NO_INLINE`）保留物理栈帧，调试时用。
- **core.async**：`go` 块里不能用 `await`（只在 `^:async` fn 里直接有效，报清楚的错误）；`proxy` / `gen-class` 可以（[cleat-184](tasks/cleat-184-proxy-in-go-blocks.md)）。
- **spec**：加载 clojure.spec.alpha 后宏展开要过 spec 检查，编译约慢 24%（`CLOJURE_SPEC_SKIP_MACROS=true` 关）；生成器要另加 test.check 包。
- **启动**：本机 `-e nil` 约 0.66 s、REPL 到首个提示符约 0.83 s（[cleat-181](tasks/cleat-181-csharp-compiler-default.md)）；
  REPL 的 `clojure.repl.deps` 加载失败尝试仍占一截。ReadyToRun 只作 opt-in（`publish --r2r`），默认产物是纯 IL。
- **实验开关**：`CLEAT_OPT_ACCINLINE=1`（小访问器跨编译单元内联）默认关、不承诺行为稳定；全部开关见 [designs/README 的附表](designs/README.md#附表开关与环境变量清单-cleat-164)。
- **LLVM 后端**搁置，只有 CIL。

## 许可

本仓库 Apache-2.0（[LICENSE](LICENSE)）。几部分改写自其他项目并保留各自的许可：`src/Cleat.Core/clojure.lang/` 与 `src/clojure/` 来自 ClojureCLR / Clojure（EPL-1.0），
`packages/nanopass/` 是 nanopass 框架的移植（MIT），测试资产来自 jolt、jank、ClojureCLR。全部列在 [NOTICE](NOTICE)。

## 文档

- [designs/README.md](designs/README.md)：设计文档索引；现行编译器是 [30 C# 编译器](designs/30-csharp-compiler.md)，架构见 [00](designs/00-overview.md)、运行时 [02](designs/02-runtime.md)、测试 [09](designs/09-testing.md)；
  编译器 [04](designs/04-compiler.md)、CIL 后端 [05](designs/05-cil-backend.md)、自举 [08](designs/08-bootstrap.md)、[19 Clojure 自寄宿](designs/19-clojure-self-hosting.md) 是历史。
- [docs/boot.md](docs/boot.md)：镜像怎样生成与验证。
- [TASK.md](TASK.md)：任务与里程碑索引（M0 ~ M20 已完成，LLVM 后端搁置）；用户可见的变化见 [CHANGELOG.md](CHANGELOG.md)。
- [packages/nanopass/README.md](packages/nanopass/README.md)：`cleat.nanopass` 用户文档。
