# cleat-052 编译驱动与宿主接入

- 里程碑：M4′　依赖：cleat-051　状态：完成
- 设计依据：[04 §9](../designs/04-compiler.md)、[05 §6](../designs/05-cil-backend.md)、cleat-012 / 022 / 030

## 工作内容
1. `eval`：展开 → 简单形式解释（04 §9，借 stage0 的 `compile-core`）/ 其余编译到内存程序集；`macroexpand` / `macroexpand-1` 钩子。
2. `load` / `compile`：逐形式展开—编译—执行；`.clj.dll` + 接口文件（`InterfaceFile`）；`BootInterface.Check` 调用的生成。
3. 用 Scheme 实现 `RuntimeHooks` 的登记（`load-source` `compile` `eval` `macroexpand-1`）；`Cleat.Host` 接上，REPL 可用。

## 验收标准
- REPL 里 `(require 'clojure.string)`（源码加载 → 编译）成功；`clojure.string` 等三个命名空间编译为 `.clj.dll` 并通过各自的 test_clojure 测试。

## 结论（2026-10-10）

完成。`cleat`（`src/Cleat.Host`）启动 Scheme 镜像，由 Scheme 代码登记 Clojure 的运行时钩子，在 clojure.core 之前（cleat-053）就有可用的 REPL / 脚本 / `-e` / `load` / `RT.load` 式加载 / 编译到 `.clj.dll`。原验收里依赖 clojure.core 的两条（REPL 里 `(require 'clojure.string)`、三个命名空间编译为 `.clj.dll` 并通过 test_clojure）移到 cleat-053（`require` 本身是 core.clj 的函数）与 cleat-032；本任务以下面的进程级测试为准。

### 启动顺序（两个镜像）

1. 登记原语（Scheme 元层的 `Cleat.Scheme.PrimitiveRegistry` 与 `cleat.kernel`）→ `RT.Init` → `*out*` / `*err*` 的根换成控制台（原来是不自动刷新的 StreamWriter，警告会丢）；
2. 加载 Scheme 镜像 `cleat.scheme.boot.dll`（`--scheme-boot`、`CLEAT_SCHEME_BOOT`，或自动找：宿主目录、当前目录与宿主目录各自上级的 `src/boot/`）；
3. `ClojureHost.Start`：读镜像旁 `cleat.scheme.clojure/cleat-host.manifest`，按序装入启动库的 fasl（`(ironscheme clr helpers)` `(ironscheme clr)` `(clojure syntax-kinds)` `(clojure namespaces)` `(clojure syntax)` `(clojure driver)`），运行 `program.fasl`（`clojure/host.sps`：`(install-clojure-hooks!)` 登记 `eval` `load-source` `compile` `macroexpand-1` 与 C# 的 `report-error`）；
4. 加载 Clojure 镜像 `cleat.boot.dll`（`--boot` / `CLEAT_BOOT` / 自动找；cleat-053 之前没有）并设置 `BootInterface.CurrentImageId`——两镜像设计不变，Clojure 镜像在钩子之后加载；
5. user 里 refer clojure.core 已有的公有 Var（clojure.main 的做法；core 之前只有 RT 定义的 `in-ns` `ns` `load-file` `*ns*` `*file*` `*command-line-args*` `*1` … 等）→ `clojure.main` 语义的动作或 REPL。主体在 512 MB 栈的线程上（与 stage0 相同）。`CLEAT_TIME` 打印各段耗时。

### 库加载的决定：构建时预编译成 fasl

| 方式 | 启动时装入驱动库 | 第一次 AOT 编译（含装入编译器库） | `cleat` 到 REPL 退出 |
|---|---|---|---|
| 源码（`-I lib -I nanopass`，psyntax 展开 + 编译每个库） | 1480 ~ 2400 ms | 约 21 s（编译器 + nanopass 26 个库从源码展开） | 约 3.0 s |
| **预编译 fasl**（选用） | **90 ~ 170 ms** | **约 2.9 s** | **约 0.54 ~ 0.7 s** |

（同一机器，负载不同时的区间；Scheme 镜像加载另计 410 ~ 900 ms，测试进程里偏高。）测量中发现镜像虽含编译器代码，`(ironscheme compiler …)` 与 nanopass 却不是镜像里登记的库：任何导入它们的库都要另装一份（基线 `(import (clojure compile))` 10.6 s）。因此：驱动 `(clojure driver)` **不导入编译器**，eval 用镜像自己的 `compile-core`（经 `current-primitive-locations` 取位置）；只有 AOT 需要的 `(clojure compile)`（及编译器、nanopass）是"延后库"，第一次 compile 时由 `ClojureHost.EnsureCompileLibraries` 装入。生成：`Cleat.Host.csproj` 的 `GenerateClojureHostLibraries`（镜像之后、增量，约 20 秒）在镜像上运行 `clojure/build-host.sps`：先 `(environment '(clojure driver))`、再 `(environment '(clojure compile))` 从源码装入（装入顺序即依赖顺序），`compile-whole-program` 写出 `libN.fasl` + `program.fasl`，再写清单（`library` / `program` / `deferred` 三段）。fasl 是对特定镜像编译的（镜像 id 核对、库 id 核对）：不可用或过期时警告并退回源码（`CLEAT_SCHEME_SOURCE` 或镜像旁的 `../scheme`）。

### eval：顶层 do 拆分与简单形式的快速路径

- `clojure-eval-toplevel` 照 Clojure 的 `Compiler.eval`：序列形式先 `macroexpand`（头部是 `def…` 符号的除外），结果是 `(do …)` 则逐个子形式递归求值（后面的子形式看得到前面的 `in-ns` / `def` / `setMacro`；宏展开出的 `do` 也拆），否则展开为 Lcore → 简单形式解释，否则镜像的 `compile-core`。`in-ns` / `ns` 在文件中途切换命名空间：每个形式展开前读 `*ns*`，`load` 在外层绑定 `*ns*`、结束后恢复。
- 镜像里没有解释器（stage0 是单独的 C# 程序，且不认 `$clj-…` 节点），所以在 C#（`Cleat.Scheme.Clojure.Driver`）写了只认简单 Lcore 的小解释器：`quote`、局部、`if` / `begin`、单子句 lambda 的立即调用（let）、`null?`、`$clj-var-ref` `$clj-var-set!` `$clj-def` `$clj-invoke` `$clj-keyword-invoke` `$clj-call`（反射调 `ClojureOps`）；`fn*`（`$clj-fn`）、`deftype*`、`case*`、`loop*`、try 等照常编译。先整体检查、再求值，不会半途退回。测量：每个简单形式 2.3 ~ 3 ms（生成程序集）→ 约 6 µs；一个 1000 个 `(def vN [N (cleat.kernel/add N 1) (. "abc" Length)])` 的文件 7.6 ~ 10 s → 0.5 ~ 0.7 s，剩下的几乎全是展开（约 0.36 ms / 形式，cleat-042）。`CLEAT_NO_SIMPLE_EVAL` 关闭快速路径；测试逐个形式对照两条路径的结果。
- 错误位置：异常**不包装**（Clojure 代码的 `catch` 语义不变）；`RunForm` 的 C# 异常过滤器在最内层顶层形式处把 `文件:行:列` 记进 `Exception.Data`，`report-error` 打印 `Execution error (类型) at (文件:行:列).` + 换行 + 消息（类型为 `Exception` 时省略，同 `clojure.main/ex-str`；REPL / `-e` 的文件记 `REPL`）；展开错误是 ExpanderException 原文 `Syntax error (文件:行:列) …`；读错误 `Syntax error reading source at (…).`。
- 050 未做的以**宏 Var 对象**开头的形式 `(#'m …)`（只能由宏构造）：`$application` 与 `macroexpand-1` 都按宏展开；别的命名空间的私有宏报 C# 的文本 `Var: #'a/m is not public`。RT 的引导 `ns` 宏展开为 Namespace 对象，改为按名字取（`ClojureOps.FindNamespaceOf`），否则存 dll 时无法编码。

### load / compile 与 `.clj.dll`、接口文件

- `load-source` 钩子：`LineNumberingTextReader` + `LispReader`（`:read-cond :allow`，平台特性 `:cleat` `:cleat-clr` `:cljr`），逐个读、`clojure-eval-toplevel`，返回最后的值；`*file*` / `*source-path*` 由 `Loader` 绑定。`RT.load` / `load-file` / 脚本 / `-m` 都经 `Loader`（dll 不比源码旧时用 dll，cleat-012）。
- `compile` 钩子（`*compile-files*` 为真时 `Loader` 调用；嵌套加载的库各成一个单元）：同 load，同时按求值顺序收集每个顶层形式（do 拆开后的）的 Lcore，结束后经 `(clojure compile)` 的 `clojure-compile-unit` 编成**一个**持久化程序集：名字照 ClojureCLR（`a/b.clj` → `a.b.clj.dll`，程序集名 `a.b.clj`），放在 `*compile-path*`（字符串时）或找到源码的加载路径目录；入口 `[CleatModule]` 的 `Initialize` 依次执行各形式。编译时运行着 Clojure 镜像时，第一个形式是 `ClojureOps.CheckBootImage(镜像 id)` → `BootInterface.Check`，镜像换了则 `StaleModuleException`，`Loader` 警告并回退源码（测试用两个小 `.clj.dll` 冒充两个镜像验证）。
- 接口文件 `a.b.clj.ifc`（cleat-022 的 EDN 格式，`Loader.LoadModule` 加载 dll 后按 MVID 登记）：`{:assembly "a.b.clj" :image-id "<dll 的 MVID>" :entries [{:var a.b/f :class "a.b$f, a.b.clj" :arities [0 1 -2] :static false} …]}`。条目：顶层 `(def v (fn* …))`（含命名 fn* 的 letrec 形状）、Var 非 `^:dynamic` / `^:redef`、单元内只定义一次、fn 类名唯一（codegen 重名时加 `__N`）；变长 arity 记 `-(必需个数 + 1)`。
- **直接链接未做，推迟到 cleat-042**：Clojure 缺省不直接链接（`*compiler-options*` 的 `:direct-linking`，clojure.core 自己才开），开了之后 REPL 里重定义对已编译调用者不可见，需要编译选项的语义；codegen 还要为 fn 类生成静态 `invokeStatic`（现在子句体是实例方法，自引用是 `this`）、在已知调用点 `call`。接口文件已记下类名与 arity（`:static false`），cleat-042 生成 `invokeStatic` 后把它置真即可跨单元使用。

### 测试

- 新增 `tests/scheme/clojure/driver.sps`（70 项，进 SchemeScriptTests）：钩子已登记；顶层 do 拆分（in-ns、同一 do 里先定义后用的宏、嵌套 do、宏展开出的 do）；哪些形式走快速路径（11 种）；36 个形式两条路径结果逐个对照（常量、集合、关键字调用、互操作、let / if 的真值、动态 Var、def 的元数据、异常、调用 nil 等）；load 的命名空间切换与恢复；宏 Var 对象开头的形式（展开、macroexpand-1、私有宏报错）；执行错误的类型与位置、语法错误文本、异常不包装。
- 新增 `tests/Cleat.Host.Tests`（11 项，子进程运行 `cleat`）：REPL 逐行对照（def、fn、`*1` `*3` `*e`、in-ns 后的提示、语法 / 执行 / 读错误后继续）、启动计时（断言用了预编译库）、`-e` 打印每个非 nil 值、脚本与 `*command-line-args*`（及脚本里的执行错误位置与退出码 1）、错误文本（未解析符号、运行时异常、文件里的语法错误）、`load-file` 切换命名空间（含文件里的顶层 do）、从加载路径加载源码、**编译为 `my.lib.clj.dll` + `.ifc` 后新进程从 dll 加载**（程序集名、`Loader.LoadedAssemblies`、接口文件登记、加载副作用只一次、deftype 类型来自该程序集），源码更新后回到源码、对另一个 Clojure 镜像编译的 dll 回退源码、`cleat -` 读标准输入、简单形式不生成程序集。
- 全量 `dotnet test Cleat.slnx`：Cleat.Core.Tests 129、Cleat.Scheme.Tests **225**（224 + `clojure/driver.sps`，含自举不动点与快照，6 分 17 秒）、Cleat.Host.Tests **11**，全部通过。

### 留给后续

- `(require 'clojure.string)`、`compile` / `compile-file` 函数、clojure.main 的 `repl` / `ex-triage` 接管 REPL 与错误报告、REPL 打印用 `print-method`：cleat-053（core.clj）；`#'user/x` 的打印也随 core_print。cleat-030 的验收（REPL 里 `(require 'clojure.string)`）同样等 cleat-053。
- 直接链接 / `invokeStatic` / 跨单元 `call`（见上）、展开的开销（简单形式的大头）：cleat-042。
- Scheme 侧编译器库在镜像里不可导入，AOT 第一次要装入 26 个延后库（约 2 秒）：可让镜像登记编译器库（构建脚本改动）后去掉，留 cleat-054 / 042 考虑。
- 宿主错误文本的黄金文件、Scheme 条件不流到 Clojure 的系统审计：cleat-055。

## 进度
- 2026-10-10 基线测量（Release，stage0 --boot 镜像）：镜像加载约 380 ms；从源码 `(import (clojure syntax) (clojure compile))` 10.6 s——镜像里有编译器的代码（eval 用），但 `(ironscheme compiler …)` 与 nanopass 不是镜像里登记的库，导入时从源码展开、编译全部 32 个库。同一组库用 `compile-whole-program` 预编译成 fasl 后加载 + 执行约 1.1 s（其中约 0.5 s 是测量脚本自己的展开与编译）。`clojure-eval` 一个简单形式（`(def z 1)` / `(. "abc" Length)`）2.3 ~ 3.1 ms，其中展开只占 0.075 ms，其余是每个形式生成一个内存程序集——简单形式的快速路径值得做。
- 2026-10-10 设计：
  - **库加载**：构建时（`Cleat.Host.csproj` 新目标，镜像之后）用镜像运行 `src/scheme/clojure/build-host.sps`：先装入 `(clojure driver)` 及其依赖（启动所需），再装入 `(clojure compile)` 及其依赖（编译器、nanopass，只有 AOT 编译需要），然后 `compile-whole-program` 把宿主程序 `clojure/host.sps` 连同全部库写成 `src/boot/cleat.scheme.clojure/` 下的 fasl，清单 `cleat-host.manifest` 分三段：启动库、程序、延后库。宿主启动只装启动库并运行程序（程序登记钩子）；第一次 AOT 编译时再装延后库。fasl 缺失或对旧镜像编译（镜像 id / 库 id 不符）时退回源码（警告，慢）。
  - **编译器入口**：驱动不导入 `(ironscheme compiler)`，用镜像自己的 `compile-core`（经 `current-primitive-locations` 取位置）。
  - **eval 快速路径**：镜像里没有解释器（eval 经编译器）；在 C#（`Cleat.Scheme.Clojure.Driver`）里写一个只认简单 Lcore 的小解释器：`quote`、局部变量、`if` / `begin`、`(case-lambda 单子句)` 立即调用（let）、`null?`、`$clj-var-ref` `$clj-var-set!` `$clj-def` `$clj-invoke` `$clj-keyword-invoke` `$clj-call`；含 `$clj-fn` / `$clj-deftype` / `$clj-case` / try / 其他过程调用的形式照常编译。先整体检查再求值，不会半途退回。
  - **错误位置**：不包装异常（Clojure 代码的 `catch` 语义不变）；驱动执行每个顶层形式时由 C# 异常过滤器在 `Exception.Data` 记下最内层形式的 文件 / 行 / 列，`report-error` 据此打印 `Execution error (类型) at (文件:行:列).`。
- 2026-10-10 实现：C# `Cleat.Scheme.Clojure.Driver`（简单形式解释器、读形式、带位置执行、顶层 do、钩子包装、Clojure 风格错误文本、AOT 输出路径与接口文件）与 `ClojureHost`（读清单装入 fasl / 退回源码、延后库）；Scheme `(clojure driver)`（`clojure-eval-toplevel`、`clojure-load-reader`、`clojure-compile-reader`、`install-clojure-hooks!`）、`clojure/host.sps`、`clojure/build-host.sps`；`(clojure compile)` 加 `clojure-compile-unit`；050 未做的以宏 Var 对象开头的形式（application 与 macroexpand-1）；RT 引导 `ns` 宏展开出的 Namespace 对象改为按名字取（否则 AOT 编码失败）。`Cleat.Host`：两镜像启动顺序、`--scheme-boot`、512 MB 栈线程、`*out*` / `*err*` 根换成控制台、user 里 refer clojure.core 已有的公有 Var、`-e` 逐个打印非 nil 值。`Cleat.Host.csproj` 加 `GenerateClojureHostLibraries`（镜像之后，约 20 秒）。
- 2026-10-10 手工验证：REPL（def、fn*、互操作、in-ns、`*1 *2 *3 *e`、语法 / 执行 / 读错误后继续）；`RT.load` 从加载路径加载源码；`*compile-files*` 下加载生成 `my.lib.clj.dll` + `.ifc`（命名 fn 的 letrec 形状也认），新进程从 dll 加载（程序集名 my.lib.clj，接口文件登记）；用两个小 .clj.dll 冒充 Clojure 镜像：对镜像 A 编译的 dll 在镜像 B 下报 StaleModule 警告并回退源码。启动：Scheme 镜像约 415 ms，驱动库 fasl 90 ms（源码 1480 ms）；`echo | cleat` 到退出约 540 ms。
- 2026-10-10 测试：`tests/scheme/clojure/driver.sps`（70 项）与 `tests/Cleat.Host.Tests`（11 项）通过；全量测试通过（Core 129、Scheme 225、Host 11）；结论已写，TASK.md：cleat-052 完成，cleat-030 仍为进行中（剩 `(require 'clojure.string)`，等 cleat-053）。
