# cleat-091 自举不动点与 Lcil 文本种子

- 里程碑：M8（阶段 C）　依赖：cleat-090　状态：完成
- 设计依据：[19 D7、G4](../designs/19-clojure-self-hosting.md)

## 目标
Clojure 编译器编自己与 core，达到逐字节不动点；Lcil 文本种子成为信任根；Clojure 编译器成为默认。

## 工作内容
1. stage1（Scheme 编译器编出的 cleat.compiler.dll + cleat.boot.dll）编出 stage2，stage2 编出 stage3；stage2 == stage3（Lcil 与 dll 逐字节）。
2. 种子：`src/boot/seed/*.lcil.gz`；`Cleat.Emit` 读 gzip；记录种子体积与从种子构建的耗时。
3. 构建流程：`Cleat.Host.csproj` 的目标改为"种子 → stage1 → 编 boot / compiler"，不再需要 Scheme 镜像与 stage0；`tools/build.cs seed` 重生成种子并验证不动点；测试 `Bootstrap` 改为验证 Clojure 不动点。
4. 默认编译器切到 Clojure（`CLEAT_COMPILER=scheme` 暂时保留作对照，cleat-093 删）。
5. 运行时契约两步法写进 designs/08 的新版。

## 验收标准
- 从干净仓库（只有种子与源码）构建出的镜像与 stage3 相同；全量测试在默认模式下通过。

## 结论（2026-10-15）

完成。

**自举链**（`tools/build.cs selfhost [目录]`）：正在运行的编译器（`cleat.compiler.*` / `cleat.nanopass.*`）不能在同一进程里重新加载自己的源码编自己
（记录类型与接口被重定义，新旧代码混用：`CljFnName` 的类变了而旧的谓词还在用），所以每一级先把源码改名成 `cleat.compiler0.*` / `cleat.nanopass0.*`
（`cleat --rename-compiler-sources`，`DriverOps.RenameCompilerSources`）编成工具编译器，再由工具（`CLEAT_COMPILER_NS=cleat.compiler0`）编真正的
`cleat.compiler`。工具以下一级镜像为目标（`--compile-package … --target-image 镜像.dll`，包的镜像核对用它的 id）、运行在下一级镜像上编编译器，
编译器包因此只取决于镜像与源码（直接链接与内联看运行着的镜像的接口文件）。stage2 与 stage3 的镜像（dll、Lcil、.ifc、.inl）与编译器包（dll、Lcil）
逐字节相同（约 3 分钟）。

**种子**（`tools/build.cs clojure-seed`，`src/boot/seed/`，6 个 gzip 共 1979 KiB）：stage3 的 `cleat.boot.lcil` / `.ifc` / `.inl` 与 `cleat.compiler.lcil`
（嵌入资源 `cleat.ifc` / `cleat.inl` 从 dll 取出放在旁边，Lcil 里的资源路径改成相对路径）。`cleat-emit --seed 目录 -o 目录 [-r 目录]` 解压并逐个发射，
发射出的两个 dll 与 stage3 逐字节相同。镜像里不再有构建机器的绝对路径：编进镜像或包的文件 `*file*` 绑成加载名（`clojure/core.clj`，同 JVM 的 `:file`）。
发射器改动：相对资源路径相对于 Lcil 文件；`"类型, 程序集"` 的程序集是本程序集自己时指本单元的类型（从种子发射时没有已加载的同名镜像可找），
本单元的类型先于跨实例的静态缓存（进程里运行着的上一级同名镜像）。

**构建**（`Cleat.Host.csproj`）：`EmitSeedStage1`（种子 → `obj/seed-stage1/`）→ `GenerateBootImage`（stage1 在 stage1 镜像上 `--compile-boot`）→
`GenerateCompilerPackage`（改名的工具编译器在新镜像上编）；不再需要 Scheme 镜像与 stage0（它们的目标暂留给对照模式，cleat-093 删）。
从种子构建出的 `src/boot/cleat.boot.dll` 与 `cleat.compiler.dll` 与 stage1 逐字节相同（Host 测试 `ClojureBootstrapTests.BuildFromTheSeedReproducesTheSeed`）。
首次 `dotnet build` 约 3 分钟（发射 stage1 几秒、镜像 27 s、工具 26 s、编译器 26 s）。

**默认切换**：`CLEAT_COMPILER` 缺省为 `clojure`；镜像旁有编译器包时不装 Scheme 镜像与 Scheme 驱动（启动 Scheme image 0 ms，驱动 86 ~ 127 ms，
镜像约 1 s）。`--no-boot`、镜像旁没有编译器、开发时编译器源码比包新时照旧装 Scheme 镜像，Scheme 驱动先跑、Clojure 编译器从源码加载后接手。
`CLEAT_COMPILER=scheme` 作对照保留到 cleat-093。tools.build 的 uber 在没有 Scheme 镜像时也能打包（带上 `cleat.compiler.dll`）；Cleat.Cli 的工具包带上编译器包。

**运行时契约的两步法**写进 designs/08 §0：改 Cleat.Core / Cleat.Scheme 的方法签名或发射器时先保留旧入口让旧种子能发射，`clojure-seed` 重生成种子后再删。

**验收**：linux01 上默认模式 Core / Scheme / Host 全量测试通过；`CLEAT_COMPILER=scheme` 下 Host 测试通过（见提交前的运行）。
