# cleat-124 字符串热点的 UTF-16 搬运优化

- 里程碑：M12　依赖：cleat-119　状态：关闭（2026-10-16，无收益）
- 设计依据：[22 §5 D11](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 剖析 `string` 基准：确认热点在拼接 / `Substring` / 大小写转换的哪一段（.NET UTF-16 搬运字节量约为 JVM Latin-1 compact string 的两倍）。
2. 只在确认的具体热点改 core 函数：`str` 多参数时先算总长度、一次分配（基准里是 `(str "123" s "456" s "789")`）；预分配 `StringBuilder` capacity、`Span<char>` / `String.Create` 减少中间字符串、去掉重复 `Substring`。
3. 不做泛化重写；不引入内部 Latin-1 字符串表示（影响 interop、identity、打印、正则与 Clojure 语义）。

## 结果（2026-10-16，本机）
- 试过：`str` 加 2 / 3 / 4 参数的 `String.Concat` 子句，5 个以上经新的 `RT.StrConcat`（各部分转字符串后一次算好总长、一次分配），
  语义用例（nil、关键字、符号、数、比值、集合、惰性序列、StringBuilder）与原实现逐字相同。
- 微基准（10 万字符的串，2000 次）：`(str "123" s "456" s "789")` 264 → 279 ms、`(str s s)` 245 → 231 ms——没有收益；r7rs string 单次 2.38 / 1.51 s（噪声）。
  大字符串的开销在分配时的清零（memset）与大对象堆的回收，不在拷贝次数（StringBuilder 只多一次 memcpy）。
- 副作用：`str` 的小子句能内联后被复制进核心库的大量调用点，种子 2103 → 2198 KiB。撤回，本项关闭；string 按宿主表示差异对待（designs/22 §5）。

## 验收标准
- `string` 项前后耗时与分配采样；其他读文件基准（read1、slatex、wc）不回退。
- 语义开 / 关输出逐字相同；基线、门禁、全量不变。
