# cleat-132 参数签名特化分发原型

- 里程碑：M13 后续　依赖：cleat-104、cleat-128　状态：完成

## 问题

频繁装箱 / 拆箱不只出现在循环里。无提示的顶层递归函数即使编译期不知道调用类型，
运行期也常只按某一个参数签名调用；通用 `IFn.invoke` 边界仍会反复拆箱实参、
在内部用对象算术、最后再装箱结果。

## 方案

不改已加载代码，也不做全局调用拦截。对满足 cleat-104 纯函数形状、但没有编译期
类型证据的 1～2 参顶层递归函数，额外预生成常见签名 `LL` / `DD` 的类型化体；
通用入口保留原体并负责对外装箱。

函数类里加一个静态签名缓存（0 通用，1/2 表示 `LL` / `DD`）。命中缓存时先做
两个 `isinst` 再直调对应 `invokeStaticTyped` 重载；缓存不命中时按顺序试签名，
成功后更新缓存，`TypedBailout` 或类型不匹配回退通用体。签名特化体内部的自调用
继续经 `TypedSelf` 直调，原始值不跨方法边界装箱；Var 重定义或返回类型不符仍走
现有异常回退协议。

## 约束

- `CLEAT_NO_OPT=sigspec` 可单独关闭；不关闭 cleat-104 与类型反馈。
- 不改变实参求值顺序、数值语义、异常语义和 Var 重定义语义。
- 只覆盖无 rest、无闭包字段、无 async、无原始签名提示的固定子句。
- 常规非递归函数不生成签名体；没有可证明的 `LL` / `DD` 返回类型也不生成。
- 外部边界仍返回 Clojure 对象；中间装箱的减少不得引入 CLR 隐式装箱。

## 验收

- 新增语义用例覆盖 `LL` / `DD` 命中、miss 回退、递归、Var 重定义、mixed 数值、
  Ratio / BigInt / BigDecimal、NaN / ±0.0、long 溢出与异常路径；
  `CLEAT_NO_OPT=sigspec` 开 / 关输出逐字相同。
- 生成的候选函数类包含静态签名缓存与 `invokeStaticTyped` 重载；非候选不变。
- Release 构建通过，Host 专项与 Core 全量本地测试通过；影响编译器产物时做种子
  不动点验证。
- 在本机做小基准，比较关闭与开启的耗时和分配。若无收益，记录原因并默认关闭。

## 结果（2026-10-18，linux01 本地）

- `spec-signature-entries` 为 1～2 参候选生成实际形参长度的 LL / DD 家族；一参候选不再误按两参签名发射 `ldarg.2`。
- `invokeStatic` 分发使用静态 `ssKind`（0 通用、1/2 命中项）。热路径先按缓存直调；慢路径按签名顺序做
  `isinst`、`unbox.any` 并调用 `invokeStaticTyped`，成功后写缓存；`TypedBailout` 或类型不符进入下一签名 / 通用体。
- 特化返回值只可能是装箱后的 long / double；方法入口的 null 局部作为 `TypedBailout` 后的失败哨兵，不引入额外布尔局部。
- 成功路径在函数入口尾位置直接返回，避免从 EH 外跨越后续 EH 块跳到共享汇合点；多签名失败标签在下一个尝试体前定义。
- `CLEAT_NO_OPT=sigspec` 可独立关闭；`CLEAT_NO_OPT=typed` 可在 cleat-104 关闭时单独验证本功能。默认开启，但默认 typed
  已证明的候选仍优先走 cleat-104。
- 语义脚本覆盖递归、mixed 数值、miss、nil / string / Ratio / BigInt / BigDecimal、NaN / ±0.0、long 溢出、错误实参、
  `with-redefs` 与恢复；默认、关闭 sigspec、关闭 typed 三种设置输出逐字相同。新增 Host 专项同时检查 LCIL 中
  `ssKind` 与类型化重载，副作用候选不特化。
- 编译器产物变化后重生成 seed；自举 stage2 / stage3 与 seed 发射结果逐字节一致（自举固定点 140s）。
- fib35 本地小基准（3 个进程 × 每进程 7 次，`CLEAT_NO_OPT=typed,sigspec` 对 `CLEAT_NO_OPT=typed`）：
  中位 142.981ms → 37.739ms（约 3.8×），最小 142.557ms → 35.971ms。7 次 fib35 工作负载分配
  1,838,784 字节 → 168 / 192 字节；整进程采样 113.1MB → 112.9MB，差异被启动 / 编译开销掩盖。
- Release 构建通过；Core 118 项通过；Host 专项通过。Host 非 Bootstrap 套件在与其他本地测试争用 CPU 时
  264 / 265（异步并发阈值一项疑似受争用影响，单测复跑通过）；Bootstrap 排除 NuGet 冷加载 fixture 为 4 通过 / 1 跳过
  （ILVerify 未安装）。NuGet fixture 的本地 `PackRuntime` 在 300s 超时，卡点在 fixture 打包进程等待，与本次生成代码无关；
  其余 Host 内嵌 cts（6868/29/6 基线不变）与 test-clojure（基线不变）均已通过。
