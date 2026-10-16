# cleat-124 字符串热点的 UTF-16 搬运优化

- 里程碑：M12　依赖：cleat-119　状态：未开始
- 设计依据：[22 §5 D11](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 剖析 `string` 基准：确认热点在拼接 / `Substring` / 大小写转换的哪一段（.NET UTF-16 搬运字节量约为 JVM Latin-1 compact string 的两倍）。
2. 只在确认的具体热点改 core 函数：`str` 多参数时先算总长度、一次分配（基准里是 `(str "123" s "456" s "789")`）；预分配 `StringBuilder` capacity、`Span<char>` / `String.Create` 减少中间字符串、去掉重复 `Substring`。
3. 不做泛化重写；不引入内部 Latin-1 字符串表示（影响 interop、identity、打印、正则与 Clojure 语义）。

## 验收标准
- `string` 项前后耗时与分配采样；其他读文件基准（read1、slatex、wc）不回退。
- 语义开 / 关输出逐字相同；基线、门禁、全量不变。
