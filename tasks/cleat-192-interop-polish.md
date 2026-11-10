# cleat-192 互操作打磨：静态属性类型、泛型短名导入、零散的加载错误

- 里程碑：M23　依赖：无　状态：完成（2026-11-10）

## 问题（183 ~ 186 期间与 test_clojure 基线里遇到的）

1. **静态属性的返回类型推断不出**：`(.Ticks DateTime/UtcNow)` 报 "reference to field/property Ticks can't be resolved (target class is unknown)"，
   成了反射调用。core.async 的 timeout 线程里满是这种（`timers.clj` 的反射警告），热路径上是反射。
2. **带类型实参的泛型短名导入**：`(:import [System.Collections.Generic |Dictionary`2[System.Object,System.Object]|])` 之后
   `|Dictionary`2[System.Object,System.Object]|` 解析不了（cleat-167 只修了不带类型实参的 `T`1`）。
3. test_clojure `array-symbols`：编译时 `Cannot invoke member MakeArrayType on nil`（数组类型符号 `String/2` 之类的某个分支）。
4. test_clojure `method-thunks`：`Guid/Parse` 作方法值（无参调用形式）报 "No matching static method Parse … with argument types ()"。
5. test_clojure `compilation2`：找 `clojure.test_clojure.compilation.line_number_examples.clj.dll`（按 ClojureCLR 的 AOT 文件名约定加载编译产物）。
6. test_clojure `clearing`：`java.lang.reflect.Field`（JVM 类，查明测试的意图后改写或记偏离）。

## 工作内容

逐条查明并修（或判定为偏离并记账）；每条带回归测试。1 修好后复查 core.async / tools.analyzer 包的反射警告数量。

## 验收

- 1 ~ 4 修好并有测试；5、6 有处置；门禁通过。

## 结果

| 项 | 原因 | 处置 |
|---|---|---|
| 1 静态属性类型 | 分析器的表达式类型只看符号自己的 / 局部的 `:tag`：`T/m` 与全局 Var 都是"未知" | 修：`T/m` 是静态字段 / 属性时取其类型（`StaticFieldOrPropertyType`）；全局 Var 取 Var 的 `:tag`（`VarValueTag`，同 JVM 的 VarExpr；fn 的 Var 与原始类型提示不用）；`HostSyntax.TagType` 遇 `Type` 直接用（原来转全名再解析，实参是求值程序集里的类型时解析不回来）、泛型语法的提示也走 `CompileTimeTypes`；loop 局部按初值定类型（同 JVM，存储仍是 Object）。core.async timers.clj 反射警告 9 → 0；core.async + tools.reader 54 → 18（余下是上游没写提示的） |
| 2 泛型短名带实参 | cleat-167 只认短名 `T`1` 本身 | 修：`HostSyntax.ImportedGeneric`——`Short`N[实参]` 经导入找到泛型定义，再按全名加实参解析（MaybeType 与 TagType 都用） |
| 3 array-symbols | ① genclass 的 `the-array-class` 元素类型只按 `System.X` 找，本命名空间定义的接口找不到（nil.MakeArrayType）；② `resolve` 不认 `String/2`；③ `RT.classForName "user.Foo[]"` 在 AppDomain 里先碰到 gen-interface 的编译期桩，接口签名引用了桩程序集 | 修：① 元素类型先查命名空间映射（JVM 的 maybeArrayClass）；② `maybe-resolve-in` 认数组类符号，元素找不到抛 TypeNotFoundException；③ `Loader.FindType` 对 `T[]` / `T[,]` 按元素类型找。44 个断言全过 |
| 4 method-thunks | Clojure 1.12 的限定方法值与 param-tags 整个没有实现：`T/m` 在值位置被当作 0 参调用，调用位置的 `^[…]` 被忽略 | 实现（`Analyzer/MethodValues.cs`，designs/03 §4 表）：值位置改写成 `fn*`（param-tags 一个元数、原始类型 `RT.xCast` 强制转换、引用类型带提示；无 param-tags 按重载元数各一个子句、运行期反射）；静态字段 / 属性照旧；调用位置同样转换实参；没有的方法 / 构造器编译期报错（措辞含 static method / instance method / constructor）。15 个断言全过 |
| 5 compilation2 | 按 ClojureCLR 的 AOT 文件名 `<ns>.clj.dll` 先 `assembly-load-from`，还要 test.generative 与 AOT 编译 examples | 偏离：移入跳过表（原因写明）；cleat 的包程序集命名不同，不为测试仿造 |
| 6 clearing | 上游（ClojureCLR）所有测试都注释掉了（CLR 不做局部清除），只剩 `(:import [java.lang.reflect Field])` | 移入跳过表：没有可跑的测试；JVM 类不模拟（cleat-168） |

**基线**：test_clojure 加载不了 5 → 1（只剩 api，cleat-190），运行 52 → 50（跳过 33 → 35），测试 439 → 445，断言通过 4223 → 4282，失败 / 错误不变（43 / 4）。
CTS、conformance（521 / 521）不变。

**测试**：`ClojureCompilerTests.InteropPolish`（`tests/Cleat.Host.Tests/data/interop_polish.clj`）。

## 门禁（2026-11-10，13）

- Core 116 / Compiler 50 / Host 295 全过（Host 17 分 50 秒）
- image-set --twice --ilverify：全部通过
- conformance --gate：521 分类偏离 / 521 失败行，OK
- CTS：6868 / 29 / 6，基线 OK
- test_clojure：50 个命名空间（35 跳过、1 加载不了），445 测试，4282 / 43 / 4，基线（本任务重写）OK
