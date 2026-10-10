# cleat-010 Reflector 与 TypeResolver（无 DLR）

- 里程碑：M1　依赖：cleat-008　状态：未开始
- 设计依据：[02 §8](../designs/02-runtime.md)、[05 §5](../designs/05-cil-backend.md)

## 目标
运行时反射互操作：按实参类型决议并缓存委托。

## 工作内容
1. `TypeResolver`：提取 `ClrTypeSpec2`（`|…|` 语法、泛型、数组、`alias-type`），去掉 `HostExpr.maybeSpecialTag`；按惯例自动加载同名程序集（IronScheme 02 §9 的做法）。
2. `Reflector`：`CallInstanceMethod`/`CallStaticMethod`/`InvokeConstructor`/`GetInstanceMember`/`SetInstanceMember`/`GetStaticMember`；重载决议（数值拓宽、`null`、`params`、`IFn` → 委托、`ref`/`out` 第一版不支持）；按（类型、名字、实参类型序列）缓存 `System.Linq.Expressions` 生成的委托。
3. 返回值转换：`void` → `nil`；数值类型按 Clojure 规则（`int` → `long` 装箱…）。
4. `MakeDelegate(IFn, Type)`：`Func<>`/`Action<>` 包装。
5. 测试：移植 ClojureCLR `ReflectorTryCatchFixture` 与 `TypeNameTests`；歧义重载报错信息。

## 验收标准
- 发射器（cleat-020）与 stage0（cleat-015）都只经这个类做运行时决议。
