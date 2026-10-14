# cleat-092 生成代码去 Scheme

- 里程碑：M8（阶段 D）　依赖：cleat-091　状态：未开始
- 设计依据：[19 D8 第二条](../designs/19-clojure-self-hosting.md)

## 目标
编出的代码与 Clojure 编译器本身不再引用任何 Cleat.Scheme 类型。

## 工作内容
1. try / catch：处理器内联为代码，不再编成 `Procedure` 闭包；不再重抛 `ContinuationException`。
2. "无泛型参数"占位由 `'()` 改为 null（`ClojureOps.CallStatic` / `CallInstance` 同步）；顶层不用 `StaticProcedure`；去掉 `RT.Box(int)`。
3. 常量表：Cleat.Core 提供 Clojure 数据的序列化（取代 `Cleat.Scheme.Fasl.ReadPackedTable`），`Cleat.Emit/EmitConstants.cs` 改接它；`.inl` 改存 L0 的 Clojure 数据。
4. Lcil 头 `(runtime scheme)` → `(runtime clojure)`；`Cleat.Emit` 去掉 `SchemeData.cs` 的依赖。
5. 裁掉 L0 中只为 Scheme 存在、普查确认不会出现的节点与对应 codegen；前端不再产出 Scheme 痕迹。
6. 按 D7 两步法重生成种子。

## 验收标准
- `cleat.boot.lcil`、`cleat.compiler.lcil` 与包程序集中没有 `Cleat.Scheme`；G4 不动点；全量测试与基准不退化。
