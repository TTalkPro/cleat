# Changelog

用户可见的变化，按里程碑记（任务与里程碑索引 [TASK.md](TASK.md)，每个任务的细节在 `tasks/` 下）。版本号：运行时包 `Cleat.Runtime` / 工具包 `Cleat.Cli`
（`CoreInfo.Version`）；各包的版本见 [notes/packages.md](notes/packages.md)。

## 1.2.0（2026-11-10）

M10 ~ M23（性能、可用性与工程、编译器改为 C#、互操作补全、兼容性收口）。

### 从 1.1.0 升级

- **包程序集要用 1.2.0 重编**：包程序集绑定编它时的 Clojure 镜像（镜像 id），1.1.0（及任何别的版本）编的包在 1.2.0 上加载时抛
  `StaleModuleException`（"module was compiled against boot image …, but the running image is … (cleat 1.2.0); recompile it …"）。
  自己的包：`cleat -T:build install` 重新打包；cleat 发布的包换成为 1.2.0 重编的版本：`Cleat.Nrepl` 1.8.4、`Cleat.Neorepl` 0.1.5、
  `Cleat.ToolsNamespace` 1.5.5、`Cleat.ToolsBuild` 0.10.21（新包见 [notes/packages.md](notes/packages.md)）。
- **包对运行时的依赖改为精确版本**：tools.build 的 `pack` 现在写 `Cleat.Runtime [1.2.0]`（原来是下限 `1.1.0`，NuGet 会解析到更新的运行时、错误拖到运行期）；
  混用为不同运行时编的包时 `dotnet restore` 直接报版本冲突。
- **删除**（M20）：Clojure 写的编译器与 `cleat.compiler.dll`、文本种子、发射器 `cleat-emit` 与它的 S 表达式格式、`CLEAT_COMPILER` / `CLEAT_COMPILER_NS` /
  `CLEAT_CS_ANALYZER` / `CLEAT_DUMP_LCIL` 等开关、`tools/build.cs` 的 `stage1` / `selfhost` / `clojure-seed` / `compile-compiler`；`cs-image` 改名 `image-set`（旧名仍可用）。
  `cleat.nanopass` 移到 `packages/nanopass`（普通的用户库）。
- **行为变化**（对齐 JVM，可能影响依赖旧行为的代码）：反射找不到成员的消息改为 `No matching member m taking n args …`；reify 的类名带外层 fn；
  空的 `[]` / `#{}` 字面量是共享的 `EMPTY`；直接调 `macroexpand` 时宏的元数错误是 `ArityException`；协议重定义后去掉的方法在调用点就报错；
  Var 的 `:tag`、静态属性 / 字段、loop 局部参与静态决议（类型不符时在调用处 InvalidCastException，原来走反射）。
- 要求不变：.NET 10 SDK / 运行时。


### M22 发布（cleat-187 ~ 188）

- 包对 `Cleat.Runtime` 写精确版本；StaleModuleException 的消息带运行时版本（见上节）。
- **包里带 Clojure 源码**（cleat-187）：Cleat.Cli 与 Cleat.Runtime 把 clojure.core 等的源码放在镜像旁的 `clojure-src/`，装好的 cleat 里
  `(source when)`、nREPL 补全的文件位置可用（原来包里没有源码）。
- 修正（cleat-187）：test.check 用 tools.build 打包失败（上游一个只给 ClojureScript 的文件命名空间与路径不符）；`:warn-on-boxed` 对 let / loop 局部误报。

### M23 兼容性收口（cleat-189 ~ 193）

- **`clojure.lang.Compiler` 门面**（cleat-189）：`Compiler+CompilerException`（cleat 的编译错误就是它，`catch` 接得住）、`munge` / `demunge` / `CHAR_MAP`、
  `eval` / `load` / `loadFile` / `macroexpand1`、`SOURCE_PATH` / `SOURCE` 等 Var；分析器内部不提供（[designs/03 §4.2](designs/03-language-core.md)）。
  test_clojure 的 control、errors、evaluation、ns-libs、special 跑起来（断言 3877 → 4223 通过）。
- 直接调 `macroexpand` / `macroexpand-1` 时宏的元数错误原样抛 `ArityException`；`(quote 1 2 3)` 的原因带 `{:form …}`。
- 反射调用找不到成员的消息改为 `No matching member m taking n args found for type T: …`（与 ClojureCLR 开头相同）。
- `case` 各分支类型相同时有静态类型（少一次反射）；`*warn-on-reflection*` 下 int 测试的 `case` 被测值不是原始类型时报性能警告（同 JVM）；
  `let` 绑定到另一个局部时沿用它的类型提示。
- **`clojure.clr.api.Clojure`**（cleat-190）：与 ClojureCLR 相同的 C# 嵌入入口 `Clojure.var(ns, name)` / `Clojure.var("ns/name")` / `Clojure.read(s)`，
  不必初始化（惰性启动）；宿主里的 Clojure 代码也能 import。`CleatRuntime` 照旧。
- **新包 data.generators、test.generative**（cleat-191，取自 ClojureCLR 的 clr.data.generators 1.1.0、clr.test.generative 1.1.2）；
  **`clojure.clr.process`**（ClojureCLR 1.12 的进程 API，随附库）——修了上游的三处：实参逐个经 `ArgumentList`（原来拼成一串，带空格的实参被拆开）、
  `:dir` 生效、`exec` 缺省收集 stdout。
- 修正（cleat-191）：含 `#inst` / `#uuid` 字面量的代码编不成包程序集（M20 的 C# 常量编码器漏了这两种）；空的向量 / 集合字面量是共享的 `EMPTY`
  （同 JVM：`(identical? [] [])` 为真，`(= (sorted-set-by compare #{}) #{#{}})` 不再因比较集合抛异常而为假）；
  宿主方法重载在 long / ulong 之间按 C# 规则取有符号的（`(inc UInt32/MaxValue)` 不再反射）。
- **失败处置表**（cleat-193）：clojure-test-suite 与 test_clojure 的每个有失败的命名空间在 `tests/failure-dispositions.edn` 里写明类别与原因
  （照 JVM 而套件期望 ClojureCLR、整数装箱模型、宏 spec 不自动加载、只在 Windows 上成立的测试……），`tools/cts.clj` 核对，缺条目或条目过期都算门禁失败。
- 修正（cleat-193）：`(bigdec nil)` 抛 NullReferenceException（原来返回一个坏掉的 BigDecimal）；协议重定义后去掉的方法在调用点报
  ArgumentException（同 JVM，原来到运行期才报"No implementation"）；`(T/.m)` 缺目标的错误原因是 ArgumentException；reify 的类名带外层 fn
  （`clojure.core$promise$reify__…`，同 JVM）；`*unchecked-math* :warn-on-boxed` 打印装箱数学警告（同 JVM 的规则与例外表）。
- **Clojure 1.12 的限定方法值与 param-tags**（cleat-192）：`(map String/.ToUpper xs)`、`(map Math/Abs xs)`、`String/new`、`^[double] Math/Abs`、
  `^[String] Guid/Parse` 作为值是 fn；调用位置的 `^[float] Math/Abs` 也按 param-tags 选重载（原始类型的实参按形参强制转换）。
  原来这些写法被当成 0 参调用或忽略提示。找不到的方法 / 构造器在编译期报错。
- 少反射（cleat-192）：静态属性 / 字段（`(.Ticks DateTime/UtcNow)`）、带 `:tag` 的 Var（`(def ^AutoResetEvent h …)` 之后 `(.WaitOne h)`）、
  loop 局部（按初值，同 JVM）都参与类型推断；core.async 的 timeout 线程不再反射，core.async + tools.reader 的反射警告 54 → 18。
- 带类型实参的泛型短名（cleat-192）：`(:import [System.Collections.Generic |Dictionary`2[System.Object,System.Object]|])` 之后
  `|Dictionary`2[System.String,System.Int64]|` 可用。
- 数组类符号（cleat-192）：`(resolve 'String/2)` 返回数组类型（元素类型找不到抛 TypeNotFoundException）；`definterface` 里可以用本命名空间接口的数组
  `^Foo/1`；`RT/classForName "user.Foo[]"` 不再拿到编译期桩的数组。

### M21 互操作补全（cleat-183 ~ 184）

- **`by-ref` 实参**（cleat-183）：`(Int64/TryParse s (by-ref n))`、`(.TryGetValue d k (by-ref v))` 等 CLR ref / out 形参的写法，局部在调用后是新值；
  重载唯一时编译成 `ldloca` 的直接调用，否则经反射。tools.reader、core.async 改回上游的 by-ref 写法。
- 修正（cleat-185）：core.async 的 timeout 偶发永不关闭（进程挂起）或 timeout 线程抛出使进程退出——timeout 线程在最早的项恰好过期时算出负的等待；
  上游 clr.core.async 同样的问题。
- 修正（cleat-186）：镜像旁的随附库（`Cleat.Clojure.Libs.dll`、`tools/`）按它记的镜像 id 决定用不用，不再看文件时间——对着别的镜像编的不用
  （从源码加载，或在标准错误说明），不会用到时才报 StaleModuleException；`tools/build.cs publish` 改为镜像定下来之后再编随附库。
- **go 块里的 `proxy`**（cleat-184）：tools.analyzer.clr 认 cleat 的 `proxy-class*` / `gen-class*`，go 块里可以直接写 `proxy`（含捕获 go 局部）；
  go 块里的 `await` 报清楚的错误；go 块里调泛型重载的方法（如 `Array/Sort`）不再在宏展开时抛 NullReferenceException。

### M20 编译器改为 C#（cleat-171 ~ 182，[designs/30](designs/30-csharp-compiler.md)）

- 编译器用 C# 重写（`src/Cleat.Compiler`，程序集 `Cleat.Compiler`）：保留 nanopass 式的小 pass（L0 ~ L3 IR 节点由 Source Generator 生成），
  L4 表示表、L5 线性 CIL 之后用 ILGenerator 直接发射；镜像 / 包程序集经 PersistedAssemblyBuilder 持久化，MVID 按内容哈希。
- 优化全部移植（直接链接与内联、KnownCall、类型推断与原始表示、循环特化、入口特化、分层等）；r7rs 56 项 × 5 轮同轮配对与 Clojure 编译器对照
  无可声明回退（111 几何平均 x0.988，本机 x0.963）。REPL 首个提示符 1.58 s → 0.83 s（编译器按需加载只要几毫秒）。
- `dotnet build` 由编译器从源码两步编出镜像（`--no-boot` 编 stage1，再在其上编），约 15 秒；构建两次逐字节相同、两台机器相同（G5）；没有种子、不需要自举。
- **删除**（cleat-182）：Clojure 写的编译器 `src/clojure/cleat/compiler/`、`CLEAT_COMPILER` / `CLEAT_COMPILER_NS` / `CLEAT_CS_ANALYZER` 开关、
  编译器包 `cleat.compiler.dll`、文本种子 `src/boot/seed/` 与 `tools/build.cs` 的 `stage1` / `selfhost` / `clojure-seed` / `compile-compiler`、
  发射器 `src/Cleat.Emit`（`cleat-emit` 命令行与它的 S 表达式 CIL 格式）与 `CLEAT_DUMP_LCIL` / `CLEAT_DUMP_EVAL_LCIL`。
- `cleat.nanopass` 移到 `packages/nanopass`，是普通的用户库（包程序集 `Cleat.Nanopass`）。
- `tools/build.cs cs-image` 改名 `image-set`（旧名仍可用）；宿主新增 `--no-boot`（不加载任何镜像）。
- `CLEAT_NO_OPT` 的记号 `inline` 删除（早已没有读取处）；调用点内联用 `CLEAT_NO_INLINE` / `-g` 关。

### M19 可用性与工程（cleat-158 ~ 170，[designs/29](designs/29-usability-and-engineering.md)）

- **新库**（packages/，各自是 cleat 项目、可打 NuGet 包，上游测试源码与包程序集各跑一遍）：
  - `spec.alpha`（clojure.spec.alpha + core.specs.alpha，`Cleat.SpecAlpha`）与 `test.check`（`Cleat.TestCheck`），取自 ClojureCLR 的移植；加载 spec 后
    宏展开前按 `:args` spec 检查（`CLOJURE_SPEC_SKIP_MACROS=true` 关），spec 错误的文本与 Clojure 1.12 相同；`CLOJURE_SPEC_CHECK_ASSERTS` 打开 `s/assert`（cleat-166）。
  - `core.async`（`Cleat.CoreAsync`，取自 clr.core.async）及其 `go` 宏要的 `tools.analyzer`（tools.analyzer + tools.analyzer.clr）、`tools.reader`（cleat-167）。
- **源位置与栈帧**（cleat-158）：镜像、包与 eval 单元带序列点与 PDB，栈帧报 `文件:行`；错误报告跳过库帧；`-g` 调试模式保留物理栈帧。
- **启动**：`CLEAT_TIME` 分段计时（cleat-159）；编译器按需加载，`-e nil` 与只跑 AOT 代码的 `-m` / `-X` / `-T` 不付编译器的启动成本（cleat-161）；
  ReadyToRun 作为 opt-in（`publish --r2r`、`r2r` 快照，cleat-160）。
- **CI**（cleat-162）：GitHub Actions 两层——PR（并行的 core-host / packages 两个 job，含确定性守卫）与 nightly（Engineering、Conformance、CTS / test_clojure 基线、
  整套产物的确定性与 ilverify）。
- **删除运行时签名特化（sigspec）与类型反馈**（cleat-163 消融裁决、cleat-170 删除）：944 行运行时设施与一个后台线程；r7rs 整体几何平均 x0.977，
  triangl 约 4.6% 的稳定代价经裁决接受。环境变量 `CLEAT_RTSPEC_*`、`CLEAT_DUMP_RT_LCIL` 与 `CLEAT_NO_OPT` 的 `sigspec` / `typefeedback` 记号随之删除。
- **开关清理**（cleat-164）：`CLEAT_NO_OPT` 改为逗号 / 空白分词后精确匹配（原来的子串匹配让 `accinline` 顺带关掉 `inline`）；开关清单进 designs/README。
- REPL 的 Tab 补全认 `require :as` 的别名（`cstr/` → clojure.string 的公有 Var），参数提示认别名、也认列表形式的 `:arglists`（cleat-162 顺带）。
- 修正：变长函数（`doInvoke`）的栈帧源文件被内联代码的文件（core.clj）顶替（cleat-166）；集合字面量上用户写的 `:line` / `:column` 等元数据键不再被剥掉；
  `(:import [… |T`1[X]|])` 之后可以用短名 `T`1`；包里以包内 deftype 为实参的泛型类型常量从包加载时找不到；某些 double（2 的幂附近）打印出的文本
  读不回同一个值，现在与 JVM 一样多给一位（cleat-167）。
- 修正（cleat-169）：158 起镜像构建把 core.clj 内部的直接链接 / KnownCall 一并关掉（只该关内联），core 函数之间全走 `IFn.invoke`——
  diviter / divrec 慢 17% ~ 27%；`-g` 改用独立的开关后恢复。M19 整体对 M19 之前的 r7rs 几何平均：本机 x0.966、111 x0.975。
- 取消：cleat-165（codegen 拆分，由 M20 取代）、cleat-168（jvm-compat 垫片：不追求 Java 部分的完全兼容）。

### M10 ~ M18 性能（cleat-100 ~ 157）

r7rs 56 项对 JVM Clojure 的几何平均倍数（cleat 越小越快）：4.52x（cleat-107）→ 1.88x（cleat-119）→ 1.61x（M15）→ **1.594x**（cleat-150，M16 的同窗口全量）。用户可见的部分：

- **M10**（[designs/20](designs/20-skeg-optimizations.md)）：Num（未定类型数的局部 / 循环 / 形参）、双局部、循环快慢两份推测、无提示函数的类型化入口、宿主调用的静态决议扩大。
- **M11**（[21](designs/21-r7rs-gaps.md)）：分支汇合的原始用途、运行时快路径、浮点循环、分层编译下的生命周期（tier-0 不再抓住惰性序列的头）、类型化调用点。
- **M12**（[22](designs/22-gc-and-performance-followups.md)）：分配快路径、本方法内的异常跳转、循环变量 deftype 的标量替换、字符串搬运。
- **M13 ~ M15**（[23](designs/23-type-specialization.md) ~ [27](designs/27-jit-style-runtime-specialization.md)）：无提示数组形参的推测、运行时签名特化（M19 的 cleat-170 整体删除）。
- **M16**（[28](designs/28-hot-path-optimizations.md)）：char 装箱缓存、String 的 seq / first 直读、跨命名空间 deftype 决议与访问器内联（`CLEAT_OPT_ACCINLINE`，默认关）、
  keyword / str 的 intern 快路径、nth 快路径、GC profile 计数器（`CLEAT_GC_PROFILE`）。
- **M17 / M18**：本机噪声地板标定与"同轮配对"判定口径（`bench/r7rs/pairbuild.py`、`m16sum.py`）；变长参数、闭包提升、调用点反馈都量化后否决，不落实现。

## 1.1.0 及以前（M0 ~ M9，2026-10-15 前）

- **1.1.0（M8 / M9）**：编译器与 nanopass 用 Clojure 重写并自举、文本种子作信任根，Scheme 元层全部删除（[designs/19](designs/19-clojure-self-hosting.md)，
  被 M20 取代）；自举后的编译器对着 clojure-test-suite 与 test_clojure 两套基线验收，偏离表清理（cleat-096 ~ 099）。
- **1.0.0 及以前（M0 ~ M7 与后续）**：不用 DLR 的运行时 `Cleat.Core`（clojure.lang 改写自 ClojureCLR）、clojure.core 与随附库、真实的 .NET 类型
  （`deftype` / `defrecord` / `gen-class` / `proxy`）、AOT 包程序集与运行时 NuGet 包 `Cleat.Runtime`（C# / F# 直接引用，惰性启动）、自包含发布、REPL 与行编辑器、
  嵌入 API `Cleat.Hosting`、`cleat.edn` 项目与 `-M` / `-X` / `-T`、移植的 tools.build / tools.namespace / nREPL / neorepl。
