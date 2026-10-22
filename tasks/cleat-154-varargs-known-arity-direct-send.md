# cleat-154 变长调用已知元数直发

- 里程碑：M18　依赖：无（建议最先做：M18 收益上限最大）　状态：未开始
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

