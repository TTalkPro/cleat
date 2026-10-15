# cleat-101 codegen 静态决议范围：构造器、属性、值类型目标、任意引用形参、泛型方法

- 里程碑：M10　依赖：cleat-100　状态：未开始
- 设计依据：[20 §2](../designs/20-skeg-optimizations.md)；cleat-099 结论

## 工作内容
1. `(new T …)`：按实参静态类型选唯一相容的公共构造器，`newobj`；`ClojureStaticCalls.ResolveCtor`。
2. 属性 getter：零实参成员找不到方法 / 字段时 `call get_X`。
3. 值类型目标：已拆箱的 `:l64` / `:fl` 目标上的实例成员。
4. 非 core 引用类型形参 `castclass`（委托形参除外）。
5. 泛型方法：类型实参能唯一推断时调用实例化后的方法。
6. 开关 `CLEAT_NO_OPT` 含 `ctor` / `prop` / `valtype` / `generic`。

## 验收标准
- `bench/ctor`、`bench/property` 降一个数量级；语料门禁、两套基线、Core / Host 全量不变；种子不动点。
