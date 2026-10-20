# cleat-143 String 的 seq / first 直读快路径

- 里程碑：M16　依赖：无　状态：未开始
- 设计依据：[28 §2.2](../designs/28-hot-path-optimizations.md)

## 问题

`RT.seqFrom` 对 string 每次 `StringSeq.create` 新建堆对象；`(seq "abc")` 在 cleat 侧绝对成本
25 ~ 27 ns（shapes3 复核后的真实每次调用成本，JVM 侧探针数字因 LICM 不可比）。
字符串是密集热路径：slatex / string / wc / read1 / nucleic 与 dynamic 的 scheme-read 都吃这个成本。

## 工作内容

1. `first` / `nth`（及 `count` / `empty?` 顺带核对）作用于 String / CharSequence 时不建 `StringSeq`，
   直接按索引取字符（返回路径经 cleat-142 的装箱缓存）。
2. `seq` 的 `StringSeq`：利用不可变性按（串, offset）复用，或改为索引式轻量表示，避免每次堆分配。
3. codegen / 分析器能静态确定操作数是 String 时直发专用路径，否则在 RT 入口做类型分支。
4. `CLEAT_NO_OPT` 开关（如 `strseq`）保留旧路径。

## 验收

- 语义红线：`seq` 返回对象的 identity / `=` / 惰性行为与 JVM Clojure 一致；空串、单字符、
  代理对（surrogate pair）、`subs` / `map` / `reduce` 组合用例开 / 关输出逐字相同。
- alloc.cs：string / slatex / wc 的 `StringSeq` 分配量下降。
- 同窗口 A/B：受益项可测收益、无回退；门禁与两套基线不变；改 RT / codegen 后按需重生成种子。
