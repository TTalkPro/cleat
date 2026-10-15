# cleat-110 结果类型交给推断：宿主调用返回的数组类型给无提示局部、deftype 原始字段读取的类型进算术（cleat-107 原因 6、2）

- 里程碑：M11　依赖：cleat-109　状态：未开始
- 设计依据：[21](../designs/21-r7rs-gaps.md)

## 工作内容
1. 复现：`(let [v (object-array 3)] (aset v 0 a))` 的 aset 走运行时决议；`(+ (.-re a) (.-re b))`（^double 字段）走 Object 算术。
2. let 变量的静态类型取初值的宿主结果类型（分析器给引用类型的局部带标注 / codegen 的 static-arg-type 认局部）；
   GetInstanceField 的字段类型是 long / double 时 expr-type 给 :l64 / :fl，原始发射直接读字段。
3. 语义用例开 / 关对照。

## 验收标准
- array1、scheme、matrix、mbrotZ 前后数字。
- 基线、门禁、全量不变；改了 src/clojure 的种子不动点。
