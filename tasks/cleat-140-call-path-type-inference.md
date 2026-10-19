# cleat-140 编译期调用路径类型推导

- 里程碑：M15　依赖：cleat-139　状态：完成
- 设计依据：[27 §5](../designs/27-jit-style-runtime-specialization.md)

## 问题

运行时签名特化要等第一次通用调用观察后才能生成。调用图上的 Var 恒等已经确定、调用点实参类型
也能在编译期证明时，应该直接预生成并登记该签名，调用点用 Var 恒等守卫直连；Var 重定义后回退
`IFn.invoke`。返回类型摘要还要沿调用图传播，给后续优化更多类型证据。

## 已完成

1. 建立编译单元内的 Var 调用点链接：`collect-runtime-defs!` 收集运行时候选，`link-runtime-calls`
   改写可证明的 `clj-invoke` 与 `KnownCall`。
2. 从静态实参推导精确实参类型码，按目标掩码过滤后拼成 64 位签名 key；同一单元同一调用签名复用
   已生成的特化。
3. 预生成 O 模式特化类，`InstallRtSpec` 在所有 `def` 初始化后调用 `RuntimeSigSpec.SeedCompiled`
   填表；`RtSpecCall` 带 Var 恒等守卫直连，重定义后走通用 `IFn`。
4. 链接后按已安装特化的形参类型重建固定类型表并重新推断，保证返回类型摘要继续参与调用方优化。
5. 补齐 `RtSpecCall` / `InstallRtSpec` 的类型、递归识别和 CIL 生成；修复 forward declare 与
   64 位 key 发射。

## 自举修复

编译期链接入口已恢复启用。此前的 arity 错误根因是 `link-runtime-calls` 把外层 pass 形参
`links` 又声明成 `Expr` / `Body` 处理器的额外形参：自举编译器生成的 letfn 闭包把它作为捕获
上下文，但调用侧仍按额外实参传递，二者在当前不动点里错位。仓库既有 nanopass pass 的风格是
处理器只接 `ir`、闭包捕获外层上下文；现在改写为同一风格，并补上 `clj-var-ref` 与 `unbox` 的恒等覆盖，
lambda 输出用省略号接入重写后的子句序列。

## 验证

- `dotnet build Cleat.slnx -c Release -v minimal -nologo` 通过，0 warning / 0 error。
- `Cleat.Core.Tests` 的 `RuntimeSig` 过滤用例 7/7 通过。
- `Cleat.Host.Tests` 的 `SignatureSpecialization` 与 `WholeFunctionRuntimeSpecialization`
  2/2 通过；后者包含默认、关闭、同步压力和后台压力语义比对。
