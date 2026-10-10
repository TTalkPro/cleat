# cleat-021 常量编码与 fasl

- 里程碑：M3　依赖：cleat-019、cleat-006、cleat-007　状态：未开始
- 设计依据：[05 §3](../designs/05-cil-backend.md)

## 目标
编译器侧的中立编码（Clojure，`cleat.compiler.constants`，先用 C# 原型验证格式）与发射器 / 运行时的二进制 fasl。

## 工作内容
1. 编码规格（05 §3）写入规格附录；map / set 排序规则；共享 `def`/`ref`。
2. `Cleat.Emit.FaslEncoder`：编码 → 字节（标签 + 变长计数 + UTF-16 字符串），Deflate，嵌入 `$Constants` RVA 数据；`ldconst` 展开。
3. `Cleat.Core.Runtime.Fasl`：`Read`（重建对象；`Var.intern`、`Keyword.intern`、`defrecord` 的 `create`、`Type.GetType`）与 `Write`（供 cleat-036 种子与接口文件）。
4. 测试：全部编码的往返；共享与环；`-0.0`；带元数据的集合；`#inst`/`#uuid`/`#"re"`。

## 验收标准
- 往返测试通过；同一输入两次编码逐字节相同。

## 结论（2026-10-10）

完成。
- 编码规格按 05 §3，写入 `src/Cleat.Emit/LCIL.md` 引用的设计文档；标签值在 `Cleat.Emit.FaslEncoder` 与 `Cleat.Core.Runtime.Fasl` 两处一致（魔数 `CLFASL`，版本 1）。编码形式：`(nil)` `(true)` `(false)` `(long n)` `(double "16 位十六进制位模式")` `(bigint "…")` `(bigdec "…")` `(ratio "n/d")` `(char 码元)` `(str "…")` `(sym "name")` / `(sym "ns" "name")` `(kw …)` `(var "ns" "name")` `(list …)` `(vec …)` `(set …)` `(sorted-set …)` `(queue …)` `(map k v …)` `(sorted-map k v …)` `(meta m x)` `(regex "…")` `(inst "…")` `(uuid "…")` `(type "T")` `(record "T" k v …)` `(def n x)` `(ref n)`。
- `FaslEncoder.EncodeTable`：常量表写成一个向量，def/ref 编号全表共享，未定义引用、重复定义、坏的位模式等报错带行列。`Cleat.Core.Runtime.Fasl.Read` 重建对象（`Keyword.intern`、`Symbol.intern`、`Var.intern`、`LispReader.MatchNumber` 解析 ratio、`PersistentArrayMap`/`PersistentHashMap.createWithCheck`、`PersistentTreeMap/Set`、`PersistentQueue`、`defrecord` 的 `create(IPersistentMap)`、类型经 `TypeResolver`（cleat-010）或 `RT.classForName`）；`ReadPackedTable` 解压后把表向量转成 `object[]`；`Fasl.Write` 把对象直接写成同一格式（复合对象按引用去重为 def/ref，元数据写 `meta`）。`ConstantPool` 供内存模式。
- 测试：`FaslEncoderTests`（编码确定性与错误）、`FaslTests`（全部编码读回为 Clojure 数据并共享、压缩表往返、对象往返含元数据与共享、确定性、不可编码对象报错）。
- 未做：`-0.0` 与 `NaN` 的去重规则属于编译器的常量表（cleat-024）；`inst` 读为 `DateTime`（`instant.clj` 加载后 reader 的 `#inst` 也是 `DateTime`，一致）。
