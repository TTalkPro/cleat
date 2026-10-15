# cleat

Cleat — Clojure made fast to the CLR.

cleat 是 .NET 上的 Clojure 实现：运行时用 C# 写，编译器用 Clojure 自己写（nanopass 风格的多遍编译器），编出的代码是普通的 .NET 程序集。
编译器已经自举——它由自己编译，仓库里的信任根是一份 Lcil 文本种子，构建只依赖 .NET SDK。

- **语义以 JVM Clojure 1.12 为准**，用三套外部测试做符合性判据：jolt 的 JVM 取证语料、jank 的 clojure-test-suite、ClojureCLR 的 test_clojure。
- **不依赖 DLR**：互操作（反射调用、类型提示、静态决议）是运行时自己做的；数值语义按 CLR 的类型来（见下文"与 Clojure 的偏离"）。
- **真实的 .NET 类型**：`deftype` / `defrecord` / `gen-class` / `proxy` 生成的是普通类，C# / F# 可以直接 `new`、实现接口、作为库引用。
- **Clojure CLI 的工作方式**：`cleat.edn` 描述项目，依赖是 NuGet 包，`cleat -M` / `-X` / `-T` 与 `clj` 对应，tools.build、nREPL、neorepl 都已移植。
- 只支持 **net10.0**。CIL 是唯一的后端；LLVM 后端的设计保留（[designs/06](designs/06-llvm-backend.md)），实现搁置。

## 快速开始

```sh
git clone --recurse-submodules <repo>     # tests/clojure-test-suite 是子模块
cd cleat
dotnet build -c Release                   # 编 C# 项目，再从种子发射 stage1、生成 Clojure 镜像与编译器包（首次约 3 分钟）
dotnet test  -c Release --no-build        # Core + Host 两套测试（Host 套件约 20 分钟，含自举不动点与 ILVerify）

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
| `src/Cleat.Emit/` | Lcil 发射器：Lcil 文本（[LCIL.md](src/Cleat.Emit/LCIL.md)）→ Reflection.Emit → 内存程序集或 dll（作者先在自己的 IronScheme 分支里写的 IronScheme.Emit，移植过来） |
| `src/Cleat.Host/` | 宿主 `cleat`：命令行、REPL、`-M/-X/-T`、镜像与编译器的加载、`--compile-boot` / `--compile-package` |
| `src/Cleat.Hosting/` | 嵌入 API `Cleat.Core.Hosting.CleatRuntime`（Init / Eval / Require / Var，惰性启动）：C# / F# 程序宿主 cleat 时用；与命令行宿主共用 `Cleat.Core.Runtime.Startup` 启动（[designs/17](designs/17-lazy-runtime-startup.md)） |
| `src/Cleat.Runtime.Package/` | NuGet 包 `Cleat.Runtime` 的打包项目（无代码）：Core、Hosting、Emit 程序集加镜像、编译器包与随附库，给 C# / F# 项目引用 |
| `src/clojure/clojure/` | `clojure.core` 与随附命名空间（string、set、walk、edn、pprint、test、repl、main、zip、data、instant、clr.io …），改写自 Clojure / ClojureCLR（EPL） |
| `src/clojure/cleat/compiler/` | 编译器（Clojure）：`analyzer`（Clojure 形式 → L0）、`languages`（L0 ~ L3）、`loops` / `front` / `closures` 各遍、`codegen`（L3 → Lcil）、`pipeline`、`driver`（eval / load / compile 钩子） |
| `src/clojure/cleat/nanopass*` | `cleat.nanopass`：nanopass 框架的 Clojure 移植，编译器用它写（[用户文档](src/clojure/cleat/nanopass/README.md)，MIT） |
| `src/boot/` | `boot-order.edn`（镜像的加载顺序）、`seed/*.gz`（Lcil 文本种子，信任根，约 1.98 MB）；构建生成的 `cleat.boot.dll` / `cleat.compiler.dll` 不入库 |
| `packages/` | 移植的库：`tools.build`、`tools.namespace`、`nrepl`、`neorepl`，各自是 cleat 项目，打成 NuGet 包 |
| `tests/` | `Cleat.Core.Tests`、`Cleat.Host.Tests`（NUnit）；`conformance/`（jolt 语料与偏离表）、`clojure-test-suite/`（子模块）、`test_clojure/`、两份基线 `cts-baseline.edn` / `test-clojure-baseline.edn`、`nanopass/`（nanopass 的上游测试翻译） |
| `tools/` | `build.cs`（维护命令，见下）、`cts.clj`（两套测试的基线运行器）、`conformance.clj`（语料运行与门禁） |
| `designs/`、`tasks/`、`TASK.md` | 设计文档（[索引](designs/README.md)）、任务文件、任务与里程碑索引 |
| `bench/` | 基准集（[bench/README.md](bench/README.md)） |

## 构建与自举

编译器自己编译自己，`dotnet build` 每次都走完这条链（[designs/08 §0](designs/08-bootstrap.md)）：

```
src/boot/seed/*.gz ──cleat-emit --seed──▶ stage1（obj/seed-stage1 的镜像与编译器包）
stage1 在 stage1 镜像上 --compile-boot src/clojure ──▶ src/boot/cleat.boot.dll（Clojure 镜像）
改名的工具编译器在新镜像上编 src/clojure/cleat ──▶ src/boot/cleat.compiler.dll（编译器包）
```

改了 `src/clojure` 下的任何东西（core、编译器、nanopass）后，要重新生成种子再提交，Host 测试会核对种子发射出来的程序集与构建产物逐字节相同：

```sh
dotnet run tools/build.cs -- clojure-seed    # 自举两级到不动点（stage2 == stage3），把 stage3 写成 src/boot/seed/*.gz 并发射回来验证
dotnet run tools/build.cs -- selfhost        # 只验证不动点，不写种子
```

`tools/build.cs` 的其他命令：`cts cts|test-clojure [--write]`、`conformance --run [--gate]`、`bench`、`compile-libs`、`compile-compiler`、`compile-tools`、`pack`、`publish`（自包含发布）、`ilverify`；
每个命令的说明在 [tools/build.cs](tools/build.cs) 开头的注释里。

## 测试与基线

| 套件 | 规模（2026-10-15） | 怎么跑 |
|---|---|---|
| Cleat.Core.Tests / Cleat.Host.Tests | 117 / 264 | `dotnet test Cleat.slnx -c Release` |
| jolt 语料（`:common` 行） | 失败行全部登记在 [cleat-divergences.edn](tests/conformance/cleat-divergences.edn)（521 行，各带类别与原因） | `dotnet run tools/build.cs -- conformance --run --gate` |
| clojure-test-suite | 247 个命名空间，断言 6866 通过 / 31 失败 / 6 错误 | `cleat tools/cts.clj cts` |
| test_clojure（ClojureCLR） | 357 个测试，断言 3688 通过 / 18 失败 | `cleat tools/cts.clj test-clojure` |

后两套逐命名空间与基线比较，差异即失败；有意改变时用 `--write` 重写基线，失败的分类写在对应任务文件里（[cleat-097](tasks/cleat-097-refresh-baselines.md)）。

## 与 Clojure 的偏离

cleat 不模拟 Java 类库：`java.*` 类、`.getMessage` / `.length` 一类的 Java 成员名没有对应物，用 CLR 的写法（`.Message`、`.Length`、`Int64/MaxValue`、`clojure.clr.io`）。
JVM 异常类名有一张别名表（`ClassCastException` → `InvalidCastException` 等）。数值按 CLR 类型：`(int x)` 是 `Int32`、`(float x)` 是 `Single`、`(str true)` 是 `"True"`。
`*warn-on-reflection*` 为真时，分析期决议不了的宿主调用按 ClojureCLR 的措辞打印反射警告。细节见 [designs/03 §4](designs/03-language-core.md) 与偏离表。

## 许可

本仓库 Apache-2.0（[LICENSE](LICENSE)）。几部分改写自其他项目并保留各自的许可：`src/Cleat.Core/clojure.lang/` 与 `src/clojure/` 来自 ClojureCLR / Clojure（EPL-1.0），
`src/clojure/cleat/nanopass/` 是 nanopass 框架的移植（MIT），测试资产来自 jolt、jank、ClojureCLR。全部列在 [NOTICE](NOTICE)。

## 文档

- [designs/README.md](designs/README.md)：设计文档索引；现行方向是 [19 Clojure 自寄宿](designs/19-clojure-self-hosting.md)，架构见 [00](designs/00-overview.md)、运行时 [02](designs/02-runtime.md)、编译器 [04](designs/04-compiler.md)、CIL 后端 [05](designs/05-cil-backend.md)、自举 [08](designs/08-bootstrap.md)、测试 [09](designs/09-testing.md)。
- [TASK.md](TASK.md)：任务与里程碑索引（M0 ~ M9 已完成）。
- [src/clojure/cleat/nanopass/README.md](src/clojure/cleat/nanopass/README.md)：`cleat.nanopass` 用户文档。
