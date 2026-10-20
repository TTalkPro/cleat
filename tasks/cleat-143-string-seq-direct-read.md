# cleat-143 String 的 seq / first 直读快路径

- 里程碑：M16　依赖：无　状态：完成（2026-10-20，commit bcb4e0c + 分支顺序修正 4b29e38）
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

## 结论

做了四处入口的运行时分支，未做 codegen 静态类型判定（原计划第 3 项）：

1. `RT.first` 对 string 直接取首字符（空串 `nil`），返回经 `CharBox.Get`，不建 `StringSeq`；
2. `RT.seq` / `seqFrom` 的 string 分支不再落入 `GetType().IsArray` 之后的长类型链；
3. `RT.next` / `RT.more` 用 `StringSeq.create(s, i)` 直接按剩余偏移建同类对象，省掉
   `seq(x).next()` 往返与 `ASeq.more` 分派；
4. `StringSeq.first()` 与 `reduce` 的返回路径改走 `CharBox`。

`seq` 返回对象的 identity 语义未改（每次仍新建 `StringSeq`），只省去分配与分派。
一个 `CLEAT_NO_OPT=strseq` 同时门控上述四处与 CharBox 装箱。

**分支位置是本轮学到的代价**：初版把 `x is string` 放在 `Cons` / `PersistentList` / `ASeq` 之前，
远端 A/B 里 lattice（纯列表负载）回退 +6.3%；单开关隔离指认到本项（关 strseq 10.35 vs 全关 10.34，
关另两项 10.8 ~ 11.0）。修正（4b29e38）只把 string 分支移到 ASeq 系之后 —— 本项收益来自
"不建 StringSeq"，与分支位置无关，位置放错只会让别的类型每次多付一次类型检查。

## 验证（2026-10-20）

- 语义：`strseq_semantics.clj`（空串、单字符、代理对码元、`subs` / `map` / `reduce` / `into`、
  `nth` 默认值、`seq` 的 `=` 与 `identical?`、class、打印形态）默认与 `CLEAT_NO_OPT=strseq`
  输出均与 `.out` 逐字相同；Host 测试 `StringSeqDirectReads` 通过。修正后在 main 4b29e38 上重跑，开 / 关仍逐字相同。
- 门禁 / 基线（合并 main 6063c39）：Release 0 error / 13 warning、Core 123/123、Host 278/278、
  `gate: OK (521)`、CTS 与 test-clojure 与基线逐字一致。不改 codegen，种子无变动。
- 分配（本地 `tools/alloc.cs`）：`StringSeq` 2.47 → 2.37 MB；wc 的 `System.Char` 6.2 → 2.6 MB、
  `System.String` 648.7 → 343.1 MB。
- 远端同窗口开 / 关 A/B（3 次取最小，秒）：**wc 6.356 → 3.857（-39%）**、browse -3.9%、read1 -2.5%、
  dynamic -1.9%；string / nucleic / slatex / scheme / peval 差异在噪声内。
  修正分支顺序后复测：wc 6.308 → 3.895、browse 5.963 → 5.687（收益不变），
  lattice 1.063 → 1.014（回退基本消除）。详见 notes 的 M16 §1 / §2。
