# cleat-125 非本地逃逸协议调研与原型

- 里程碑：M12　依赖：cleat-122　状态：完成（2026-10-17）
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
- 语言构造按新语义进入默认生成路径；证明不了的路径回退旧异常协议且输出保持。基线、门禁、全量不变。

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

## 实现记录（2026-10-17）

### 语言协议

- 新增特殊形式 `(escape* (fn [k] body))`：进入时分配一个 `ClojureOps/EscapeOwner` 作为单向逃逸续延；`k` 只能在
  `body` 的动态范围内调用。这是语言层协议，编译器不识别基准库的 `call-with-escape` 写法。
- 已证明调用点：`k` 是 `^:escape` 形参且直接作为该形参传给声明了 `:escape-params` 的 Var，或对 `k` 的单参局部调用。
  前者按普通返回值传播 `EscapeMarker`；后者由 `EscapeInvoke(k, v)` 构造 marker，外层 `EscapePropagate` 立即把它送出当前方法。
- 未证明调用点：`k` 作为普通值读出、传给非指定参数 / 其他函数时，先经 `EscapeFallbackOf` 包回旧协议。此时 owner 调用
  仍抛 marker（marker 继承 Exception），由 `escape*` 的 catch 拆开；因此普通调用不增加 marker 检查。
- codegen 的保护区规则：不在 `try` / `catch` / `finally` 里从受保护块 `ret`。保护区内的 `EscapePropagate` 改调
  `EscapePropagateProtected`，用同一个 marker 异常退回现有异常协议。非保护区内尾位置 `ret` marker，非尾位置 `throw` marker。
- 异步、动态绑定与线程：第一版不引入全局状态；owner 和 marker 只沿当前逻辑调用链返回，动态绑定仍由既有线程局部机制决定。
  续延捕获是语言层的动态范围约束，不提供跨线程 / 跨异步边界或把 `k` 存活到 `escape*` 之后的 call/cc 语义。捕获续延属于
  违反协议；后续如要支持，需要单独设计生命周期与失效检查，不能靠本协议隐式表达。
- 已知限制：`defn` 宏会剥掉形参元数据，声明 marker 感知形参目前要写 `(def name (fn* [x ^:escape k] ...))`。
  marker 在回退路径继承 Exception；穿过会捕获 Exception 的用户 handler 时仍按现有异常协议可见。

### 变更

- 运行时：`ClojureOps.EscapeOwner` / `EscapeMarker`，以及 owner 创建、调用、拆包、catch、回退和传播入口。
- 分析器：`escape*` 特殊形式、fn* 形参 `^:escape` 到局部和 Var `:escape-params` 的标注、直接调用点识别与保守 fallback。
- codegen：`EscapePropagate` 的快速路径分支；保护区专用 `EscapePropagateProtected` 入口。
- 测试：`escape_semantics.clj` 覆盖直接逃逸、正常返回、跨函数传播、非尾逃逸、嵌套 owner、用户异常和保护区回退；
  `ClojureCompilerTests.EscapeReturnsMarkersAndFallsBackConservatively` 逐字对照输出。

### 结果与验证

- 基准（本地各 3 轮）：fibc 33.68 / 33.85 / 33.89 s → 1.073 / 1.094 / 1.108 s（约 31x）；ctak 36.34 / 36.51 / 36.49 s →
  0.710 / 0.751 / 0.726 s（约 50x）。已改写 `bench/r7rs/fibc.cljc` / `ctak.cljc` 使用语言层构造。
- `dotnet build -c Release` OK；种子重新生成且不动点（stage2 == stage3，2084 KiB）。
- 门禁 521 行 OK；cts 6868 / 29 / 6 与 test-clojure 3688 / 18 基线不变；新增 escape 语义测试通过。
- 本项是显式语言构造而不是可选优化，因此没有 `CLEAT_NO_OPT=escape` 开关；保守路径指编译器证明不了 marker 返回时
  自动回到异常协议，并继续由既有的 try / catch 语义处理。
