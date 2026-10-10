# cleat-040 原生 core：持久集合、惰性序列与协议（Clojure 实现）

- 里程碑：后续（LLVM）　依赖：cleat-039　状态：搁置（2026-10-10 决定先完成 CIL 能力）
- 设计依据：[06 §1 D3](../designs/06-llvm-backend.md)

## 工作内容
1. `native/core/`：协议定义（`ISeq`、`ISeqable`、`ICounted`、`IIndexed`、`ILookup`、`IAssociative`、`IMap`、`ISet`、`IStack`、`IReduce`、`IKVReduce`、`IFn`、`IDeref`、`IMeta`、`IWithMeta`、`IEquiv`、`IHash`、`IComparable`、`IPrintWithWriter`…）；`List`、`Cons`、`LazySeq`、`PersistentVector`（含 transient 与 `ChunkedSeq`）、`PersistentArrayMap`、`PersistentHashMap`（HAMT）、`PersistentHashSet`、`PersistentTreeMap/Set`、`Range`、`Atom`、`Volatile`、`Delay`、`Reduced`、`MultiFn`、打印。参照 ClojureScript `core.cljs`（EPL，注明来源）。
2. `src/clojure/core.clj` 的 `#?(:cleat-llvm …)` 分支：把 `clojure.lang.RT`/`Numbers` 调用改为原语或原生 core 函数；`core_deftype.clj` 的协议实现改为原生协议表。
3. 原生 boot 构建：`tools/build.cs native-core` → `libcleat-core.a`（缓存）。
4. 测试：原生 `compile-small` 加集合用例；集合的哈希与 `=` 与 CLR 侧一致。

## 验收标准
- 原生目标能加载 `clojure.core` 并运行 `(println (reduce + (map inc (range 100))))`。
