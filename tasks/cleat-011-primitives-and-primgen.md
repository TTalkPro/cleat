# cleat-011 原语标注与 PrimGen

- 里程碑：M1　依赖：cleat-006 ~ cleat-010　状态：未开始
- 设计依据：[02 §5](../designs/02-runtime.md)、[04 §5](../designs/04-compiler.md)

## 目标
运行时与编译器之间的契约文件 `src/boot/primitives.edn`。

## 工作内容
1. `[Primitive]` 属性（名字、`Pure`、`Inline`、`Unchecked`）；标注 `RT`、`Numbers`、`Util` 中编译器要静态调用的方法（`first`/`next`/`more`/`cons`/`seq`/`nth`/`get`/`count`/`assoc`/`conj`/`add`/`lt`/`equiv`/`booleanCast`/`keyword`/`symbol`/`intCast`…，参照 ClojureCLR `Intrinsics.cs` 与 `core.clj` 中 `. clojure.lang.RT` 调用的集合）。
2. `Cleat.PrimGen`：反射 `Cleat.Core` 生成 `primitives.edn` 的 `:clr` 列；`llvm` 列的合并留到 cleat-037（现在输出空）。
3. `PrimitiveRegistry`：启动时把原语包装为 `IFn` 绑到 `cleat.kernel/…` Var。
4. 构建集成：`Cleat.Core` 构建后运行 PrimGen（增量）。

## 验收标准
- `primitives.edn` 生成且确定（两次相同）；`cleat.kernel` 命名空间在运行时初始化后含全部原语 Var。
