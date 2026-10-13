# cleat-064 移植 nREPL / neorepl 时发现的缺陷：宏展开的源位置、以 - 开头的协议方法、带超时的 deref、ReaderException 的消息、spit 不截断

- 里程碑：后续　依赖：cleat-053　状态：完成
- 设计依据：JVM Clojure 的 `Compiler.eval` / `analyzeSeq`（LINE / COLUMN 动态绑定）、`HostExpr` 的成员名解析、
  `java.util.concurrent.Future.get(timeout)`、`LispReader.ReaderException(cause)`、`clojure.java.io` 的 `:append`；
  发现于 [cleat-062](cleat-062-nrepl-package.md) 与 [cleat-063](cleat-063-neorepl-package.md) 的测试。

## 背景

移植 nREPL 1.8 与 neorepl 0.1 的测试套件时（cleat-062、063），几处 cleat 与 JVM Clojure 的偏差让上游测试失败；它们不限于
这两个包：nREPL 的 `lookup`、neorepl 的 `,where` / `,edit` / `,source` 都依赖第一处，`spit` 的缺陷连 test_clojure 基线文件都写坏了。

## 工作内容

1. **宏展开出的 def 没有源位置**：`(defn f …)` 定义的 Var 的 `:line` / `:column` 恒为 0（连 `clojure.core/map` 也是），
   只有直接写出的 `(def …)` 有。原因有两层：
   - 顶层形式先在 Clojure 数据层面 macroexpand（`clojure-eval-toplevel`，即 JVM 的 `Compiler.eval`），展开结果不带原形式的元数据；
   - 嵌套的宏调用经 `macro-transformer` 展开，结果转语法对象时没有源位置。
   JVM 的做法是分析时绑定 LINE / COLUMN，def 取当前值。cleat 的对应：顶层展开结果用 `transfer-source` 带上原形式的
   `:line` / `:column`（自己有的不动）；嵌套展开（宏、`.method` 语法糖、`:inline`）转语法对象时以调用处的注解源位置作
   外层位置，结果里没有自己位置的子形式沿用（`clj->scheme` 加可选的初始位置，`clj-datum->syntax*` 加第三个参数）；
   顶层 `(do …)` 拆开逐个求值时，子形式也带上 `do` 的位置（`(defmacro m …)`、`(ns …)` 展开成顶层 `do`）。
   用户层面的 `macroexpand` 结果不变（仍不带元数据，与 JVM 相同）。
2. **以 - 开头的协议方法**：`(defprotocol P (-m [x y]))` 的接口方法叫 `_m`，deftype / defrecord 也实现 `_m`，但协议函数里的
   `(. x (-m y))` 被当成字段访问（去掉 `-` 找成员 `m`），抛 MissingMethodException。按 JVM 的 HostExpr：`-name` 只在成员是
   单独的符号时是字段，`(. x (-m …))` 是名为 `munge("-m")` 的方法。
3. **带超时的 deref**：`(deref fut ms v)` 在 future 出错或被取消时返回值（nil），不像无超时的 deref 那样抛出——ClojureCLR 的
   `Future.deref(long, object)` 原样如此。改为线程结束后走 `deref()`；超时大于 Int32.MaxValue 毫秒时不再回绕。
4. **ReaderException 的消息**：ClojureCLR 传 null，消息成了 .NET 的 "Exception of type ... was thrown."；JVM 的 `super(cause)`
   取 `cause.toString()`。改为 "原因类型: 原因消息"（`(ex-message e)` 于是说出 "Unmatched delimiter: )"）。
5. **spit 不截断、不认 :append**：`clojure.clr.io` 写文件用 `FileMode/OpenOrCreate`，`(spit f "ab")` 写在 "hello world" 上得
   "abllo world"，`:append true` 也被忽略——ClojureCLR 原样如此。改为默认 `FileMode/Create`（截断），`:append true` 时
   `FileMode/Append`，与 clojure.java.io 相同。


**另记**：集合字面量上的非常量元数据不求值（`(let [x 1] (meta ^{:a x} {:b 1}))` 得 `{:a x}`，JVM 得 `{:a 1}`）。修它要让
展开器给常量字面量造带其词法上下文的标识符，需改展开器核心的导出并两步重生成种子，另立 [cleat-065](cleat-065-literal-metadata-evaluation.md)。

## 验收标准

- 各处行为与 JVM 相同，有回归测试；test_clojure 基线只朝好的方向变；镜像不动点；全量测试通过。

## 结论（2026-10-13）

完成。

- **源位置**：`syntax.sls`（`transfer-source` 导出；内联、`.method` 语法糖、宏 Var 开头的调用传调用处位置）、`namespaces.sls`
  （`syntax-source`、`clj->scheme/at`、`macro-transformer` 传位置）、`driver.sls`（顶层展开结果 `transfer-source`）。
  `clojure.core/map` 现在是 `{:line 2750, :column 1}`。展开器报错的位置也跟着变准：test_clojure 基线里四个命名空间的加载错误
  从 `(…:0:0)` 变成真实行列，`clojure.test-clojure.repl` 多过 2 条断言（`source` 依赖 `:line`），基线已有意更新（3668 → 3670 通过）。
- **协议方法**：`parse-dot` 的 `prop?` 只在非方法调用形式时成立。
- **deref**：`Cleat.Core/clojure.lang/Lib/Future.cs`。
- **ReaderException**：`Cleat.Core/clojure.lang/Lib/LispReader.cs`。**spit**：`src/clojure/clojure/clr/io.clj` 的 `file-mode`。
- **基线**：顶层 `do` 的子形式带位置后，又有九个命名空间的加载错误从 `(…:0:0)` 变成真实行列（多在 `ns` 形式里）；
  原来的基线文件末尾多出一段——正是 `spit` 不截断留下的旧内容，修好后重写干净。
- **测试**：`tests/Cleat.Host.Tests/PortFoundFixesTests.cs`（5 项）。全量 `dotnet test` 通过（含两段自举不动点与三个符合性基线）。
  Scheme 元层的种子不受影响（只改了 `src/scheme/clojure/` 的宿主库，不在种子里）。
