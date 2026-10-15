# cleat-099 反射警告（*warn-on-reflection*）

- 里程碑：M9　依赖：cleat-097　状态：完成

## 背景
cleat-097 分类 test_clojure 的失败：`clojure.test-clojure.rt` 的 18 条全是 `*warn-on-reflection*` 为真时期望的反射警告，cleat 从不输出。
分析器（`cleat.compiler.analyzer`）决议宿主调用时知道哪些落到了运行时反射（`Reflector`），只是没有报出来。

## 工作内容
1. 分析器：`*warn-on-reflection*` 为真且宿主成员在编译期决议不了时，向 `*err*` 打印一行警告，措辞按 ClojureCLR（test_clojure 的正则）：
   - `Reflection warning, FILE:LINE:COL - reference to field/property NAME can't be resolved (target class is unknown).`
   - `Reflection warning, FILE:LINE:COL - reference to field/property NAME on TYPE can't be resolved.`
   - `Reflection warning, FILE:LINE:COL - call to method NAME on TYPE can't be resolved (no such method).`
   - `Reflection warning, FILE:LINE:COL - call to method NAME on TYPE can't be resolved (argument types: T1, T2).` / `(argument types: unknown)`
   - `Reflection warning, FILE:LINE:COL - call to method NAME can't be resolved (target class is unknown).`
   - `Reflection warning, FILE:LINE:COL - call to static method NAME on TYPE can't be resolved (argument types: …).`
   - `Reflection warning, FILE:LINE:COL - call to TYPE ctor can't be resolved.`
2. 镜像构建与编译器包构建时 `*warn-on-reflection*` 为假（不改输出）；core.clj 里现有的反射调用不受影响。
3. `tests/test-clojure-baseline.edn`：rt 的 18 条转为通过后用 `--write` 更新。

## 验收标准
- test_clojure 的 rt 命名空间 0 失败；全量测试通过；种子不动点。

## 结论（2026-10-15）

### 做了什么
- **分析器**（`cleat.compiler.analyzer`，"反射警告"一段）：`sp-dot` / `sp-new` 在 `*warn-on-reflection*` 为真时调用 `check-host-call` / `check-host-ctor`，向 `*err*`（`RT/errPrintWriter`）打印一行 `Reflection warning, FILE:LINE:COL - …`，七种措辞照 ClojureCLR（test_clojure rt.clj 的正则逐条命中）。判定照 ClojureCLR / JVM 的**编译期语义**：目标类型未知；同名同实参个数的成员一个都没有（no such method / 无实参时 field/property）；多个候选按实参的静态类型选不出一个（argument types: …，未知写 unknown）；构造器同理。
- **实参 / 目标的静态类型**（`host-arg-type` / `host-form-type`）：提示、局部的 `:tag`、字面量（含 `(quote x)` → 其类，正则、关键字…）、`(var …)` / `(def …)` → Var、`(new T …)` → T、Var 的 `:tag`（`str` → String）、`:inline` 展开后的形式（`(int x)` → `RT/intCast` → Int32；`(+ a b)` → `Numbers/add` 的返回类型）、嵌套宿主调用的返回类型（方法、属性、字段，接口含父接口的成员）。
- **重载选择**（`select-overload`）：一个候选直接用（JVM 同）；多个时按 C# 的"更优转换"近似 DLR（ClojureCLR 编译期用它）：同型 > 隐式转换（引用拓宽 / 装箱 / 数值拓宽，§10.2.3 表）> 显式数值转换；两个隐式之间以能否单向隐式转到对方定优劣；**未知实参按 object**：object 形参优于其他，其余平手——于是 `(.IndexOf ^String x y)` 报 unknown（与 ClojureCLR 一致），`(.Write w x)` 选 Write(object) 不报，`(Numbers/add (int a) (int b))` 选 (long,long) 不报。
- **设计决定：警告 ≠ codegen 会不会发静态调用。** `ClojureStaticCalls/Resolve` 另有发射器自己的限制（非 core 的引用类型形参只认 String、泛型方法、属性读取、值类型目标都交给运行时的 Reflector），那些加提示解决不了，不报成反射警告；只有泛型 / params / by-ref 候选时也不报。这部分差距记为 M10 性能方向的候选（codegen 静态决议范围：castclass 任意公共引用形参、属性 getter、值类型实例成员、泛型推断）。
- `parse-dot` 的 `:type` 现在也认字面量目标（`(.ToUpper "abc")` 静态决议）。
- 核心宏 `defonce` / `defmulti` 的 `v#` 加 `^clojure.lang.Var` 提示（JVM 从 DefExpr 推断局部类型，cleat 不推断；不加则用户文件里每个 defonce / defmulti / defprotocol 都误报 hasRoot）。
- **镜像构建关掉警告**（`driver/compile-boot` 置 `an/reflection-warnings?` 为假，与 `inline-calls?` 同法）：core_print.clj 17、gvec.clj 4、instant.clj 6、core/protocols.clj 2，共 29 处真实的未提示宿主调用（目标类型未知的 `.Name` / `.FullName` / `.Contains` / `.Append` 等，`(.ToString ^DateTime d fmt)` 的 fmt 无提示），留待补提示（不影响行为，补了 codegen 还能多发静态调用）。编译器包的源码不开 `*warn-on-reflection*`，不用处理。
- **顺带发现并修复：镜像里的文件再 RT.load 不是空操作。** `clojure.core` 不在 `*loaded-libs*`（JVM 也不在，但 JVM 的 RT.load 对已初始化的 `__init` 类什么都不做），cleat 的 `(use 'clojure.core)` 或任何 `(:require [clojure.core :as c])` 都从源码重载整个 core.clj（两秒多、全部 Var 重定义、`.inl` 内联失效）。clojure-test-suite 的 247 个测试文件都这么写，**此前两套基线实际测的是源码重载后的 core，不是镜像**。`Loader` 现在记下镜像的文件（初始化结束时 `*loaded-libs*` 里的库 + 初始化期间 RT.load 经过的路径），之后 RT.load 它们直接返回（`Loader.IsImageFile`）；构建下一级镜像时 `Program.CompileBootImage` 置 `Loader.ReloadImageFiles`，从源码真正加载。`(require 'clojure.string :reload)` 因此也是空操作（JVM 对 AOT 库同）。
- 测试：`ClojureCompilerTests.ReflectionWarnings`（八条措辞 + 不该报的十余种情形 + 核心宏 + 关掉时无输出）、`ClojureCompilerTests.LoadingAnImageFileAgainIsANoOp`。

### 基线
- test_clojure：`clojure.test-clojure.rt` 4 tests 30/0（原 12/18）；总计 357 tests，3688/18/0（原 3670/36/0）。
- clojure-test-suite：`clojure.core-test.not-eq` 68/3（原 69/2）——新失败的一条是 cljr/clj 分支的 `(is (not (not= ##NaN ##NaN)))`：cleat 的 `(not= ##NaN ##NaN)` 为 true（内联后的 `not=` 比较 double），JVM / ClojureCLR 为 false 只因装箱的 NaN 常量是同一个对象（测试自己的注释称之 arguably buggy）；此前为 false 是因为 core 被源码重载、`not=` 不再内联。归入 cleat-097 的 :cljr-branch 一类。其余 246 个命名空间不变（6866/31/6 → 6866/31/6 的 NaN 项外无变化）。
- 两份基线用 `tools/cts.clj … --write` 重写。
