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

## 结论（2026-10-10）

完成（做法与设计略有不同：原语集中声明在一个 C# 表里，不在 EPL 文件上加属性）。
- `Cleat.Core.Runtime.Primitives.Table`：61 项，Clojure 名 → (类型, 方法, 参数类型, `:pure`, `:inline`)：`RT` 的序列 / 集合 / 转换 / 打印，`Numbers` 的装箱算术、比较、位运算（拆箱重载留给 cleat-042），`Util` 的相等与哈希。编译器按 (类型, 方法, 参数类型) 查表（`Primitives.Find`），所以 `core.clj` 里 `(. clojure.lang.RT (first x))` 这样的宿主调用可以内联；`:inline` 项是发射器 / codegen 有模板的（`first` `next` `seq` `count` `nth` `get` `booleanCast` `identical` `equiv` `add` `minus` `multiply` `inc` `dec` 比较 `isZero`）。
- `Cleat.PrimGen`：构建后把表写成 `src/boot/primitives.edn`（按名字排序、类型全名；内容不变不写文件；`-p:GeneratePrimitives=false` 跳过）。格式 `{:version 1 :primitives {cleat.kernel/first [{:target :clr :type … :method … :params […] :ret … :flags #{…}}]}}`，`:llvm` 列留给搁置的 cleat-037。
- `PrimitiveFn : AFunction`（按参数个数选重载，参数经 `Reflector.Convert<T>`、返回值经 `ConvertFrom`，`params object[]` 重载接全部实参）；`PrimitiveRegistry.RegisterAll()` 在 `cleat.kernel` 绑定同名 Var（元数据 `:primitive true`）。
- 测试 `PrimitivesTests`：每项解析到静态方法且无重复；EDN 确定且与仓库中的 `primitives.edn` 一致（过期时测试失败提示重建）；`cleat.kernel` Var 调用（`first` `add` `lt` `count` 的 int → long、`nth` 的 long → int、`vector` 的 params、arity 错误）。
