# cleat-154 变长调用已知元数直发

- 里程碑：M18　依赖：无（建议最先做：M18 收益上限最大）　状态：完成（2026-10-23 收口：111 A/B 不达标，src / seed / tests 已回退，机制归因记账保留）
- 设计依据：[28 §3.3 / §7.2](../designs/28-hot-path-optimizations.md)、
  [notes M17 §3](../notes/r7rs-performance.md)、cleat-147 的量化结论

## 问题

cleat-147 已把变长调用的成本量化到可判定的量级：111 口径下，20M 次/进程、5 个独立进程的中位，
`rest0` / `rest2` 相对固定元数基线分别多 **63.293 / 57.380 ns/iter**，同时多 **120 / 112 B/iter**。
增量不只是 `Object[]` 本身，还包括 RestFn 调用协议带出的实参数组、装箱与 `ArraySeq_object`。
因此不能再把这条只记成“分配画像”。

当前 `fn*` 变长子句生成 `RestFn` 子类（`codegen.clj` 的 `clj-fn-class!`，`base` 在
`src/clojure/cleat/compiler/codegen.clj:3382` 选择 `restfn-type`）；已知固定元数调用仍可能先进入
`RestFn.invoke(...)` 协议，再构造尾部数组并进入 `doInvoke`。缺少的是：**编译期已同时知道被调函数类
与实参个数，且该个数命中固定元数入口时，直接调用固定元数方法**。

## 口径

本任务**默认全面启用，不新增 `CLEAT_NO_OPT` / `CLEAT_OPT_*` 开关**。A/B 用改前 / 改后构建做同轮配对；
既有开关保留为归因仪器，但新能力不挂在开关后面。

本任务同时合并 designs/28 §7.1 的 docstring 挂账：
`src/clojure/cleat/compiler/driver.clj:87` 的 `register-eval-accessors!` 仍写
“`CLEAT_NO_OPT=accinline` 时保持空表”，实际 145 已改成 `CLEAT_OPT_ACCINLINE=1` 才开。
这句话会编进种子，所以改完必须重走种子不动点。

## 工作内容

1. 先 dump 一组真实调用点 IL，确认变长目标在已知元数下经过哪些 `invoke` / `applyTo` /
   `doInvoke` / 数组分配点；把“已知函数类 + 已知元数”的静态事实从 codegen 传到发射点。
2. 对命中固定元数签名 / 桥方法的目标直发固定元数调用；只有静态信息不足时保留通用 RestFn 协议。
   不做小数组复用：这会把生命周期和共享可变性引入调用协议，收益和语义风险不成比例。
3. 语义红线逐条固定：实参从左到右求值且副作用顺序不变；`apply` 仍把最后一个 seq 展开后按顺序拼接；
   不足 / 超出 required arity 抛同一类错误；`&` 后得到的 seq 类型和 JVM 侧一致；变长函数作为值传递时协议不变。
4. 若直发改变 `src/clojure`，按种子纪律重生成；若只改 codegen 的种子输入，也必须验 stage2 == stage3。
5. docstring 修正与本任务同一个不动点窗口完成，不为注释单开一轮。

## 验收

- 新增语义用例覆盖 0 ~ 20 固定实参、恰好 required arity、越过 required arity、`apply` 的空尾 / 非空尾 /
  惰性尾 / 带副作用尾、arity 错误、函数值转发；期望输出与改前逐字相同。
- `rest0` / `rest2` 探针重测：时间与 `GC.GetAllocatedBytesForCurrentThread()` 净值都要相对 fixed 下降；
  真实负载至少看 peval / browse / dynamic，同时看 list / lattice 这类不受利项不回退。
- 门禁：Release 0 warning、gate 521 分类不变、CTS 6868/29/6、test-clojure 基线、Core / Host 全量。
- stage2 == stage3，BuildFromTheSeed 通过；111 同轮配对给出逐项门槛表，不达标不声称收益。

## 进展（2026-10-22）

- 已生成 `invokeExact(required arity)` 直达入口；KnownCall 的 `isinst` 守卫不变。命中静态固定入口时仍优先固定入口，
  避免 `(sorted-map)` 这类 fixed/variadic 双子句函数把必需元数 0 的变长入口误当成当前元数。
- `register-eval-accessors!` 的 accinline docstring 已并入同一个种子不动点窗口；无新增优化开关。
- 新增语义矩阵覆盖恰好必需元数、越过必需元数、`apply`、闭包、重定义、副作用顺序与 arity 错误。
- 本地已过：Release 0 warning、种子 stage2 == stage3、BuildFromTheSeed、Host 286/286、Core 123/123、
  conformance gate 521/521、CTS 6868/29/6、test-clojure 3877/25/4 基线。
- cleat-147 的 `rest0` / `rest2` 探针调用元数越过 required arity，按本任务范围仍保留 RestFn 协议；
  恰好 required arity 的路径需在 111 补独立探针并和真实负载一起配对，完成前不声称收益。

## 进展（2026-10-22，111 同轮 A/B）

- 双构建配对：ref = 6a7194c、cur = 948fcc4（+ 19b3e3a 只动 bench 的探针相位，运行时产物与 948fcc4 相同），
  各自 Release 0 error；`CLEAT_DUMP_EVAL_LCIL` 取证确认机制成立——ref 的恰好必需元数调用点发
  `castclass IFn` → `IFn.invoke`（进 RestFn 的 arity switch 后 doInvoke），cur 发 `isinst` 守卫 →
  `callvirt invokeExact`（rest 直接 `ldnull`）。
- alloc 探针补 `req0` / `req2` 相位（19b3e3a），20M 次 × 5 进程同轮配对：分配逐相位两臂完全相同
  （预期——恰好必需元数本就不建尾数组）；时间 `req0` x1.026（0/5 同向偏慢）、`req2` x1.015（1/5），
  都在 10% 门槛内、无可声明收益，`rest0` / `rest2` 与闭包相位 ±2% 噪声内不变。
- 真实负载（bisect149 同轮配对 × 5 轮，111）：peval x1.020（5/5 偏慢）、browse x0.957（3/5）、
  dynamic x0.985（5/5 偏快）、lattice x0.987（4/5）、sum x1.248（4/5，秒以下启动主导）、
  几何平均 x1.034——全部 10% 门槛内不可判定；数据 results/bisect154.jsonl（111 cleat-154-cur 工作树）。
- 机制归因：ref 的 RestFn 恰好必需元数入口本来就是"小 int switch → doInvoke(…, null)"，无分配；
  154 换成 isinst 分支 + 具体类 callvirt，两笔相抵，净值趋零——收益上限本来就在噪声里。
  有代价的部分（>必需元数的 RestFn 参数数组）按范围刻意不动。
- **判定：不达标，不声称收益**。peval 的 5/5 +2% 低于门槛且与地板不可区分，不构成可声明回退；
  按 M18 口径"已证明有效即默认全面启用"的反面，收益未证明就不该留在默认路径，
  待收口决定回退 src 改动（保留探针相位与本节取证）还是按中性记账保留。

## 收口（2026-10-23）

- 按回退决定执行：512069f revert 948fcc4 的 src / seed / tests（revert 后 `git diff 6a7194c HEAD -- src tests`
  为空，即回到 153 收口时已过全套门禁的验证态，无需重验种子链）；bench 探针相位、`CLEAT_DUMP_EVAL_LCIL`
  取证与本节归因全部保留。accinline docstring 欠账随回退重新挂回 §7.1，留给下一个动 `src/clojure` 的任务。
- 记账：RestFn 的恰好必需元数入口（`IFn.invoke` → 小 int switch → `doInvoke(…, null)`）本来就是零分配、
  几条整数比较的近零成本路径；把它换成 isinst 守卫 + 具体类直达的期望净收益趋零，与本轮 111 读数一致。
  变长调用真正的成本只在**越过**必需元数时的参数数组路径（147 量化 +120 / +112 B/iter），
  那条要动只能整体改 RestFn 协议（小数组复用已被口径否决），M18 内不再重开。

