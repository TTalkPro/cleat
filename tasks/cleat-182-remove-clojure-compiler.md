# cleat-182 删除 Clojure 编译器、种子与自举、Lcil

- 里程碑：M20　依赖：cleat-181　状态：完成（2026-11-05）
- 设计依据：[30 §1 阶段 E、§2 D7 / D9](../designs/30-csharp-compiler.md)

## 工作内容

1. 删除 `src/clojure/cleat/compiler/`、`CLEAT_COMPILER=clj` 路径与 `CLEAT_COMPILER_NS` / compiler0 改名机制（`DriverOps.RenameCompilerSources`）。
2. 删除种子与自举：`src/boot/seed/`、`tools/build.cs` 的 `selfhost` / `clojure-seed` / `compile-compiler` / `stage1`、
   `Cleat.Host.csproj` 的 `EmitSeedStage1` / `GenerateCompilerPackage`、`cleat.compiler.dll` 编译器包、`ClojureBootstrapTests`
   （换成"构建两次逐字节相同"的确定性测试）。
3. 删除 Lcil：`Cleat.Emit` 的 `LcilParser` / `SExpr` / `Model` / `Program`（`cleat-emit` 命令行）与 `LCIL.md`；`LcilParserTests`、以 Lcil 为输入的 `EmitterTests`；
   `CLEAT_DUMP_LCIL` / `CLEAT_DUMP_EVAL_LCIL`。仍被 C# 编译器使用的部分（如果 174 时没搬完）并入 `Cleat.Compiler.Emit`，`Cleat.Emit` 项目整体删除。
4. nanopass 移到 `packages/nanopass`（包程序集），`NanopassTests` 改为包测试；只为编译器存在的测试（`compiler/languages_test`、`pilot_test` 等）删除。
5. CI（cleat-162）：种子守卫换成确定性守卫（G5）。
6. 文档：designs 00 / 04 / 05 / 08 / 19 开头加现状注记（04 / 05 / 08 / 19 标为历史，现行见 30）；designs/README 首段与环境变量 / 开关清单；
   README、CHANGELOG。

## 验收

- 仓库里 `lcil`（不区分大小写）只出现在历史文档与 tasks 中；`src/` 下没有 Clojure 编译器源码。
- 全新克隆后 `dotnet build` 一步得到镜像与全部包；G2 / G3 / G5 全过。

## 181 之后的现状（2026-11-05）

- `dotnet build` 已不用种子（`EmitSeedStage1` 已删），镜像由 C# 编译器从源码两步编出；`cleat.compiler.dll` 由 C# 编译器编（`GenerateCompilerPackage` 还在，删 Clojure 编译器时一起删）。
- 确定性测试已有：`ClojureBootstrapTests.BuildImageIsAFixedPointOfTheCsCompiler`（删 `ClojureBootstrapTests` 时把它挪出来留下）。
- `selfhost` 已固定 `CLEAT_COMPILER=clj`；从 C# 编的镜像出发的 stage3 与种子流水线的产物逐字节相同，删种子不丢任何东西。
- 宿主的 `--no-boot` 已恢复（tools/build.cs 的 `stage1` 现在可用）；从零模式的两处让步（前向引用 declare、inliner 失败退回）在 `FormAnalyzer.DeclareCoreForwardRefs`。

## 实施记录（2026-11-05）

### 1. 删除

- Clojure 编译器 `src/clojure/cleat/compiler/`（10 个文件）；`Startup` 只剩 C# 编译器：`CLEAT_COMPILER` / `CLEAT_COMPILER_NS` 与包程序集
  `cleat.compiler.dll` 的登记、`CompilerFromPackage` 删除，有没有镜像都登记惰性入口（`Startup.CompilerKind` 恒为 `cs`，CLEAT_TIME 的格式不变）。
  C# 编译器里借用 Clojure 分析器的 `ClojureFrontEnd` 与 `CLEAT_CS_ANALYZER` 删除；G1 差分用的 `DiffSupport.cs`、`tools/cs-diff.clj` 删除。
- 种子 `src/boot/seed/`；`tools/build.cs` 的 `stage1` / `selfhost` / `clojure-seed` / `compile-compiler`；`Cleat.Host.csproj` 的 `GenerateCompilerPackage`
  （`GenerateBootImage` 不再设 `CLEAT_COMPILER`）；宿主的 `--rename-compiler-sources` 与 `DriverOps.RenameCompilerSources`。
- 发射器项目 `src/Cleat.Emit`（文本格式、解析器、`cleat-emit` 命令行、`LCIL.md`）整个删除——C# 编译器早已不引用它；`Cleat.Hosting` 改为引用
  `Cleat.Compiler`（把编译器带到嵌入应用的输出目录），`Cleat.Runtime` 包、tool 包、`publish`、`uber` 不再带 `cleat-emit.dll` / `cleat.compiler.dll`。
- 程序集名 `Cleat.Compiler.CSharp` 改回 `Cleat.Compiler`（171 的避让理由随编译器包消失）；R2R 的 `ShouldR2R` 按前缀，181 的评估结论不变。
- 源码注释里的 Lcil 措辞改掉（Core 的 7 处、`ConstantEncoder`、`.editorconfig`、`.gitignore`、`NOTICE`、README）；验收的 grep：
  git 跟踪 + 未跟踪未忽略的文件里，`lcil`（不区分大小写）只在 `designs/`、`notes/`、`tasks/`、`TASK.md`、`CHANGELOG.md` 出现。

### 2. 测试

- Core：`LcilParserTests`、`EmitterTests`、`FaslEncoderTests` 删除；`LoaderTests` / `InterfaceFileTests` 要的小模块 / 包程序集改用 Reflection.Emit
  直接生成（`TestAssemblies`）；常量表的编码与读回挪到 `Cleat.Compiler.Tests/ConstantEncoderTests`（经 C# 的 `ConstantEncoder`）。
- Host：`ClojureBootstrapTests` → `DeterminismTests`（`Category=Determinism`）：从零两步（`--no-boot` → stage1 → stage2）编出的镜像 / .ifc / .inl
  与 `dotnet build` 的 src/boot 逐字节相同、src/boot 是不动点、重编的镜像跑冒烟集、ILVerify。`NanopassTests` → `PackageTests.Nanopass`
  （源码一遍、编成 `Cleat.Nanopass.dll` 后再一遍）；`languages_test` / `pilot_test` 随编译器删除。
  `OptFlagTests` 改读 `Cleat.Core.Runtime.OptFlags`；`ClojureCompiledFormsRun` 改经 `eval`（改名 `CompiledFormsRun`），其余去掉 `package|cs` 两可的断言。
- 核对中发现：`CLEAT_NO_OPT` 的记号 `inline` 没有读取处——旧 codegen 删除前已只定义 `opt-inline?` 而不用，C# 编译器也不读。按用户决定删除该记号：
  开关表 24 → 23 个记号；`OptFlagTests` 改用 accinline / loopinl / num，并验证 `inline` 现在什么都不关；`OptFlagsTests` 的分词例子换成 loopinl。

### 3. nanopass

`src/clojure/cleat/nanopass*` → `packages/nanopass/src/cleat/`，上游测试翻译 `tests/nanopass/` → `packages/nanopass/test/cleat/`；新增 `cleat.edn`、
`build.clj`（`Cleat.Nanopass` 1.0.0，MIT）、`test/run.clj`（`*_test` / `*_tests` 命名空间）；README 改为用户库的说明。`image-set` 也编它。

### 4. CI 与工具

- PR：种子守卫 → 确定性守卫（`Category=Determinism`），并加 `Cleat.Compiler.Tests` 全量；nightly：`Category=Bootstrap` → `Determinism`，
  `ilverify src/boot/cleat.compiler.dll` → `image-set --twice --ilverify`。
- `tools/build.cs cs-image` 改名 `image-set`（旧名保留为别名）；`tools/alloc.cs`、`bench/m13_micro.clj`、`bench/startup/tracebuckets.py` 去掉对编译器包 / 发射器的引用。

### 5. 文档

designs 00 / 04 / 05 / 08 / 19 开头加现状注记（04 / 05 / 08 / 19 标为历史，现行见 30）；designs/README 首段、索引的状态、开关表（环境变量 23 → 19，
`tiered` 的读取处改为 `Cleat.Compiler`）；README 重写构建、布局、CI 节；`docs/boot-and-seed.md` → `docs/boot.md`；新建 `CHANGELOG.md`（M20 条目）；NOTICE。

### 6. 验收

- 全新克隆（工作树里跟踪 + 未跟踪未忽略的文件复制到空目录）后 `dotnet build Cleat.slnx` 一步 15 秒，镜像 / .ifc / .inl 与现有 src/boot 的 SHA256 相同，宿主可用。
- G2：Core 114 / 114，Compiler 49 / 49，Host **286 / 286**（13 m 37 s；291 − 自举 4 − Nanopass 6 + Determinism 4 + 包测试 1）。
- G3 / G5：`image-set --twice --ilverify` 16 个文件两次逐字节相同，7 个程序集（镜像、随附库、tools 2、packages 3）ILVerify 全过；改动前后 src/boot 的镜像 SHA256 不变（57039f08…）。
- 未跟踪的 `src/boot-clj/`（181 的 Clojure 编译器对照镜像）已删除，`.gitignore` 里的条目一并去掉。
