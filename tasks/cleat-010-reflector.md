# cleat-010 Reflector 与 TypeResolver（无 DLR）

- 里程碑：M1　依赖：cleat-008　状态：完成
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

## 结论（2026-10-10）

完成。
- `TypeResolver`（`src/Cleat.Core/Runtime/TypeResolver.cs`）：ClojureCLR 的 `ClrTypeSpec.cs` / `ClrTypeSpec2.cs` 原样复制到 `clojure.lang/Lib/`（加 EPL 头；`ClrTypeSpec2` 唯一的编译器依赖 `HostExpr.maybeSpecialTag` 换成本地 `MaybeSpecialTag`：`Compiler.PrimType` 的原语名 + `objects ints longs … sbytes` 数组名）。`Resolve(name, namespaces)`：特殊名 → ClrTypeSpec2 解析（反引号元数、推断元数 `OneG[int]`、嵌套 `A+B`、数组 `[] [,] [*]`、`*`/`&`、程序集限定名）并把 C# 风格 `List<Object>` 规范化成 `List[Object]`；单个名字先按原名、再按命名空间前缀顺序经 `RT.classForName`（已加载程序集 + DependencyContext）查找，泛型实参同样处理；成功结果按（名字，命名空间列表）缓存，失败不缓存返回 null；`ResolveE` 抛 `TypeNotFoundException`（附尝试过的命名空间）。**不查当前 Clojure 命名空间的 import 别名**（设计 04 §7：由展开器负责），否则缓存会跨命名空间串扰；ClojureCLR 测试里依赖 `ns.importClass` 别名的用例因此没有移植。`|…|` 转义 ClrTypeSpec2 本身不支持（reader 层处理），未加。
- `Reflector`（`src/Cleat.Core/Runtime/Reflector.cs`）：公开 API 与 `Cleat.Emit/EmitInterop.cs` 的 `CoreApi` 完全一致——`Call(Type, string, object instance, object[] args, Type[] paramTypes, Type[] genericArgs)`、`GetField`/`SetField(Type, string, object[, value])`、`New(Type, object[])`、`NewArray(Type, object)`、`Cast(Type, object)`、`Is(Type, object)`（都返回装箱的 Clojure 值）、`T Convert<T>(object)`、`ConvertFrom(object, Type)`、`MakeDelegate(IFn, Type)`。决议规则（ClojureCLR `IsCongruent`/`Subsumes` 移植，`BoxArg` 由 `Convert<T>` 取代，DLR 绑定器全部去掉）：按名字、静态/实例、元数（含 params 展开）筛选 → 逐参数匹配等级 0 精确 / 1 可赋值（含 null → 引用类型、装箱到 object/接口）/ 2 同类数值拓宽与 IFn → 委托 / 3 跨类拓宽（long → double）/ 4 收窄（long → int、double → float）与枚举名 → 逐参数支配者胜，等级全同时非 params 展开优先，再以 `Subsumes` 决胜，同签名取最靠近叶子的声明类型，仍不唯一抛 `More than one matching … found`（列出候选）。`paramTypes` 非 null 时按形参类型精确选；`genericArgs` 非 null 时只看泛型方法定义并 `MakeGenericMethod`。无参 `Call` 找不到方法退到属性/字段，`set_X` 一参退到字段；`GetField`/`SetField` 顺序为字段 → 属性 → 无参方法；`get_Item`/`set_Item` 经 `DefaultMemberAttribute` 映射，数组下标走 `Array.GetValue/SetValue`（多维也支持）；声明类型是接口而成员在运行时类型上时退到运行时类型，显式接口实现按接口类型调用；`ref`/`out`/指针/`Span` 参数不支持（`NotSupportedException`）。缓存：`ConcurrentDictionary<CallKey, Func<object, object[], object>>`（键 = 类型、名字、静态/实例、实例运行时类型、实参运行时类型序列（nil 用占位类型）、显式参数类型、泛型实参），值是 `System.Linq.Expressions` 编译的委托（实参转换内联或经 `Convert<P>`、返回值经 `ConvertFrom`），命中路径不再反射；构造函数、取值、赋值各一张表。`Convert<T>` 按 T 只构造一次（RT 的 `intCast`/`longCast`… 保证溢出与截断语义与 Clojure 一致；枚举接受 keyword/symbol/string/整数；IFn → 任意委托类型，每个委托类型只编译一次包装，回调参数按 `ConvertFrom` 转成 Clojure 表示、返回值经 `Convert<R>`）。`ConvertFrom`：int/short/byte/sbyte/ushort/uint → long，ulong 超出 long → BigInt，float → double，decimal → BigDecimal，其余（含枚举）不变。`Cast` 对 bool 是真值语义（`(boolean x)`），数值目标返回目标类型的装箱值（`(int 3.7)` → Int32 3）。
- LispReader 的 4 个 cleat-005 存根换成 Reflector/TypeResolver 调用：`#=(Ctor. …)` → `Reflector.New`，`#=(T/m …)`（命名空间部分既不是别名也不是已加载 ns 时视为类型；`T/new` → 构造）→ `Reflector.Call`，其余 `#=(f …)` 按 `Compiler.maybeResolveIn` 的 var 一半解析后 `applyTo`；`#ns.R[…]` → `Reflector.New`，`#ns.R{…}` → 静态 `create`。`*read-eval*` 门不变。
- 测试：`ReflectorTests` 18 个、`TypeResolverTests` 52 个（含从 ClojureCLR `TypeNameResolvingTests2` 移植的 7 组），全套 105 个通过（`dotnet test Cleat.slnx`）。`ClojureLangSmokeTests` 里断言 "#= 命中 cleat-005 存根" 的一条检查改成断言新行为（`Can't resolve +`）。
- 顺手验证：`EmitInterop.CoreApi` 用 `Type.MakeGenericMethodParameter(0)` 查 `Convert` 能匹配到 `T Convert<T>(object)`（实测），签名不用改。
- 未做 / 交给后续：泛型方法类型实参推断（只支持显式 `genericArgs`）；`ref`/`out`（by-ref 提示，设计 05 §5）；值类型实例上的字段/属性赋值（装箱副本，报 `NotSupportedException`）；`ReflectorTryCatchFixture` 只是 `try_catch.clj` 的宿主夹具，等 cleat-018 跑 test_clojure 时一并复制；`gen-delegate`/事件（02 §8）在展开器接通时补；按惯例自动加载同名程序集已由 `RT.classForName` 的 DependencyContext 探测覆盖，没有另写。
