# cleat-111 运行时快路径：Util.equiv(Object, long / double)、zero? / pos? / neg? 作用于 Object、(byte x) 与 byte[] 的 aset、符号相等（cleat-107 原因 5、8、9）

- 里程碑：M11　依赖：cleat-110　状态：未开始
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 逐个量化：`(= x 0)`、`(zero? x)`、`(byte 5)`、`(aset ^bytes b i v)`、`(= 'a 'b)` 的调用路径与耗时。
2. ClojureCLR 运行时（src/Cleat.Core/clojure.lang）对应方法加类型快路径（Int64 / Double 先判），byte 转换与 aset 的重载选择；符号相等先比引用与哈希。
3. 语义不变（溢出、NaN、类型不同的相等按 Clojure 规则）。

## 验收标准
- 各微基准接近 JVM 的量级；maze、bv2string、peval、dynamic、scheme 前后数字。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
