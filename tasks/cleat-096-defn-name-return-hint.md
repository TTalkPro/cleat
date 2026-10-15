# cleat-096 defn 名字上的原始返回类型提示

- 里程碑：M9　依赖：cleat-095　状态：完成

## 背景
`(defn ^long fib [^long n] …)` 然后 `(fib 10)`：InvalidCastException（`user$fib` 转 `primifs.LO`）。两个编译器时期就有，一直挂着。
原因：`defn` 把名字上的 `^long` 作为 `:tag` 放进 Var 的元数据并**求值**，存的是函数 `clojure.core/long` 本身；函数类按宏传下来的 `:rettag`
（符号 `long`）实现 `primifs.LL`，而调用点 `var-prim-sig` 只认符号，算出 `LO`。JVM Clojure 同样的写法报 `AbstractMethodError`
（`invokePrim(J)Ljava/lang/Object; is abstract`），即两边本来一致地"坏"。

## 工作内容
1. 分析器 `var-prim-sig`：Var 的 `:tag` 是函数 `clojure.core/long` / `double` 时认回符号，调用点与函数类的签名一致。
2. 测试：Host 进程级测试里加 `^long` 在名字上与在形参表上的两种写法；conformance 不加（JVM 是错误，cleat 是正确结果，不比较）。

## 验收标准
- `(fib 10)` → 55；`(defn fib2 ^long [^long n] …)` 照旧；种子不动点；全量测试通过。

## 结论（2026-10-15）

完成。真正的原因有两处，都在 `cleat.compiler.analyzer`：
1. 分析 `def` 时预先放到 Var 上的元数据只有 `:arglists`（供体里的自调用选签名），没有名字上的 `^long`：自调用 `(fib (dec n))` 据此算出
   `LO`，而函数类按 `:rettag` 实现 `LL`——这就是 InvalidCastException 的来源（`sq` 这种不自调用的函数一直是好的）。现在 `:tag` 是
   `long` / `double` 时一并预放。
2. 定义完成后 Var 的 `:tag` 是求过值的函数 `clojure.core/long`（`defn` 把名字的元数据当表达式求值，JVM 也如此），外部调用点
   `var-prim-sig` 只认符号。加 `var-rettag`：认回 `long` / `double`。
JVM Clojure 对 `(defn ^long fib [^long n] …) (fib 10)` 报 `AbstractMethodError: invokePrim(J)Ljava/lang/Object; is abstract`
（实测 clj），即两边原本一致地"坏"；cleat 现在正确返回 55，是 JVM 的超集，不进偏离表。
测试：`ClojureCompilerTests.PrimitiveReturnHintOnTheDefnName`（名字上 / 形参表上的 `^long`、`^double`、无原始形参的 `^long id`）。
编译器源码改了：种子重生成，不动点与全量测试见提交信息。

