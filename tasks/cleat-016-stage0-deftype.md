# cleat-016 stage0：deftype* / reify* 生成真实类型

- 里程碑：M2　依赖：cleat-015、cleat-019、cleat-020　状态：未开始
- 设计依据：[08 §4](../designs/08-bootstrap.md)、[00 D12](../designs/00-overview.md)、cleat-003 结论

## 目标
在 stage0 中 `deftype*`/`reify*` 产生实现接口的真实 CLR 类型，方法体转发到解释器闭包。

## 工作内容
1. 由 Lcore 的 `deftype` 节点生成 Lcil：类型、字段（可变 / volatile）、构造函数、每个接口方法 `override`，方法体 = 查静态方法表 `IFn[]` 后 `callvirt invoke(k)`（`this` 为第一个参数）；`getBasis` 静态方法。
2. 经 `Cleat.Emit` 内存模式创建类型，再把方法体闭包填进方法表；`reify*` 的捕获变量作为额外构造参数与字段。
3. 类型缓存：同名 `deftype` 重定义时创建新类型（与 Clojure 一致，旧实例保留旧类型）。
4. 测试：`deftype` 实现 `IDeref`/`ISeq`/`IFn`，由 C# 侧调用；`defrecord`（宏展开后）的 `=`/`hash`/`assoc`；`reify` 捕获闭包；`set!` 可变字段。

## 验收标准
- `core_deftype.clj` 中的 `deftype`/`reify` 在 stage0 上可用；`(instance? ISeq x)` 对 `deftype` 实例成立。
