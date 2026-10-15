# bench — 基准集（cleat-042）

运行与聚合：

```
dotnet run tools/build.cs -- bench [-r 次数] [--write] [--only 正则]
```

- `bench/` 下每个 `*.clj` 是一个基准：`(ns bench.<名字>)` + `-main`，跑若干轮并逐轮打印 `BENCH <名字> <毫秒>`。
- 聚合打印最小 / 中位；`--write` 把 `{:name … :min … :median …}` 写到 `bench/baseline.edn`（优化对照的基线）。
- 每个基准一次宿主进程（进程启动 ~1.2 s 不计入：时间在基准内部用 Stopwatch 测）。

| 基准 | 内容 | 规模 |
|---|---|---|
| sum_loop | recur 循环的装箱算术（`+` / `<` / `inc`） | 500 万次迭代 |
| fib | 朴素双递归（调用 + 算术 + 分支） | fib 25（~24 万次调用） |
| reduce_vec | 持久向量构造 + `reduce` | 100 万元素 |
| lazy_chain | 惰性序列链 `map` + `filter` + `into` | 30 万元素 |
| assoc_map | 哈希 map 的 `assoc`（1000 键循环） | 20 万次 |
| protocol | defprotocol / defrecord 的分派 | 40 万次调用 |
| keyword_invoke | `(:k m)` 关键字调用快速路径 | 50 万次 |
| prim_fib | `^long` 形参与返回的双递归（原始签名） | fib 27（~63 万次调用） |
| untyped_loop | 初值来自无提示参数的 long / double 循环（cleat-100） | 各 500 万次 |
| nested_arith | 无提示参数上的嵌套算术 `(+ s (* (- a b) (+ a b)))`（cleat-100） | 500 万次 |
| ctor | deftype / defrecord 工厂 / BCL 构造器（cleat-100） | 各 100 万次 |
| property | `(.Length ^String s)` 一类属性读（cleat-100） | 100 万次迭代，2 次读 |
| mbrot | Mandelbrot 计数，数值全部从无提示参数进入（cleat-100；skeg r7rs 的 mbrot 形状） | 500×500，最多 100 次迭代 |

`baseline.edn` 是 M10 起点（2026-10-15，cleat-100 `bench --write`）的数字，M10 各任务在它之上对照；cleat-042 时期的基线与各步数字见 tasks/cleat-042。

分配采样：`dotnet run tools/alloc.cs -- [--top N] -I bench -m bench.<名字>`（在本进程里跑宿主，按类型汇总 GCAllocationTick；`--first-chance` 打印每个异常，排错用）。
