# cleat-186 随附库的重编：dotnet build 不重编 Cleat.Clojure.Libs，publish 的顺序反了

- 里程碑：M21　依赖：无　状态：完成（2026-11-09）
- 来源：[cleat-183](cleat-183-by-ref-arguments.md)（169 的修复之后 src/boot 里出现新镜像配旧随附库）

## 问题

1. `dotnet build` 的 `GenerateBootImage`（`src/Cleat.Host/Cleat.Host.csproj`）只编 `src/boot/cleat.boot.dll`；随附库的包程序集
   `src/boot/Cleat.Clojure.Libs.dll` 只有 `tools/build.cs compile-libs` / `publish` / `pack`（以及调它们的 Host 测试）会重编。
   编译器或镜像改了之后，src/boot 里可能是新镜像配对着旧编译器编的随附库：169 修复后的 r7rs 原型树就带着修复前的 Libs；
   镜像 id 变了时旧 Libs 加载会报 StaleModuleException。
2. `tools/build.cs publish` 先 `CompileLibs` 再 `dotnet build solution`——后者可能重编镜像，随附库就对着旧镜像。

## 工作内容

1. `GenerateBootImage` 之后增量编随附库（输入：src/clojure 的随附库源码、镜像、`Cleat.Compiler.dll` / `Cleat.Core.dll`；输出 Libs），或在加载时
   发现 Libs 的镜像 id 不符时忽略它并提示（二选一，写明理由）。tools 目录（`src/boot/tools`）同样处理。
2. `publish` 改为先 `dotnet build` 再 `CompileLibs`；检查 `pack` / `r2r` 的同类顺序。
3. 测试：改编译器（或换镜像）后 `dotnet build`，Libs 跟着更新；publish 产物里镜像与 Libs 的 id 一致。

## 结果（2026-11-09）

### 第 1 条的选择：加载时按镜像 id 判断（不在 dotnet build 里重编随附库）

- 包程序集新增程序集级特性 `Cleat.Core.CleatBootImageAttribute(镜像 id)`（`CsDriverPersist.CompilePackageUnit`，与入口里 `CheckBootImage` 的 id 相同）；
  `PackageIndex.ReadBootImageId` 按元数据读（不加载程序集）。
- 宿主启动（`Startup.BuiltForRunningImage`）：镜像旁的 `Cleat.Clojure.Libs.dll` 与 `tools/*.dll` 只在记的 id 与正在运行的镜像相同时登记；不同则不用，
  没有源码可退回时（随附库在非开发布局、tools）在标准错误说一句（指明重编的命令）。没有这个特性的旧程序集（186 之前编的）仍按原来的文件时间判断。
- 理由：
  1. 文件时间不可靠——复制、检出、rsync、确定性重编（内容相同、时间更新，反而让有效的随附库被弃用）都会改它；镜像 id 是内容决定的。
  2. 不在 `dotnet build` 里重编：随附库的命名空间要先逐个试加载才能定（`LoadableLibNamespaces`，boot-order 里 `:boot false` 且能从源码加载的），
     在解决方案的 MSBuild 里跑 `tools/build.cs` 是嵌套构建；开发布局总有源码可退回，随附库只由 `compile-libs` / `publish` / `pack` 产出，
     这三条现在都在镜像定下来之后编。
  3. 判断在加载处，挡住所有来源的不一致（不只是 dotnet build），不会再到用时才报 StaleModuleException。
- 同一个镜像 id、只是旧编译器编的随附库仍会被用：那是对这个镜像有效的产物（与任何包程序集一样），不算不一致。

### 第 2 条

`tools/build.cs publish` 改为先 `dotnet build` 再 `CompileLibs`；`pack` 本来就是先 `BuildCSharp`（不重编镜像）再 `CompileLibs` / `CompileToolLibs`，
`r2r` 不编随附库。

### 测试

`HostProcessTests.BundledLibsAreUsedOnlyWithTheImageTheyWereCompiledAgainst`：另编一个 id 不同的镜像（`CLEAT_NO_INLINE` 的镜像）与对着它编的随附库；
对着本镜像编、文件时间比镜像早一小时的随附库照样用（`require clojure.test.tap` 不需要源码）；对着别的镜像编的不用，标准错误说明原因，
不出现 StaleModuleException（原来的文件时间判断下它会被登记，用到时报错）。镜像 `cleat.boot.dll` 不变（特性只加在包程序集上）。

### 门禁

Core 116 / Compiler 50 / 全量 Host 293（含新测试）；`image-set --twice --ilverify`（包程序集带新特性，两次逐字节相同）、conformance `--gate`、
CTS / test_clojure 基线 OK。镜像 `cleat.boot.dll` 不变。
