# cleat-147 `RT.nth` 非 Indexed 路径、闭包提升与变长参数数组

- 里程碑：M16　依赖：无（建议排在 cleat-142 ~ 146 之后）　状态：未开始
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
