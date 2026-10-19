# cleat-140 编译期调用路径类型推导

- 里程碑：M15　依赖：cleat-139　状态：进行中
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

## 当前阻塞

编译期链接入口必须保持条件禁用。启用后 `GenerateCompilerPackage` 的第三个 Exec 稳定失败：

```text
Wrong number of args (2) passed to:
cleat.compiler0.codegen/link-runtime-calls/Expr
```

这不是 MSBuild 环境缺失：使用完整 `CLEAT_COMPILER_NS=cleat.compiler0` 环境可复现。把直接
Var 调用改成 `mapv` 高阶调用、显式 `loop` 或 `apply` 都不能绕过；同一改动禁用入口后完整自举
通过。由于包编译时 `@live-unit?` 为 false，运行路径不应进入链接，怀疑是 cleat-140 的 IR / 接口
变化破坏了当前 compiler0 自举不动点。需要按 cleat-141 重生成种子或在独立干净自举环境中分级比对
接口与 Lcil 后再启用。

## 验证

- `dotnet build Cleat.slnx -c Release -v minimal -nologo` 通过，0 warning / 0 error。
- `Cleat.Core.Tests` 的 `RuntimeSig` 过滤用例 7/7 通过。
- `Cleat.Host.Tests` 的 `SignatureSpecialization` 与 `WholeFunctionRuntimeSpecialization`
  2/2 通过；后者包含默认、关闭、同步压力和后台压力语义比对。
