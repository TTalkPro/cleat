# cleat-065 集合字面量上的非常量元数据求值

- 里程碑：后续　依赖：cleat-064　状态：未开始
- 设计依据：JVM Clojure 的 `VectorExpr` / `MapExpr` / `SetExpr` 带元数据时生成 `MetaExpr`（元数据表达式求值后 `withMeta`）；
  发现于 [cleat-062](cleat-062-nrepl-package.md)。

## 背景

`(let [x 1] (meta ^{:a x} {:b 1}))` 在 JVM 上是 `{:a 1}`，cleat 是 `{:a x}`：集合本身是常量时，`datum-expression`
（`src/scheme/clojure/syntax.sls`）把元数据原样当常量引用。元素里有符号时，元数据经元素里找到的标识符取上下文求值（`find-id`），
所以只有"常量集合 + 非常量元数据"出错。nREPL 的 middleware 测试用 `^{::descriptor {:requires #{n #'v}}} {:dummy …}` 写中间件，
cleat-062 改成显式 `with-meta`。

## 工作内容

1. 常量集合 + 非常量元数据：生成 `WithMetaOf`（与非常量集合同一路径），元数据在字面量的词法上下文里展开。
2. 难点：字面量的子项都是常量时没有标识符可作 `datum->syntax` 的模板；psyntax 的 `datum->stx` 能直接用语法对象的 wrap，
   但不导出。方案二选一：展开器核心导出一个"取语法对象的 wrap 造标识符"的过程（改 `boot/expander/core.sls` 的导出，两步重生成种子），
   或者让 Clojure 集合的语法扩展把元数据里的符号也带上 wrap（`syntax-kinds.sls`，要保证省略号模板与 `syntax-ext->list` 不受影响）。
3. 元数据里的源位置键（`:line` `:column` `:source-span` …）不参与求值，照现在剥掉。

## 验收标准

- 上例得 `{:a 1}`；`^{:tag String} []` 等常见写法不变；种子不动点；全量测试通过；nREPL 的 middleware 测试可以改回上游写法。
