# cleat-146 keyword / symbol intern 与 `(str <long>)` 快路径

- 里程碑：M16　依赖：无　状态：完成（2026-10-20，commit 6063c39）
- 设计依据：[28 §2.4](../designs/28-hot-path-optimizations.md)

## 问题

`(keyword (str 7))` 在 cleat 侧 52.5 ns；探针分配画像里 `clojure.lang.Symbol` 占 42%。
browse 的 `generate-symbol`（`(keyword (str @current-gensym))`）是真实热路径，
scheme / dynamic / lattice / paraffins 同型。复核修正后本条按**绝对成本 + 分配量**论证，
不按对 JVM 的倍数（JVM 的 intern 也先建 Symbol）。

## 工作内容

1. intern 查找前置：`Symbol.intern` / `Keyword.intern` 在命中已有实例时不新建对象
   （按 ns / name 字符串直接查表返回；未命中才走现有分配路径）。
2. `(str <long>)` / 整数 → 串：专用路径避免中间 String 拼接与重复哈希
   （String.Concat 路线已在 cleat-124 证伪，此处只做整数格式化）。
3. `keyword` / `symbol` 对已是目标类型的入参恒等返回，核对与 JVM 的元数据语义一致。
4. `CLEAT_NO_OPT` 开关（如 `intern`）保留旧路径。

## 验收

- 语义用例：intern 的 identity（同一 name 返回同一实例）、元数据、`::` 别名解析、
  gensym 唯一性；开 / 关输出逐字相同。
- alloc.cs：browse / scheme 的 `Symbol` 分配量显著下降。
- 同窗口 A/B：browse 及受益项可测收益、无回退；门禁与两套基线不变；按需重生成种子。

## 结论

按收口决策只做两项：`Keyword` 的 ns / name **字符串键查找前置**，以及 `RT.CultureToString`
的 `long` / `int` 不变区域性格式化。

- `Keyword.cs` 新增 `readonly struct StringKey(ns, name)` 键表与"原文键"表（`intern(nsname)`
  命中时连 `Substring` 都省掉）；三张表在同一把 `_keyMapSync` 锁下同步更新，死引用清理一起清。
  `_symKeyMap` 仍是 `intern(Symbol)` / `All()` / 反序列化的权威表，字符串表只是热路径索引。
- `Symbol` 继续按上游语义**不唯一化**，`intern(Symbol)` 与带元数据 symbol 的路径保持现状
  （symbol 副本身份不能当可丢弃键）；`keyword-with-meta` 的 cleat / JVM 既有差异仍只记录不修。
- `CLEAT_NO_OPT=intern` 同时关掉两项，完整回到"先建 Symbol + `String.Format`"的旧路径。

**要订正的证据链**：任务书原写"browse 的 `generate-symbol` 是真实热路径"，实测 browse 的 char 常量
与关键字**不经过** `Keyword.intern`（alloc 里 `clojure.lang.Symbol` 只有 2 ~ 3 MB），
browse 的分配大头是 `MPair` / `System.Char`；本项的真实收益在 scheme 这类动态构造 keyword 的负载
与"命中路径零分配"本身。browse 残留的 `Symbol` 52.4 MB / `WeakReference` 89.6 MB 来自
`Symbol.intern` 自身的唯一化，改它会动 identity 语义 → 归 cleat-145 / 147 的调用与访问器路径。

## 验证（2026-10-20）

- 语义：`intern_semantics.clj`（identity / 元数据 / `find-keyword` 三态 / symbol 文本 /
  `keyword` ↔ `symbol` 恒等 / 整数与浮点文本 / gensym 唯一性 / 字符串键 interning 后跨表一致
  + 70 次 GC 压力）默认与 `CLEAT_NO_OPT=intern` 输出与 `.out` 逐字相同；
  Host 测试 `InternAndIntegerStringFastPaths` 通过。
- 门禁 / 基线（合并 main 6063c39）：Release 0 error / 13 warning、Core 123/123、Host 278/278、
  `gate: OK (521)`、CTS `cts` 6868/29/6 与 `test-clojure` 3688/18 与基线一致；
  不改 codegen，`clojure-seed` 重生成后 `src/boot/seed` 无变动。
- 微基准：`(keyword (str 7))` 29 → 10 ns/次（本地，3 次取最小）。
- 分配（本地 `tools/alloc.cs`）：scheme 的 `System.String` 213.4 → 140.9 MB、总分配 1993.8 → 1914.4 MB；
  `clojure.lang.Symbol` 退出 top（browse 2.1 MB / scheme 5.2 MB 量级）。
- 远端同窗口开 / 关 A/B（3 次取最小，秒）：与 142 / 143 合并后 browse 5.728 → 5.503、scheme 16.755 → 17.063、
  lattice 10.338 → 10.987、paraffins 13.028 → 13.323；**单开关隔离**（只关 intern）lattice 10.819 /
  paraffins 13.501 / scheme 17.367 / slatex 10.201，即 scheme 与 paraffins 的 +1 ~ 2% 差异在本轮噪声内
  （11 项的开关极差 1 ~ 8%），**不据此声明端到端 wall-time 收益**。本项成立的部分是命中路径零 Symbol
  分配与微基准下降。详见 notes 的 M16 §1 / §2 / §3。
