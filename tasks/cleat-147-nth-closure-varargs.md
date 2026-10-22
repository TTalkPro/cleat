# cleat-147 `RT.nth` 非 Indexed 路径、闭包提升与变长参数数组

- 里程碑：M16　依赖：无（建议排在 cleat-142 ~ 146 之后）　状态：部分完成（第 1 条 nth 落地；第 2 / 3 条量化不足，见结论，交 M17）
- 设计依据：[28 §3](../designs/28-hot-path-optimizations.md)

## 问题

复核修正后的三条 P1（都有 cleat 侧绝对成本或真实分配证据）：

1. `(nth xs 1)` 在 cleat 侧 15.7 ~ 16.9 ns，且 RT 直调与通用调用同价 → 成本在本体：
   `RT.nth` 对非 Indexed 走 `NthFromRef` → `NthFrom` 的类型判断链，ref 转发是 cleat-117
   为绕开 tier-0 形参活性问题加的。
2. 每调用重建的闭包：peval 的 `ref_count$rc$fn__20` 占该项分配 17%（1.02 GB）。
3. 变长调用的参数数组：`System.Object[]` 在 peval 占分配 4.0%、browse 占 1.8%，
   其中来自 RestFn / invoke 参数数组的比例未量化。

## 工作内容

1. `RT.nth` 非 Indexed 路径：对已知 seq 类型（PersistentList / LazySeq / chunked / StringSeq）
   直接分派，缩短 `NthFromRef` → `NthFrom` 的判断链；保留 cleat-117 的活性语义
   （不重新引入 tier-0 多抓一轮的问题），必要时只在优化编译单元去掉 ref 转发。
2. 闭包提升：捕获变量全为静态 / 未被改写时，把 `fn` 闭包提升为单例（lambda 提升 + 常量闭包缓存）。
   cleat-105 量化后未实现，本次以 peval 的真实分配画像为依据重开。
3. 参数数组：先用 alloc.cs 量化 RestFn / invoke 参数数组的占比，再决定
   （已知元数直发避免数组，或小数组复用）；量化不足就只记录结论关闭本条。
4. 每条各自 `CLEAT_NO_OPT` 开关（`nthfast` / `closurlift` / `varargs`）。

## 验收

- 语义用例：`nth` 的 not-found / 越界 / 各 seq 类型、闭包的捕获与可变性（`volatile!` / 循环变量）、
  变长调用与 `apply` 的副作用顺序；开 / 关输出逐字相同。
- alloc.cs：peval 的闭包与 `Object[]` 分配量下降。
- 同窗口 A/B：peval / dynamic / divrec 等受益、array1 与 tier-0 相关项不回退；
  门禁与两套基线不变；改 codegen 后种子不动点。

## 结论（2026-10-20）

**第 1 条 `RT.nth` 非 Indexed 路径：已落地，开关 `nthfast`。**

- 实现：`RT.NthFromRef` 与 `RT.nth(coll, n, notFound)` 各加一段快路径（受 `s_nthfast` 门控，
  `CLEAT_NO_OPT=nthfast` 时完整退回旧判断链，开 / 关是同一份运行时的对照）：
  string 直读 + `CharBox`、数组走 `Array.GetValue`（旧顺序）、`ISeq` 直接在 ref 入口遍历
  （去掉旧 `NthFrom` 的二次调用与 `RT.seq`）、`LazySeq` 先 `seq()` 一次、`StringSeq` 按
  `I + n` 直接索引、`PersistentList` 用 `count()` 提前判 miss（只对 O(1) count 的类型，
  一般 seq 的 count 可能 O(n)，不放热路径）。**保留 cleat-117 的 ref 转发与形参活性语义**：
  进入遍历前显式把 `coll` / `iseq` / `lazy` 置 null，不重新引入 tier-0 多抓一轮的问题。
- 探针（本机、同轮 `MODE=on/off` 配对、每次 20M 次、减去 overhead ~0.9 ns，ns/次，取三轮最小）：
  vector 19.46 → 16.80（-13.7%）、list 37.88 → 34.01（-10.2%）、string 15.14 → 11.92（-21.3%）、
  StringSeq 70.89 → 58.71（-17.2%）、lazy 44.09 → 40.65（-7.8%）、object-array 28.96 → 26.80（-7.5%）、
  chunked 98.50 → 94.32（-4.2%）。索引随循环变量变化、结果写进 sink，不是死值探针（cleat-149 §5.1 口径）。
- 语义：新增 `tests/Cleat.Host.Tests/data/nthfast_semantics.clj` + `.out`（vector / list /
  含代理对的 string / StringSeq / chunked / lazy / **无穷 lazy 的 not-found** / object 与 long 数组 /
  负索引 / 越界 / `nth` 三参默认值），`NthFastPaths` 断言开与关输出逐字相同。
- 门禁：串行复跑 `tools/conformance.clj tests/conformance/corpus.edn --gate` 两次 ——
  `CLEAT_NO_OPT=nthfast` 与默认（开）**都是** `rows 4842 / pass 4320 / fail 77 / error 444 / timeout 1`、
  `gate: 521 classified divergences, 521 failing rows`、`gate: OK`，与 main 基线逐字相同。
- Core 全量 123/123。

**诚实记录的两次假失败**：第一次 Host 全套在 144 / 148 / 149 同机并发时出现 3 个失败
（`CorpusCommonRowsMatchTheDivergenceTable` 报 3 条 `nth` / `rand-nth` / `pop` on nil 的新发散、
`Nrepl`、`ClojureTestSuiteMatchesItsBaseline`）。串行复跑后语料三条逐字通过、gate 与基线相同，
确认是并行污染（submodule 未 checkout、共享 `/tmp/cleat-clojure-bootstrap`、nREPL 端口与捕获 stderr 串台、
load 5 ~ 8 的超时），不是 `nthfast` 的语义问题。分类见 `notes/m16-regression-triage.md` §6。

**第 2 条闭包提升、第 3 条变长参数数组：M16 当轮量化不足以支撑改动，未实施。**
`bench/r7rs/src/cleat147_alloc_probe.cljc` 落了七相位探针（none / closure-none / closure-static /
closure-mutable / fixed / rest0 / rest2），读数是"进程总分配 + 类型直方图"：闭包相位每次迭代多
`cleat147_alloc_probe$run$fn__N` 一类闭包对象，`rest0` / `rest2` 里 `System.Object[]` 从 2340 MB 升到
4623 MB 并带出 `clojure.lang.ArraySeq_object` 2285 MB —— 方向与归因实验一致，但各相位的 `n` 不同，
而 harness 自身基线（`CallKey` ~3.4 GB、`System.Type[]` ~2.3 GB、`System.Int64` 3.4 ~ 4.6 GB）
与被测相位混在同一进程里，**没法把"每次迭代多少字节"归一出来**。按 designs/28 §3 的纪律
"量不出就不改"：闭包单例化要动捕获可变性（`volatile!` / 循环变量 / `set!`），
没有可归一的分配证据 + 语义用例不足以安全落地。M17 重做方式：同一 `n` 跑基线相位并逐项相减，
或直接改用 `GC.GetAllocatedBytesForCurrentThread()` 包住计时段。

**M17 量化基建已就位（2026-10-22，探针重写）**：`cleat147_alloc_probe.cljc` 改成七相位同进程、
同 `n`（缺省 2000 万，可传参），每相位 warm-up 后用 `GC.GetAllocatedBytesForCurrentThread()`
（.NET 线程累计分配）包住迭代段，JVM 侧用 HotSpot `ThreadMXBean.getThreadAllocatedBytes`
（不支持则明确报 unavailable，不混用进程级读数），输出 bytes/iter 净值与配对差值
（closure-* 相对 none / closure-none，rest* 相对 fixed）；热循环改 primitive 累加器，
消掉旧 sink 数组约 240 B/iter 的 `Int64` 装箱基线，`fixed3` 改 primitive 签名后固定元数基线归一到 0。
本机读数（net-empty 口径，cleat）：无捕获闭包 24 B/iter，捕获静态变量 +32，捕获 `volatile!` +56，
`rest0` / `rest2` 相对固定元数基线 +120 / +112（RestFn 调用总增量，含 `Object[]`、装箱与
`ArraySeq_object`，不是单独的 `Object[]` 尺寸）——可分辨、可归一，第 2 / 3 条的决策可以在这份
表上做；注意这是分配成本，是否转成活体实现仍要回到 28 §3 的"量不出时间收益就不改默认路径"。

## 验收对照

- 语义用例开 / 关逐字相同：已做（`NthFastPaths`）。
- 门禁与两套基线不变：语料 gate 已按基线逐字复跑；CTS / test-clojure 与 Host 全套由合并后的 main 串行复跑
  （并行窗口里那三项失败属环境性，见上）。
- 改 codegen 后种子不动点：本任务只改 `RT.cs` 运行时，不动 Clojure 源码 → 种子不受影响。
- 同窗口 A/B（peval / dynamic / divrec / array1）：交 cleat-150 的整体三臂，`nthfast` 需并入
  `bench/r7rs/m16full.py` 的 `DEFAULT_NO_OPT` 名单，否则关侧不是真 M15 行为。

## cleat-150 收口时抓到并修正的回归（2026-10-20，commit c960f7c）

- 合并 main 后的 CTS 复跑发现 `clojure.core-test.nth` 由 78/0 变 76/2：
  `(list)` 返回 `PersistentList+EmptyList`，它也实现 `ISeq` 但 `seq()` 为 nil；
  快路径直接把 ref 形参当节点走，空列表的 `first()` 恒为 nil，
  arity-2 的 `(nth (list) 0)` 因此不抛、arity-3 返回 nil 而不是默认值
  （arity-3 里 `is PersistentList` 的 O(1) count 守卫对 EmptyList 恒不成立——它是嵌套子类，不是 `PersistentList` 实例）。
- 修正：arity-2 / arity-3 的 ISeq 分支统一先过 `RT.seq(iseq)` 归一
  （ASeq 仍是一条 isinst 直接返回自身，LazySeq 的 realize 已在 `RT.seq` 内完成，去掉手写 lazy 分支），
  非空遍历与 StringSeq 直读成本不变，空列表 / 耗尽序列路径与旧 `NthFromSeq` 逐字一致。
- `nthfast_semantics.clj/.out` 补空 list / 空 vector / `(seq (list))` 五条，开 / 关逐字相同；
  修正后 CTS 6868 / 29 / 6、test-clojure 384 / 3877 / 25 / 4 回到基线（关侧 357 / 3688 / 18 不变）。
