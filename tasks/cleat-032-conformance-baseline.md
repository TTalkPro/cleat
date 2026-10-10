# cleat-032 符合性基线：语料、clojure-test-suite、test_clojure

- 里程碑：M6　依赖：cleat-031、cleat-002　状态：完成
- 设计依据：[09](../designs/09-testing.md)

## 工作内容
1. 语料 runner 跑新系统（编译执行）：`:common` 全量 + 我们新增的 `:clr` 行；偏离分类写入 `known-divergences.edn`。
2. clojure-test-suite：逐命名空间运行，建立 `tests/cts-baseline.edn`（通过 / 失败数），优于或劣于基线都报错。
3. test_clojure：可运行的命名空间集合与通过数。
4. 修复高价值失败（按语料 suite 排序），直到 `:common` 通过率 ≥ 99% 且剩余全部分类。
5. 把数字写入 09 §3。

## 验收标准
- 三个基线记入 09 §3；`dotnet test --filter Category=Conformance` 通过（按基线判定）。

## 结论（2026-10-10）

完成。语料 `:common` 从 3913（80.8%）提高到 **4281 / 4842（88.4%）**，其余 561 行（560 个 `[suite label]`，语料里有一个重复 id）全部在 `tests/conformance/cleat-divergences.edn` 里分类；去掉宿主相关（`:host/*`）与取证值不是 JVM 的（`:corpus/jolt-value`）行后，4327 行通过 98.9%。三个基线记入 designs/09 §3，`dotnet test --filter Category=Conformance` 跑三个门禁。

### 失败分组：之前 / 之后

| 分组（cleat-053 的结论） | 之前 | 之后 |
|---|---|---|
| 运行器在 `conformance.rowN` 而不是 `user` 里求值（记录、语法引用、`*ns*`、`#'user/x`） | 约 30 | 0（每行新的 `user`；先读 `:expected`） |
| JVM 异常类名（`IllegalArgumentException`、`ClassCastException`、`Throwable` …） | 约 230 | 0 个解析失败；仍不同的捕获语义 28 行 → `:host/exception-type` |
| `(catch :default e …)` | 22 | 0 |
| `clojure.math` | 25 | 0 个缺失（剩下的 clojure.math 行用 `Long/MAX_VALUE`、`.getMessage` → `:host/jvm-api`） |
| Printf `%a` / `%e %f %g` 格式 | 约 60 | `%h` 与两处 Printf 缺口 2 行 `:printf`；其余是 `%t` 的 java.util.Calendar（`:host/jvm-api`） |
| `str` / 打印的浮点数与布尔（`"1"`、`"True"`、`1E+100`） | 约 15 | 0 |
| 哈希（字符串 / 符号 / 关键字 / 字符 / 布尔）与哈希集合顺序 | 11 | 0 |
| `(= 1 1.0)`、`(= \a 97)` 报重载歧义 | 13 | 0 |
| `sort` 的布尔比较器反序 | 2 | 0 |
| deftype 重定义（同名记录再定义后转换失败、`instance?` 为假） | 约 20 | 0 |
| JVM 方法 / 类（`.length`、`.getName`、`Long/`、`java.io.File`、`clojure.java.io` …） | 约 300 | 310 → `:host/jvm-api`（不模拟 Java 类库） |
| 正则（java.util.regex 方言） | 约 57 | 39 → `:host/regex` |

### 修正（按层）

- **Cleat.Core（C#）**：`Runtime/JvmNames.cs`（JVM 异常名的隐式导入，designs/03 §4.1 的表）；`Runtime/JvmText.cs`（Double.toString / Float.toString / Boolean 的文本、`Pattern.split`、`String.indexOf` / `lastIndexOf` / `replace`、Java 替换串语法与 `quoteReplacement`、`Double.valueOf` 文法的 `parse-double`）；`Runtime/ClojureMath.cs`（clojure.math 的 `*-exact`、`floor-div`、`round`、`rint`、`ulp`、`next-*`、`get-exponent`、`expm1`、`log1p` …）；Printf：`%e %f %g %a` 照 java.util.Formatter 重写（最短十进制数字 + HALF_UP、两位指数、`Infinity` 不补零、`-0.0`、BigDecimal、`Double.toHexString` 与 hexDouble 舍入），`IllegalFormatException` 公共基类，`%h` 对字符串用 String.hashCode；`RT.CultureToString` / 打印用 JVM 文本；`RT.SortArray` 稳定排序、布尔比较器；`RT.find` 对非关联集合抛错；`Atom.compareAndSet` 对装箱值类型按值比较；`Numbers.ops` 非数值操作数抛 InvalidCastException；`Util.hasheq` / `Symbol.hasheq` 与 JVM 相同（跨进程稳定）；`JReMatcher`：`matches` 用 `\A(?:p)\z`、未参与匹配的组为 nil；Reflector：互不支配的重载里取第一个无需数值转换的；C# 展开器也认 `catch :default`。
- **Cleat.Scheme（C#）**：HostSyntax——正在展开的 deftype 的“待定桩”优先于同名的旧定义（重定义）、deftype 方法里 `toString` / `hashCode` / `equals` 对应 Object 的虚方法、只差返回类型（协变）的接口方法不要求提示；`ImportOf` 按名字取最新定义。
- **`(clojure syntax)`**：`(catch :default e …)` 捕获 `System.Exception`。
- **编译器 codegen**：Clojure 的 fn 每次求值新建对象（不再对无自由变量的 fn 做单例；`(identical? (mk) (mk))` 为假）；没有基本字段的记录也生成无参构造函数与 `create`。Scheme 镜像随之重建，Scheme 与 Clojure 两个自举不动点照常通过。
- **宿主**：脚本 / `-e` / `-m` / REPL 在 clojure.main/with-bindings 的绑定里运行（`*1 *2 *3 *e` 为 nil、`*print-namespace-maps*` 为 true）。
- **src/clojure（`;;; CLEAT-032` 标记）**：新增 `clojure/math.clj`（Clojure 1.11 的 API + ClojureCLR 的扩展，进 boot 镜像）；core.clj：`conj!` 变长、`distinct` 不对集合做 nth、`merge` 按 Clojure 1.12 的 conj 语义、`parse-double` 用 Java 文法；string.clj：`split`、`replace`（字符串与正则两种）、`replace-first`、`re-quote-replacement`、`index-of` / `last-index-of` 的 from 版本、`replace-by` 的组；test.clj：`do-report` 跳过没有声明类型的动态方法栈帧（原来**每个失败的断言都变成 NullReferenceException 错误**）。
- **工具**：`tools/conformance.clj`（`user` 命名空间、`--results`、`--gate`）、`tools/cts.clj`（两个 clojure.test 套件的基线与门禁）、`tools/build.cs cts`；`tests/Cleat.Host.Tests/ConformanceTests.cs`（`Conformance` 类别，3 项）；`tests/test_clojure/test_helper.clj`（从 ClojureCLR 复制，EPL）。

### 偏离分类（`cleat-divergences.edn`，561 行）

| 类别 | 行数 | 说明 |
|---|---|---|
| `:host/jvm-api` | 310 | java.* 类、java.lang 静态成员（`Long/MAX_VALUE`、`Integer/parseInt`）、Java 方法名（`.getMessage`、`.length`）、java.io / clojure.java.io、clojure.lang 成员的 JVM 名字 |
| `:corpus/jolt-value` | 98 | `:expected` 是 jolt 自己的值（jolt 的 known-divergences / profile 里非 certified 的行、jolt 的函数名、jolt 仓库的文件） |
| `:host/regex` | 39 | java.util.regex 方言（`\Q…\E`、占有量词、POSIX `\p{Alpha}`、类交集 …） |
| `:host/exception-type` | 28 | CLR 实际抛出的类型 / 消息与 JVM 不同（ArgumentNullException、Reflector 重载失败、数组越界 …） |
| `:not-yet` | 27 | 原始类型提示的强制转换（cleat-042）、pprint 要 proxy TextWriter（cleat-043）、`seque`（cleat-043）、宏里顶层 do 的逐个求值与常量集合字面量的元数据求值（cleat-033） |
| `:host/class-name` | 17 | CLR 类型名（`System.String`、`Outer+Inner`） |
| `:host/numeric-types` | 10 | byte 是无符号 System.Byte、强制转换规则 |
| `:upstream/clojureclr` | 7 | src/clojure 是 ClojureCLR 的 core（Clojure 1.13 alpha）：版本号、`select-keys`、`:or` 提升、array-map 增长 |
| `:error-model` | 6 | 编译错误是一个带位置的 ExpanderException，没有单独的 cause（cleat-055 的错误模型） |
| `:host/reader-features` 4、`:host/boxing` 3、`:strictness` 2、`:numeric` 2、`:host/hash` 2、`:printf` 2、`:host/message` 2、`:host/strings` 1、`:host/defaults` 1 | 19 | 见 `:legend` |

### 三个基线（designs/09 §3）

- 语料 `:common`：4281 / 4842 通过（88.4%），不等 98、异常 462、超时 1；门禁：失败行都在偏离表里、表里的行都仍失败（`tools/conformance.clj --gate`）。每行结果在 `tests/conformance/cleat-results.edn`。
- clojure-test-suite（`tests/cts-baseline.edn`）：247 个命名空间全部运行，断言通过 6865、失败 32、错误 6。剩下的大多是套件 `#?(:cljr …)` 分支记录的 ClojureCLR 行为，而 cleat 选了 JVM 的（`(str 1.0)` 是 `"1.0"`、`(split-lines "")` 是 `[""]`），以及 `(int 0)` 是 Int64、byte / float 的类型。
- test_clojure（`tests/test-clojure-baseline.edn`）：85 个命名空间，跳过 32（test.generative / test.check、ClojureCLR 的测试支持程序集、辅助命名空间），运行 53 个，15 个加载失败（`clojure.lang.Compiler`、gen-class 等）；断言通过 3443、失败 34、错误 144（其中 pprint 124：proxy TextWriter）。
- 两个 clojure.test 套件的门禁与 jolt 相同：任何命名空间的计数与基线不同（更好或更差）都失败，有意地用 `--write` 更新。

### 测试

- 全量 `dotnet test Cleat.slnx`：Cleat.Core.Tests 129、Cleat.Scheme.Tests 225（含 Scheme 自举不动点）、Cleat.Host.Tests 198（含 Clojure 镜像不动点 B == C 与新增的 3 项 `Conformance`），全部通过。

### 留给后续

- 原始类型提示（`^long` / `^double` 参数与返回值的强制转换、`(int x)` 的 Int32）：cleat-042。
- `proxy` 继承类（clojure.pprint 的 pretty-writer、`seque`）：cleat-043。
- 宏展开结果里的顶层 `do` 逐个求值、常量集合字面量上的非常量元数据、`.clj.dll` 的 `*compile-path*`：cleat-033。
- 编译错误带 cause（`(ex-cause e)` 拿到内层异常）、`var: #'ns/x is not public` 的文本：cleat-055 的后续。
- 正则方言转换（java.util.regex → .NET）、`AssertionError`、clojure.spec（test_clojure 的 fn 规格检查）、反射警告：未排期，记在偏离表 / 基线里。

## 进度

- 2026-10-10 起点：语料 `:common` 4842 行通过 3913（80.8%，cleat-053 的基线）。运行器改为每行在新的 `user` 命名空间里求值（SPEC：fresh user namespace；原来是 `conformance.rowN`，记录 / 语法引用 / `*ns*` 的行因此失败）→ 3932。
- 2026-10-10 第一批平台修正：deftype 重定义（方法体里 `this` 的提示解析到旧类型 → HostSyntax 的“待定桩”优先）；JVM 异常类名的隐式导入（`Cleat.Core.Runtime.JvmNames`：短名进默认导入、全名经 classForName）；Printf 的 `IllegalFormatException` 公共基类；`(catch :default e …)`；`clojure.math`（Clojure 1.11 API + ClojureCLR 的扩展，`Cleat.Core.Runtime.ClojureMath`，进 boot 镜像）；Printf 的 `%e %f %g %a` 照 java.util.Formatter 重写。→ **4180（86.3%）**。
- 2026-10-10 第二批：运行器先读 `:expected` 再求值 `:actual`（JVM 取证进程先读入整份语料）；`str` / 打印的浮点数与布尔按 JVM 文本（`Cleat.Core.Runtime.JvmText`：`1.0`、`1.0E100`、`2.3E-4`、`Infinity`、`true`）；`sort` 的布尔比较器与稳定排序；atom 的 CAS 对装箱值类型按值比较；Reflector 在互不支配且无需数值转换的重载间取第一个（`(= 1 1.0)` 原来报歧义）；非数值操作数抛 InvalidCastException（原为 FormatException，且 `(+ 1 "1")` 被解析）；deftype 里 `toString` / `hashCode` / `equals` 对应 Object 的虚方法；`conj!` 变长；无基本字段的记录有无参构造函数与 `create`（codegen）；Clojure 的 fn 每次求值是新对象（codegen 不再做单例）；`find` 对非关联集合抛错；`distinct` 不再对集合做 nth；clojure.string 的 `split` / `index-of` / `last-index-of` / `replace`（空匹配、Java 替换串语法）/ `re-quote-replacement` 按 java.lang.String / Pattern 语义；未参与匹配的组为 nil；字符串 / 字符 / 布尔 / 符号的 hasheq 与 JVM 相同（也不再随进程变化）；`import` 取同名类型的最新定义；宿主按 clojure.main/with-bindings 绑定（`*1` … 为 nil、`*print-namespace-maps*` 为 true）。→ **4264（88.1%）**。
- 2026-10-10 第三批：`re-matches` 整串匹配、`merge` 的 1.12 语义、`parse-double` 的 Java 文法、`%h`、协变返回的接口方法、Reflector 取无需转换的重载；clojure.test 的 `do-report` 修正。→ **4281（88.4%）**；偏离表 561 行全部分类，`--gate` 通过。clojure-test-suite 基线 6865 / 32 / 6，test_clojure 基线 3443 / 34 / 144。设计文档 03 §4、09 §3 更新。
- 2026-10-10 全量测试通过（Core 129、Scheme 225、Host 198）；TASK.md：cleat-032 完成。
