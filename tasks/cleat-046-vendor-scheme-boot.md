# cleat-046 纳入 Scheme 元层并在本仓库自举

- 里程碑：M2′　依赖：cleat-045　状态：完成
- 设计依据：[15 §1、§6](../designs/15-scheme-core-vendoring.md)、IronScheme `designs/05-bootstrap.md`

## 目标
不改任何 Scheme 源码，证明"种子 → stage0 → Scheme 镜像 → 不动点"在本仓库成立。

## 工作内容
1. `src/scheme/boot/`（boot 库、展开器、`primitives.ss`、`boot-seed.core`、构建脚本）、`src/scheme/compiler/`（去 `async.sls`，`targets.sls` 类名改 `Cleat.Scheme.*`）、`src/scheme/nanopass/`（原样）。
2. `tools/build.cs`：`scheme-stage1` / `seed` / `ilverify`；`Cleat.Host.csproj`（或单独的 `Cleat.Boot` 项目）的 `GenerateSchemeBootImage` 目标。
3. `tests/Cleat.Scheme.Tests` 的 `Bootstrap` 类别：stage0 → stage1 → stage2 → stage3，Lcil 与 dll 不动点、ILVerify。
4. 编译器快照测试（IronScheme `tests/compiler/`）纳入，作为"nanopass 未受影响"的基线。

## 验收标准
- 不动点成立；快照测试通过；记录 Phase A 耗时。

## 结论（2026-10-10）

来源：IronScheme 仓库（`/home/david/workspace/IronScheme`）`src/scheme/`，与 cleat-045 的 C# 一侧同一提交 `26386793ee27f478a2e69ccdc17bc10a3a134944` **及其工作树**（iron-034 的未提交改动：`codegen.sls`、`parse.sls`、`compiler.sls`、`async.sls`、种子、快照等；cleat-045 纳入的 `BootInterface.cs` / `InlineArith.cs` 也只在工作树里，两侧一致）。不改任何 Scheme 语义；来源与布局见 `src/scheme/README.md`，`NOTICE` 加 `src/scheme/`（BSD + psyntax 的 MIT 式声明）与 `src/scheme/nanopass/`（MIT）两条。

### 纳入了什么

| 目标 | 来源 | 规模 | 说明 |
|---|---|---|---|
| `src/scheme/boot/` | `src/scheme/boot/` | 37 个 `.sls` 14,686 行（展开器 6,008）；`primitives.ss` 1,100 行（PrimGen 生成）；`boot-seed.core` 1,562,847 字节 / 18,777 行；两个构建脚本 4,329 行 | 全部原样（不含 `*.dll` / `*.lcil` / `*.ifc`） |
| `src/scheme/lib/ironscheme/compiler/`、`compiler.sls` | `lib/ironscheme/compiler/` | 11 个文件 3,623 行 | **不在** `src/scheme/compiler/`：psyntax 的文件定位器把 `(ironscheme compiler parse)` 固定映射到 `<搜索目录>/ironscheme/compiler/parse.sls`，库名不改（cleat-054）就只能放这里。`async.sls`（536 行）不纳入；其中与 async 无关的 `lower-loops` / `reset-fresh-names!` / `loops-made` 及其工具（`kids`、`check-refs`、`jumpify`、`fresh`）一字不改地拆到新库 `loops.sls`（228 行，`(ironscheme compiler loops)`） |
| `src/scheme/lib/ironscheme/{clr,contracts,contracts-helper,integrable,typed}.sls`、`clr/helpers.sls`、`syntax/utils.sls`、`typed/{core,parsing,parsing-helper}.sls` | `lib/ironscheme/` | 10 个文件 704 行 | boot 源码、编译器与 nanopass 适配库导入的、不在镜像里的库（按 import 传递闭包算出：boot 的 `(ironscheme contracts)`、`(ironscheme typed)` 等，构建时从源码加载） |
| `src/scheme/lib/nanopass/implementation-helpers.ironscheme.sls` | `lib/nanopass/` | 209 行 | IronScheme 的覆盖适配库，搜索路径里先于框架找到 |
| `src/scheme/nanopass/` | 子模块 `lib/nanopass-framework-scheme`（`c14d3924`） | 54 个文件（代码 12,450 行，含 doc / tests） | 原样快照，不含 `.git`；`Copyright` 即 MIT 许可 |
| `tests/scheme/compiler/` | `src/scheme/tests/compiler/` | 8 个脚本 593 行 + 44 个快照文件 | 去 `async-snapshots.sps` 与 `async-*` 快照 |
| `tests/Cleat.Scheme.Tests/{BootstrapTests,SchemeScriptTests,SchemePaths}.cs` | `IronScheme.Tests/BootstrapTests.cs`、`TestRunner.cs`、`IronScheme.Core.Tests/Stage0ScriptTests.cs` | — | 见下 |

### 每一处文本改动（Scheme 一侧）

1. 类型名 `IronScheme.Core.*` → `Cleat.Scheme.*`：`targets.sls`（机器描述表，含 `async-frame` 条目改名并注明不存在）、`codegen.sls`（`static-proc-type`）、`compiler.sls`（`Fasl`、`ModuleLoader`）、`lib/ironscheme/typed/parsing-helper.sls`（`->` 过程类型 → `Cleat.Scheme.Procedure`，这是种子里 `IronScheme.Core.Procedure` 的来源）、构建脚本（`BootInterface`）、`boot-seed.core`（36 处 `IronScheme.Core.Procedure`，文本替换）、测试脚本与快照。保留：nanopass 适配库的 `clr-static-call IronScheme.Runtime.Builtins`（cleat-045 在 `TypeResolver` 里映射，镜像 Lcil 中仍是这个名字）以及 `"not an IronScheme compiled file"` 等消息字符串。
2. 发射器：`(clr-reference IronScheme.Emit)` → `(clr-reference cleat-emit)`，`IronScheme.Emit.LcilEmitter` → `Cleat.Emit.LcilEmitter`（`compiler.sls`、构建脚本、测试脚本）。
3. `codegen.sls` 的 Lcil 程序集头加 **`(runtime scheme)`**：cleat 的发射器缺省按 Clojure 运行时解释互操作指令与常量表（LCIL.md §2），Scheme 编译器的产物必须声明。快照 `.lcil` 同步加这一子句（`(references "Cleat.Scheme") (runtime scheme) (entry …)`），之后 44 个快照全部逐字相同。
4. 镜像名 `ironscheme.boot` → `cleat.scheme.boot`：`codegen.sls` 的 `boot-assembly-name`（接口文件里的类名带程序集名）、`compiler.sls` 的 `compile-bootfile` 缺省文件名、构建脚本的程序集名与 `.lcil` / `.dll` / `.ifc` 路径、C# `BootInterface.BootAssemblyName`。接口文件头 `(ironscheme-interface 1 …)` 不改（格式标记）。
5. 路径：构建脚本的库搜索路径 `("." "lib/" "lib/nanopass-framework-scheme/")` → `("." "lib/" "nanopass/")`，输出目录缺省 `boot` → `../boot`（即 `src/boot/`）；`primitives.ss` 由 PrimGen 改写到 `src/scheme/boot/`（编译器按 `boot/primitives.ss` 相对 `src/scheme` 读），`src/boot/primitives.ss` 删除，`PrimitiveTableTests` 跟着改；`snapshots.sps` 的快照目录改为 `../../tests/scheme/compiler/snapshots/`。
6. async：`compiler.sls` 去掉 `(ironscheme compiler async)` 与 `lower-async` 调用（`lower-async` 在没有 `async-lambda` 的单元上本就是恒等）；`parse.sls` 的两处 `async-lambda` 分派改为 `(no-async x)`，报 `syntax-violation "async-lambda is not supported in cleat"`——`codegen.sls` 的 async 状态机路径成为不可达的死代码，未删（cleat-047 / 后续清理）。`languages.sls` 的 `async-lambda` / `await` 节点保留。boot 源码里没有 `async-lambda` 的使用（只有展开器对该核心形式的定义）。

### 构建与工具

- `src/Cleat.Host/Cleat.Host.csproj` 的 **`GenerateSchemeBootImage`** 目标（`BeforeTargets=CopyFilesToOutputDirectory`，Inputs 为种子、构建脚本、原语表、`boot/**/*.sls`、`lib/**/*.sls`、`nanopass/**`、stage0 / Cleat.Scheme / Cleat.Core / cleat-emit 程序集，Outputs 为 `src/boot/cleat.scheme.boot.dll`；`-p:BuildBootImage=false` 跳过）。为保证 PrimGen 先重生成原语表，Host 对 PrimGen 加了 `ReferenceOutputAssembly=false` 的引用。实测首次 `dotnet build` 71 秒（含镜像），再次 4 秒（跳过）。
- `tools/build.cs`：`scheme-stage1 [outdir]`、`seed [--stage0]`（IronScheme 05 §5：`--stage0` 由 stage0 执行种子副本运行 `seed-buildscript.sps`，缺省 `stage0 --boot src/boot/cleat.scheme.boot.dll`）、`ilverify`（引用运行时目录、Cleat.Core、Cleat.Scheme、cleat-emit 与镜像；工具路径同 IronScheme）。原 `stage1` 留给 cleat-053（Clojure 镜像）。
- 宿主：没有新建 `Cleat.SchemeHost`。`Cleat.Stage0` 已有 `--boot <dll>` 模式（cleat-045 原样纳入）：加载镜像后以 `(load/program 脚本)` 运行，展开器的 `eval` 走镜像内编译器的 `compile-core`（镜像里该名字绑定到编译器库的位置，stage0 登记的同名符号不起作用），与 IronScheme.Host `--boot` 等价。命令行约定：`-I` 等选项在 `--boot` 之前。
- `.gitignore`：`src/boot/*.dll|*.lcil`、`**/*.ifc` 原已忽略；加 `src/scheme/boot/*.{dll,lcil,pdb}` 与 `tests/scheme/compiler/snapshots/*.actual`。

### 不动点（`tests/Cleat.Scheme.Tests` 的 `Bootstrap` 类别，6 个测试）

stage0（种子）→ stage1；`stage0 --boot stage1` → stage2；`stage0 --boot stage2` → stage3。

| | 结果 |
|---|---|
| stage1 == stage2 == stage3 | **成立**，dll 与 Lcil 逐字节相同（第一次运行即成立，没有需要修正的 vendoring 伪差异） |
| `cleat.scheme.boot.dll` sha256 | `319f61f1351c5fce324b72022500cb916fc7c606c4045a4d960138899da9a140`（1,887,232 字节；`dotnet build` 的产物、手工 scheme-stage1、三级自举全部相同） |
| `cleat.scheme.boot.lcil` sha256 | `d2e9a5852660d8dd3deb1e2a7ac6a2a807323a4ad3f326433ef068f815a360e9`（15,853,640 字符，64 个形式，26 个库进镜像，接口文件 1,679 个过程） |
| ILVerify | `All Classes and Methods in … cleat.scheme.boot.dll Verified.` |
| 种子不动点 | `seed --stage0` 与 `seed`（镜像）生成的种子与纳入的种子**逐字节相同**（sha256 `11692029…`）。第一次再生成时差 9 行：`typed/parsing-helper.sls` 还写着 `IronScheme.Core.Procedure`——这就是种子里那个名字的来源，改名后一致 |
| 与 IronScheme 自己的镜像比对 | 把 IronScheme 工作树的 `ironscheme.boot.lcil` 做同样改名并中和 gensym 计数、常量下标、模块字段编号后与 cleat 的 Lcil 按行集合比较：cleat 独有的只有 82 行，全是提到 `loops.sls` 的源位置字符串（nanopass 错误消息）与 `"no-async"`；IronScheme 独有的全是 `lower-async` 的代码。即除去 async 与上面列的改名，产物没有别的差别 |

耗时（本机，Release）：Phase A 在 stage0 上 **56 秒**（种子 0.25 秒；展开 boot 源码 4.9 秒；展开 nanopass 与编译器 16.9 秒；编译 8.5 秒；写 Lcil 6.1 秒；发射 1.3 秒）；stage2 / stage3 在镜像上各 **21 秒**（展开 boot 1.6 秒、展开 nanopass 与编译器 6.4 秒、编译 1.9 秒、写 0.35 秒、发射 1.1 秒）；`seed --stage0` 30 秒，`seed` 14 秒。与 IronScheme 记录（45 秒 / 16 秒）同量级。

### 编译器快照测试（`SchemeScript` 类别，8 个测试）

`tests/scheme/compiler/*.sps` 在镜像上运行（`stage0 --boot`），每个 2 ~ 13 秒：`snapshots.sps` **44 个快照比较全部一致、0 个新建**（11 个用例 × l0 / l2 / l3 / lcil，含 iron-034 的 inline-arith、typed-loop、let-lambda-labels 等），确定性检查通过；`compile-small` 41/41；`inline-templates` 156/156；`constants-roundtrip` 46/46；`parse-seed`、`front-seed`（种子全部形式过 parse-core / 前中端）；`slow-compile-seed`（编译整个种子，Slow 类别）。例外 `compile-library.sps` 在 stage0 + 种子上运行（18 秒）：它把编译好的 `(boot lists)` 装进进程覆盖解释执行的版本，在镜像上没有意义（镜像里的 `filter` 已是编译版，位置符号不同），与 IronScheme 的 `Stage0ScriptTests` 一致。

### 验证

`dotnet test Cleat.slnx`：`Cleat.Core.Tests` 129 通过；`Cleat.Scheme.Tests` **222 通过**（原 208 + Bootstrap 6 + SchemeScript 8），该程序集 3 分 52 秒（Bootstrap 约 2 分钟，脚本约 1.5 分钟）；含构建与镜像生成总计 316 秒。`--filter "TestCategory!=Bootstrap&TestCategory!=SchemeScript"` 可跳过慢的部分。

### 未做 / 留给后续

- 库名仍是 `(ironscheme …)`、`(boot …)`，文件名 `ironscheme-buildscript-nano.sps` 等也未改（cleat-054）。
- `codegen.sls` 的 async 死代码、`languages.sls` 的 async 节点、`targets.sls` 的 `async-frame` / `escape-continuation` / `continuation-exception` 条目：cleat-047 改异常模型时一起清。
- 原语表里 IronScheme 有而 cleat 没有的只有 `await`、`task-*`、`ffi-*`、`pinvoke-call-internal`（cleat-045 删的运行时），boot 源码不引用它们；`(ironscheme clr)` 的 `pinvoke-call` 宏保留但不可用。
- `src/scheme/nanopass/` 带了上游的 `doc/`（LaTeX）与 `tests/`，占 54 个文件中的 23 个；要瘦身可以删。
- IronScheme 的 `Stage0ScriptTests` 是全部脚本在 stage0 上跑；这里只有 `compile-library.sps` 在 stage0 上，其余在镜像上（快 3 ~ 5 倍）。想要 stage0 版本只需在 `SchemeScriptTests.s_onStage0` 里加名字。
