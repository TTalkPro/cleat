# cleat-146 keyword / symbol intern 与 `(str <long>)` 快路径

- 里程碑：M16　依赖：无　状态：未开始
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
