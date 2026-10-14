# cleat-093 删除 Scheme

- 里程碑：M8（阶段 E）　依赖：cleat-092　状态：完成
- 设计依据：[19 §1 阶段 E](../designs/19-clojure-self-hosting.md)

## 目标
仓库里不再有 Scheme：源码、程序集、测试、构建目标、文档引用。

## 工作内容
1. 删除 `src/scheme`、`src/Cleat.Scheme`、`src/Cleat.Stage0`、`Cleat.PrimGen` 的 Scheme 原语表部分（Clojure 内核表若仍需要则保留）、`src/boot/cleat.scheme.*`、`Cleat.Core/Expander`（保留 `LocalBinding` / `ExpanderException`）。
2. 测试：删 `tests/Cleat.Scheme.Tests`、`tests/scheme`（R6RS、Chez mats、Scheme 快照）；Core 测试去掉 Scheme / Stage0 引用；`SchemeInvisibilityTests` 改为"生成代码与诊断中没有 Scheme"之类仍有意义的检查或删除；Clojure 编译器的快照测试补齐。
3. 构建：csproj 目标、`tools/build.cs`（scheme-stage1、seed 旧义等）、`Cleat.slnx`、`PackageIndex.cs` 里的 `Cleat.Scheme.dll`、打包清单。
4. NOTICE / 许可：去掉随 Scheme 删除的条目（psyntax、nanopass-framework-scheme 快照、Chez mats、R6RS 套件、UCD 表若随之删除）；Cleat.Emit 等仍源自 IronScheme 的 C# 保留条目。
5. 文档：designs/13、14、15、08 旧版标为历史；00、04、README 改为现行架构；`CLEAT_SCHEME_*` 环境变量与 `CLEAT_COMPILER` 开关删除。

## 验收标准
- `git grep -i scheme` 只剩历史文档与许可说明；从干净仓库构建与全量测试通过。

## 结论（2026-10-15）

完成。仓库里不再有 Scheme：`git grep -i scheme` 只剩历史文档（designs/13、14、15、08 §1 ~ §8、notes/）、NOTICE 的来源说明、
"原版"注释里对已删实现的指代，以及 IronScheme 的出处标注。511 个文件删除，`Cleat.slnx` 只剩 Cleat.Core / Cleat.Emit / Cleat.Host / Cleat.Hosting
与两个测试项目。

**删除**：`src/scheme`（boot、psyntax、Scheme 编译器、nanopass、R6RS / Chez 测试）、`src/Cleat.Scheme`、`src/Cleat.Stage0`、`src/Cleat.PrimGen`
（`src/boot/primitives.edn` 与 `Primitives.ToEdn` 随之删）、`tests/Cleat.Scheme.Tests`、`tests/scheme`（含编译器快照）、C# 展开器 `Expander.cs`
与 `Lcore`（只剩 `Cleat.Core.Expand` 的 `LocalBinding` / `ExpanderException`，文件改名 `Expand.cs`）、`Cleat.Emit/SchemeData.cs`、
`cleat.compiler.dump` / `shadow`、`tools/compiler-diff.clj`、`tools/gen-unicode-props.cs`、`tests/nanopass/cleat/test/scheme_data.clj`、
`ir-dump` / `scheme-stage1` / `seed` 命令、`CLEAT_COMPILER` / `--scheme-boot` / `CLEAT_SCHEME_BOOT` / `--no-boot`、
`CLEAT_SCHEME_NO_034` / `CLEAT_NO_070`（合为 `CLEAT_NO_OPT`）、偏离表里只对 Scheme 编译器成立的两条、`.gitignore` / `.editorconfig` 的 Scheme 项。

**宿主**（`Program.Start`）：登记原语 → `RT.Init` → 加载镜像、登记镜像旁的包（编译器包 `cleat.compiler.dll` 只要存在且不是正在编的就登记，
不再比较源码与包的时间——`dotnet build` 按输入增量重编）→ `require` 编译器驱动（`CLEAT_COMPILER_NS` 指定自举用的改名工具编译器）→
`install-hooks!`。`--compile-boot` / `--compile-package` 经 `DriverOps.CompileBootHook` / `CompilePackageHook`；没有镜像或编译器时报
"the compiler is not loaded"。`CLEAT_TIME` 的输出改为 `Clojure image N ms, compiler (package|source) N ms`。`Program.SchemeBootPath` /
`DriverLibraryDir`、`CleatOptions.SchemeBoot` 删去（tools.build 的 uber 与 compile-clj 改用 `ClojureBootPath` 与
`cleat.compiler.driver/compile-package`）。

**发射器**：`LcilRuntime` 只有 `clojure`；`EmitInterop` 只剩 Reflector / TypeResolver 一套规则；`FaslEncoder` 与 `Cleat.Core.Runtime.Fasl`
只保留 Clojure 的编码（标签值不变，Scheme 专有的标签删去不复用；表的外层标签改叫 `Table`）；`cstr` / `csym` / `clist` / `cvec` 作为别名保留。
LCIL.md 同步。

**运行时契约的两步法实例**：`clojure.core/special-symbol?` 原来调 `Cleat.Core.Expand.Expander/IsSpecial`——删掉 Expander 后旧种子发射出的
stage1 加载 clojure.pprint 时 TypeLoadException。按 08 §0 的两步法：先加 `Cleat.Core.Expand.SpecialForms`、保留 `Expander.IsSpecial` 的过渡壳，
core.clj 改用新入口，`clojure-seed` 重生成种子，再删过渡壳。

**测试**：`SchemeInvisibilityTests` → `ValueBoundaryTests`（边界本身仍是编译器契约；Scheme 对象的用例删去）；`CompilerDiffTests` →
`ClojureCompilerTests`（只剩编译器自己的运行测试）；`ClojureBootstrapTests` 改为跑 `tools/build.cs selfhost`（stage2 == stage3）、stage3 冒烟、
ILVerify、种子复现；`FaslTests` 只测 `Cleat.Core.Runtime.Fasl`；`PrimitivesTests` 去掉 primitives.edn；错误黄金文件的 usage 行更新；
`ErrorGoldenTests` 的栈过滤测试改查编译器帧。

**许可**：`src/Cleat.Emit/LICENSE-BSD.txt`（IronScheme）与 `src/clojure/cleat/nanopass/LICENSE-MIT.txt`（nanopass）随源码放置；NOTICE 改写。

**文档**：designs/00 的目录布局改为现行；08 §0 的宿主行为更新；13 / 14 标为历史；17、18、README 索引同步。

**自举**：种子重生成（1.76 MB），`selfhost` 不动点（stage2 == stage3，147 s），`dotnet build` 从种子出的镜像与编译器包与 stage1 逐字节相同。

