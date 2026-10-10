# cleat-053 Clojure 自举

- 里程碑：M5′　依赖：cleat-052　状态：完成
- 设计依据：[15 §1](../designs/15-scheme-core-vendoring.md)、[08 §7](../designs/08-bootstrap.md)（确定性）

## 工作内容
1. `GenerateBootImage`：Scheme 镜像按 `src/boot/boot-order.edn` 编译 `cleat/kernel.clj`、`clojure/core.clj` … → `cleat.boot.dll` + `cleat.boot.ifc`；cleat-017 列出的新原语（`maybe-type`、`maybe-special-tag`、`gen-interface`）与 C# 移植（`Printf`、`ImmutableDictionaryEnumerator`）补齐。
2. 不动点：镜像 A 编 core 得 B，B 编得 C，B == C（Lcil 文本与 dll）；确定性措施按 08 §7。
3. `Cleat.Bootstrap.Tests`：两段不动点 + ILVerify。

## 验收标准
- `dotnet build` 从零产出两个镜像；不动点成立；宿主用 `cleat.boot.dll` 启动并跑语料 `:common` 子集（通过率记入 09 §3）。

## 结论（2026-10-10）

完成。Scheme 元层上的 Clojure 工具链从 `src/clojure` 源码加载并编译 boot-order.edn 的全部 35 个文件，产出**一个**程序集 `src/boot/cleat.boot.dll`（+ `.lcil` + `.ifc`），由 `dotnet build` 的 `GenerateBootImage` 增量生成；宿主自动加载它，REPL / 脚本 / `-e` 有完整的 clojure.core。镜像不动点成立（A → B → C，B == C），两个镜像都通过 ILVerify。cleat-030 的验收（REPL 里 `(require 'clojure.string)`）一并完成。

### 逐文件加载（从源码，无 AOT）

| 文件（boot-order） | 顶层形式（cleat-017） | 结果 | 耗时 |
|---|---|---|---|
| `cleat/kernel.clj` | 1 | 通过 | — |
| `clojure/core.clj` + core_clr、core_proxy、core_print、genclass、core_deftype、core/protocols、gvec、instant、uuid | 723 + 303 | 通过 | 约 10 秒 |
| `clojure/string.clj` | 29 | 通过 | 240 ms |
| `clojure/set.clj` | 15 | 通过 | 120 ms |
| `clojure/walk.clj` | 12 | 通过 | 100 ms |
| `clojure/edn.clj` | 3 | 通过 | 35 ms |
| `clojure/data.clj` | 16 | 通过 | 135 ms |
| `clojure/template.clj` | 3 | 通过 | 35 ms |
| `clojure/test.clj` | 60 | 通过 | 610 ms |
| `clojure/stacktrace.clj` | 8 | 通过（由 clojure.test 拉入） | — |
| `clojure/repl.clj` | 20 | 通过 | 185 ms |
| `clojure/main.clj` | 42 | 通过 | 465 ms |
| `clojure/pprint.clj` + 7 个子文件 | 10 + 297 | 通过（加载；运行时 pretty-writer 要 proxy TextWriter，见下） | 2.4 s |
| `clojure/zip.clj` | 30 | 通过 | 235 ms |
| `clojure/clr/io.clj` | 57 | 通过（core.clj:7322 就 require 了它） | — |
| `clojure/clr/shell.clj` | 14 | 通过 | 115 ms |
| `clojure/reflect.clj` + `reflect/clr.clj` | 35 | 通过 | 780 ms |
| `clojure/datafy.clj` | 6 | 通过（由 clojure.reflect 拉入） | — |

冒烟集（第 2 步）全部通过：`(+ 1 2)`、`map`、`reduce`、`str`、`defn`、`defrecord` + `->P` / `:x`、`defprotocol` + `extend-type String`、`(require 'clojure.string)`、`pr-str`、`ex-info` / `ex-data`、`iterate` / `take`、`format`、`sort-by`、atom / `swap!`、`binding`；另试 clojure.set / walk / edn / data / clojure.test 的 `deftest` + `run-tests`。

### 平台修正（按层）

- **Cleat.Core（C#）**：新原语 `Kernel.MaybeSpecialTag` / `MaybeType`（`cleat.kernel/maybe-special-tag`、`maybe-type`，原语表与 primitives.edn / .ss 重生成）；从 ClojureCLR 复制 `clojure.lang.Printf`、`clojure.lang.Runtime.ImmutableDictionaryEnumerator`；LispReader 语法引用不再把含 `.` 的类名限定到当前命名空间；`RT.classForName` 补上 ClrTypeSpec 后备（经 `TypeResolver`：泛型 / 数组 / 嵌套类型名）；`RT.setNextID`（镜像构建的确定性）；Reflector：枚举的字面量字段用 `GetValue`（原来给底层整数）、只差在引用类型形参的重载取第一个（JVM 规则）；`Loader.LoadBootImage`（镜像初始化期间 RT.load 为空操作）与 `BootImageInitializing`。
- **Cleat.Scheme（C#）**：`HostSyntax`——deftype 方法体里的短名字解析为桩（按命名空间登记）、不同接口里签名相同的方法合并、类型名解析回退到 Cleat.Core 的 `TypeResolver`；`ClojureOps.OrderedMap` / `OrderedSet`（语法对象的 map / set 重建保持顺序）；`Driver`——**所有**序列形式先 macroexpand（原来 def… 开头的不展开，defprotocol 的顶层 do 不拆）、`CompileBoot` 钩子、`CLEAT_TRACE_LOAD`、展开错误无位置时报最内层顶层形式的位置（仅限正在加载的文件）。
- **Cleat.Emit（C#）**："类型, eval$N" 先找本单元同名类型、允许动态程序集；本单元定义的接口的方法按全名查找并查父接口。
- **`(clojure syntax)` / `(clojure namespaces)` / `(clojure syntax-kinds)`**：`Type/.method` 的提示直接挂在目标上（不再依赖 `clojure.core/identity`）；`clojure.core/import*` 是特殊形式；`deftype*` 的 `:cleat/interface` 选项（`build-interface`）；记录的描述多两项（基本字段名、特殊字段个数），`getBasis` 不含 `__meta` 等。
- **编译器 codegen**：`interface-class!`（Lcil `interface` 类型）；记录的另两个构造函数与静态 `create(IPersistentMap)`（`record-members`）；`operands!` 溢出到临时变量的操作数取回后补 `castclass`（ILVerify）。Scheme 镜像随之重建，Scheme 自举不动点与快照照常通过。
- **驱动 `(clojure driver)` / `(clojure compile)`**：单元收集改为后序；`boot-build` 下嵌套加载记入同一单元并在其后记 `(in-ns '原命名空间)`；每个顶层形式前重置 id 计数器（序号 × 100000）；`clojure-compile-boot` / `clojure-compile-boot-unit`（规范化 gensym、写 .lcil / .dll / .ifc）；常量编码里哈希 map / set 的项按编码文本排序。
- **宿主**：`--no-boot`、`--compile-boot out.dll`、`--boot-order`；已有镜像时 `*loaded-libs*` 绑到空集再构建；REPL 照 clojure.main 做 repl-requires（逐个，失败跳过）；开发布局下 `src/clojure` 加入加载路径。`Cleat.Host.csproj` 加 `GenerateBootImage`（宿主编译之后，输入：src/clojure/**、boot-order.edn、Scheme 镜像、宿主库清单、宿主与依赖程序集；`-p:BuildBootImage=false` 跳过）。`tools/build.cs stage1 [目录] [--boot 镜像]`、`conformance --run`。

### `CLEAT-053` 源码改动（src/clojure，6 处）

| 文件:行 | 改动 |
|---|---|
| core.clj:4424 | `maybe-resolve-in`（CLEAT-017 加的）用了 5203 行才定义的 `defn-` → `(defn ^:private …)` |
| core_proxy.clj:396 | proxy 宏（CLEAT-017 子集）用了尚未定义的 `into` → `reduce1` |
| genclass.clj:270 | `generate-interface` 返回 `(deftype* 名 名 [] :implements [...] :cleat/interface [[m [参数类型] 返回类型] …])`（原为运行时原语 `cleat.kernel/gen-interface`，不再需要） |
| genclass.clj:308 | `gen-interface` 宏直接展开为上面的形式 |
| core_deftype.clj:47 | `definterface` 的 `(let [] …)` → `(do …)`：顶层 do 拆开，`import` 展开时接口已生成 |
| pprint/cl_format.clj:1702 | 参数类型检查：声明为 Int32 的参数接受任何整数（互操作的 int 是 long） |

C# 的 EPL 文件另有 `CLEAT-053` 标记：LispReader.cs（语法引用）、RT.cs（`setNextID`、ClrTypeSpec 后备），以及复制来的 Printf.cs、ImmutableDictionaryEnumerator.cs。

### 镜像

- 构建：`cleat --no-boot -I src/clojure --compile-boot src/boot/cleat.boot.dll`，约 18 ~ 20 秒（加载 + 展开 + 求值约 16 秒，编译 + 发射约 2 秒）。dll 1.82 MB、Lcil 14.2 MB、`.ifc` 2.5 KB；2308 个类型（fn 类、deftype / record、接口），2189 个顶层形式（`Init<k>`），8447 个常量。
- 启动（到 REPL 提示并退出）：约 2.0 秒，其中 Scheme 镜像约 0.41 秒、驱动库 fasl 约 0.09 秒、Clojure 镜像约 0.87 秒（初始化全部顶层形式，不再展开 core）；对比从源码加载 clojure.core 约 10.7 秒、全部 boot 命名空间约 16 秒。
- 镜像初始化：被加载文件的形式排在 `(load …)` / `(require …)` 之前（后序），初始化期间 RT.load 为空操作，require 照常登记 `*loaded-libs*`；嵌套加载后恢复 `*ns*`。
- 不动点（`ClojureBootstrapTests`，Bootstrap 类别）：A = 源码构建（与 `dotnet build` 生成的 src/boot 镜像逐字节相同），B = 在 A 上构建，C = 在 B 上构建；B == C。sha256：A dll `5abc16d276c60e7d…`、lcil `077f887ec49518e9…`；B = C dll `a5fc4d17fd786094…`、lcil `a98aed9d047707df…`。A ≠ B 是预期的（A 展开 core.clj 开头时用的是 RT 的引导 `ns` 等，B 用镜像里的）。确定性措施：id 计数器按形式序号重置（gensym `x__N__auto__`、`fn__N` 类名）、Scheme gensym 规范化、语法对象 map / set 保序、常量里的哈希集合按编码排序；MVID 由 Cleat.Emit 回填。
- ILVerify：`cleat.boot.dll` 与 `cleat.scheme.boot.dll` 都 "All Classes and Methods … Verified"（测试里对 C 校验）。

### 语料基线（第 5 步，修正留给 cleat-032）

`dotnet run tools/build.cs -- conformance --run`（`tools/conformance.clj`：每行新命名空间、256 MB 栈线程、10 秒超时，`=` 比较；`:throws` 须抛出）。`:common` 4842 行：**通过 3913（80.8%）**，不等 179，异常 749，超时 1；约 50 秒。已记入 designs/09 §3。
- 失败最多的套件组：format 100、interop 61、host-interop 51、numbers 44、conformance 43、regex 42、io 37、reader 36、exceptions 32、charsets 26、clojure.math 25、errors 18、refs 15、protocols 14、ns 14。
- 异常最多的原因：JVM 异常类名不存在（`IllegalArgumentException` 51、`ClassCastException` 48、`Throwable` 29、`IllegalStateException` 25、`NullPointerException` 24、`NumberFormatException` 19、`RuntimeException` 11、`IndexOutOfBoundsException` 9 …）；没有的命名空间（`clojure.math` 23、`clojure.java.io` 17、`java.util.TimeZone` 19、`Long` / `Integer` 静态 28）；`(catch :default …)` 22；Printf 的 `%a`（HexFloat）18；JVM 方法名（`.length`、`.getName`、`.substring`、`Double/parseDouble` …）。

### 测试

- 新增 `tests/Cleat.Host.Tests/ClojureBootstrapTests.cs`（6 项：三段构建、B == C、C 跑冒烟、ILVerify）、`HostProcessTests.ReplRequireClojureString`；REPL / `-e` / `load-file` 的期望随 print-method 更新（`#'user/x`、`#object[Namespace …]`），宿主糖 `(String/.Length "abc")` 的展开为 `(. "abc" Length)`。
- 全量 `dotnet test Cleat.slnx`：Cleat.Core.Tests 129、Cleat.Host.Tests 195、Cleat.Scheme.Tests 225（含 Scheme 自举不动点、ILVerify、快照），全部通过。

### 留给后续

- `clojure.pprint/pprint`、`cl-format` 的大小写转换、`PrintWriter-on`、repl 的 `source-fn`：要 proxy 继承 TextWriter / PushbackTextReader（cleat-043）；`gen-interface` 的属性与 by-ref 参数（cleat-043）。
- 语料的失败（JVM 类名 / 方法名、clojure.math、clojure.java.io、HexFloat、`catch :default` …）：cleat-032。
- def 元数据 `:file` 是绝对路径（JVM 是相对 classpath 的 `clojure/core.clj`）：镜像不可重定位，只在同一工作区里逐字节可比（cleat-033 / 034 发布时改为相对路径）。
- 直接链接 / `invokeStatic`、接口文件只有少数条目（`defn` 的元数据包装使多数 fn 不是 `(def v (fn* …))` 形状）：cleat-042。
- 非 boot 命名空间的 `.clj.dll` AOT 与 test_clojure：cleat-033 / cleat-032。
- `#object[Namespace …]` 等打印用短类型名（ClojureCLR 的 core_print 用 `.Name`；JVM 为全名）：cleat-032 按语料决定。

## 进度
- 2026-10-10 开始：宿主 `cleat -I src/clojure -e '(clojure.lang.RT/load "clojure/core")'` 从源码加载，首个失败在 core.clj:253（`cleat.kernel/maybe-special-tag` 未定义）。新增 `Cleat.Core.Runtime.Kernel`（`MaybeSpecialTag`、`MaybeType`）并登记进原语表（`maybe-special-tag`、`maybe-type`；primitives.edn / .ss 重生成，Scheme 镜像不变）。`gen-interface` 计划改为由宏展开成带 `:cleat/interface` 选项的 `deftype*`，编译器生成 Lcil `interface` 类型（AOT 可持久化）。
- 2026-10-10 **clojure.core 及其 load 的 9 个文件从源码全部加载**（约 10 秒）。沿途修正：语法引用把含 `.` 的类名限定到当前命名空间（LispReader，`;; CLEAT-053`）；`Type/.method` 改写不再依赖 `clojure.core/identity`（syntax.sls `hint-target`）；core.clj 的 `maybe-resolve-in` 用了尚未定义的 `defn-`、core_proxy 用了尚未定义的 `into`（源码改动）；`clojure.core/import*` 作特殊形式（namespaces.sls）；`clojure.lang.Printf`、`ImmutableDictionaryEnumerator` 从 ClojureCLR 复制；`gen-interface` → `deftype*` + `:cleat/interface`，codegen 生成 Lcil `interface` 类型；`definterface` 改为顶层 `do`；驱动对**所有**序列形式先 macroexpand（原来 def… 开头的不展开，defprotocol 的顶层 do 没有拆开）；发射器对 "类型, eval$N" 先找本单元同名类型、允许动态程序集；deftype 方法体里短名字解析为桩（HostSyntax.StubForTag）；不同接口里签名相同的方法合并（gvec 的 count）；RT.classForName 补上 ClrTypeSpec 后备（泛型 `BlockingCollection`1[System.Object]`）；`CLEAT_TRACE_LOAD`、无位置的展开错误报最内层顶层形式的位置。冒烟：`(+ 1 2)`、map、reduce、str、defn 通过；下一个：defrecord（PersistentHashSet/create 重载歧义）。
- 2026-10-10 defrecord：记录的另两个构造函数（基本字段 / 去掉 __hash __hasheq）与静态 `create(IPersistentMap)`、`getBasis` 不含特殊字段（syntax.sls 描述加两项，codegen `record-members`）；Reflector 对只差在引用类型形参的重载取第一个（JVM 规则；ChunkedCons 同时是 IList / ISeq）。**第 2 步的冒烟集全部通过**（含 `(require 'clojure.string)`）。下一个：按 boot-order 加载其余命名空间。
- 2026-10-10 **boot-order 全部 35 个文件从源码加载成功**（core 约 10 秒，其余 16 个命名空间约 6 秒）。修正：cl_format 的 Int32 参数检查接受整数（源码，互操作 int → long）；Reflector 读枚举的字面量字段用 GetValue（原来给底层整数，clojure.reflect）。下一个：第 3 步镜像编译。
- 2026-10-10 **镜像编译成功并可运行**：`cleat --no-boot -I src/clojure --compile-boot <dir>/cleat.boot.dll`（读同目录 boot-order.edn；18 个入口：load kernel / core，require 其余 16 个）约 18 秒，dll 1.8 MB，lcil 14 MB。做法：驱动的单元收集改为**后序**（被加载文件的形式排在 (load …)/(require …) 之前），`boot-build` 时嵌套加载也记入同一单元、加载结束后记一个 `(in-ns '原命名空间)`；`Loader.LoadBootImage` 在镜像初始化期间让 RT.load 成为空操作（require 照常登记 *loaded-libs*）；`clojure-compile-boot-unit` 规范化 gensym、写 .lcil/.dll/.ifc；发射器找本单元接口的方法用全名并查父接口；宿主 `--no-boot`、`--compile-boot`、`--boot-order`，已有镜像时 *loaded-libs* 绑到空集。用镜像跑冒烟集全部通过，Clojure 镜像加载约 0.83 秒（源码约 16 秒）。下一个：MSBuild 目标、REPL 的 repl-requires、测试。
- 2026-10-10 **不动点成立**：A（源码构建）→ B（A 上构建）→ C（B 上构建），B == C（lcil 与 dll 逐字节）。确定性措施：构建时每个顶层形式前 RT id 计数器置为 序号×100000（`RT.setNextID`）；语法对象里的 map / set 重建保持子项顺序（`ClojureOps.OrderedMap/OrderedSet`，子项全是数据时照常建哈希集合）；常量编码里哈希 map / set 的项按编码文本排序；Scheme gensym 规范化。MSBuild 目标 `GenerateBootImage` 已加（src/boot/cleat.boot.*）。下一个：测试（Bootstrap 类别的二段不动点、Host 的 require）、语料、全量测试。
- 2026-10-10 ILVerify：镜像里 3 处 StackUnexpected（try 在操作数位置时操作数存进 object 临时变量，取回后缺 castclass）→ codegen `operands!` 取回后补转换（`reload-cast`）；两个镜像都通过 ILVerify。测试：`tests/Cleat.Host.Tests/ClojureBootstrapTests.cs`（Bootstrap 类别：A/B/C、B == C、C 跑冒烟、ILVerify），`HostProcessTests.ReplRequireClojureString`（cleat-030 验收），REPL 打印随 print-method 更新；宿主的 REPL 照 clojure.main 做 repl-requires（逐个，失败跳过），开发布局下 src/clojure 加入加载路径；展开错误的位置后备只用于正在加载的文件（REPL 仍为 0:0，同 JVM）。**全量测试通过**（Core 129、Host 195、Scheme 225）。语料运行器 `tools/conformance.clj` 已写。下一个：跑语料基线、tools/build.cs 接入、结论。
- 2026-10-10 第 5 步：语料基线 `:common` 4842 行通过 3913（80.8%），`tools/build.cs conformance --run` 接入；`stage1` 命令实现；结论已写，TASK.md：cleat-053、cleat-030 完成。
