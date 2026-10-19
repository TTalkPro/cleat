# cleat-139 整体函数特化（O 模式）

- 里程碑：M15　依赖：cleat-138　状态：完成
- 设计依据：[27 §3 ~ §4](../designs/27-jit-style-runtime-specialization.md)

## 问题

cleat-138 的运行时特化仍只给能证明返回 `long` / `double` 的纯体走 R 模式；非纯函数、闭包、异常、
集合与数组写入都不能按观察到的实参类型重新生成。默认 128 次阈值也太晚：通用体常常先完成分层升级，
再切到特化体又要重新预热，`mbrot` 上切换后反而慢 50%。

## 工作内容

1. **O 模式整体特化**：非纯 / 返回类型不可证明的候选也按观察到的类型重新生成。数值形参保留
   `object` 存储，体里按推断的原始用途 `unbox.any`；返回仍是 `object`，因此没有放弃点，副作用只执行一次。
2. **身份守卫自调用**：运行时特化体里的 `(selfish …)` 比较 Var 当前值与原函数对象；相符时直调
   `rtSpec` 类，实参静态证明不了类型时用 `RuntimeSigKey.Code` 核对后走慢路径。`with-redefs`、
   `binding`、重定义自动回通用调用。
3. **入口单态快路径**：`RuntimeSigTable.Fast` 保存首个不放弃条目与各槽位精确类型；入口比较
   `GetType()` 后直接 `IFn.invoke`，不走 key 计算、表查找与拆箱。逐出快路径条目时清空。
4. **JIT 式触发与后台编译**：缺省阈值降到 8，赶在通用体 tier-1 之前；编译进独立后台线程，
   调用线程继续通用体。`CLEAT_RTSPEC_SYNC=1` 恢复同步编译（测试确定时机用），
   `CLEAT_RTSPEC_STATS=1` 输出成功 / 失败与编译耗时，`CLEAT_RTSPEC_DEBUG=1` 输出失败原因。
5. **候选与收益门槛**：候选不再要求纯；进 key 的形参掩码只统计热区（真循环体 / 自递归体）里的
   类型化用途，循环内用途加权，避免直线小函数被无关类型分裂。`CLEAT_RTSPEC_ALL=1` 是验证用的
   不看门槛压力模式。
6. **运行时单元质量与引用**：`rtSpec$` 程序集改用 `eval$` 前缀并走 PE 字节 + 默认装载上下文，
   保留分层编译与动态 PGO；发射时优先在原 eval 单元里解析短类型名。类型化体接到宿主形式后一起
   做推断 / 折叠 / 循环推测，最后拆回登记表；循环用途加权后选择存储形式，减少内层循环重复装箱。
7. 诊断：`CLEAT_DUMP_EVAL_LCIL=<文件>` 追加每个 eval 单元的 Lcil，用来对照编译期生成的调用形态。

## 验收

- 新增 `tests/Cleat.Host.Tests/data/rtspec_jit_semantics.clj`，覆盖副作用只执行一次、返回 `nil` /
  `false`、闭包捕获原始形参、闭包内自调用、异常 / 溢出 / `try`、自调用实参类型漂移、
  `with-redefs` / `binding` / 重定义、多签名、数组与字符串写入、惰性序列。
- 默认、`CLEAT_NO_OPT=sigspec`、同步压力与后台压力模式输出逐字相同；默认确有运行时生成且失败为 0；
  压力模式至少生成 25 个特化且失败为 0。
- `WholeFunctionRuntimeSpecialization` 与更新后的 `SignatureSpecialization`、4 项
  `RuntimeSigTableTests` 全部通过。完整压力基线、种子重生成与 r7rs 三方重跑留给 cleat-141。
