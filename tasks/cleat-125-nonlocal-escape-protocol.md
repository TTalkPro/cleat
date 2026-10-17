# cleat-125 非本地逃逸协议调研与原型

- 里程碑：M12　依赖：cleat-122　状态：进行中（fibc 收益上界已测量）
- 设计依据：[22 §3 D9](../designs/22-gc-and-performance-followups.md)

## 工作内容
1. 前置门：cleat-122 完成后 fibc / ctak 仍由异常主导（throw 在其他栈帧，`leave` 变换覆盖不到）才开工；否则关闭本项。
2. 最小语义原型：逃逸返回特殊 marker 或线程局部 escape state；可能被穿过的调用点返回后检查 marker 并继续向外返回；编译器标注"可能逃逸调用"，不给所有调用加检查。
3. 协议对应语言层的构造（例如 cleat 提供的内建 `call-with-escape` 形式）：r7rs 的 `call-with-escape` 是基准自带的工具函数，
   编译器不能为识别它的写法而特判（designs/22 §0.1 第 3 条）。
4. 显式设计异步、动态绑定与多线程下的语义；`call/cc` 的完整语义不在范围（r7rs 移植只依赖逃逸续延）。
5. 收益上界用微基准锚定：CLR 同方法预分配 throw 1.8 ~ 2.0 µs，JVM 5 ns；fibc 18.9x、ctak 72x 的差距全部来自这里。

## 验收标准
- 原型上 fibc / ctak 与 JVM 的倍数有数量级改善，普通调用无 marker 检查开销。
- 语义开关开 / 关输出逐字相同；基线、门禁、全量不变；不进入默认生成路径则明确记录并保留原型。

## 第一轮调研（2026-10-17，linux01）

- 前置门成立：cleat-122 统计到的本方法 throw 频率为零，fibc / ctak 仍是跨栈异常。当前 cleat 输入实测
  fibc 33.68 / 33.85 / 33.89 s，ctak 36.34 / 36.51 / 36.49 s（各 3 轮）。
- 现有 `util/call-with-escape` 已经复用单个 `ex-info`，每次逃逸只填 object-array 后 rethrow；开销主要不是构造异常对象，而是
  CLR throw 的栈捕获、EH 展开 / dispatch 与每个中间 catch 的过滤。因此"复用异常"已做到头，减少分配不会改变量级。
- C# marker 原型：`call-with-escape` 分配 owner，逃逸续延返回 `Escape(owner, value)`；owner 匹配就拆箱返回，不匹配继续向外
  返回。fib 形态（每层两个逃逸捕获，`addc` 用循环）里同一结果的对照：
  - n=22：exception 282 ms（另一轮 178 ms），marker 8 / 9 ms；
  - n=30：exception 8.31 / 9.36 s，marker 0.47 / 0.57 s（约 16 ~ 18x）。
  这只是协议收益上界，不是 cleat 编译后的端到端数字；marker 还会给每个可能穿过逃逸的调用返回值加 `is Escape` 检查，C#
  原型只手工检查了必要点。
- ctak 的 C# 对照原型没能直接测完：CLR 匿名委托调用没有尾调用，即使 1 GB 线程栈也在异常路径展开时栈溢出；cleat / JVM
  的尾调用形态不受这个 C# 原型限制。后续若需要 ctak 上界，应先手工把原型改为状态机，或在 cleat AST 原型里直接测。
- 编译器切入点：新增语言层构造（不是识别 `r7rs.util/call-with-escape`）。分析期至少要区分"普通调用返回值"与
  "可能返回 escape marker 的调用"，并把 escape continuation 的调用编译成构造 marker 而不是 throw。保守传播可以先从
  `call-with-escape*` 参数 / 返回值流开始，未证明的函数退回现有异常协议。下一步先做 AST 形态与最小语义样例，再评估
  fibc / ctak 端到端。
