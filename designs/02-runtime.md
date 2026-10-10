# 02 运行时 Cleat.Core

## 1. 项目

| 项 | 决定 |
|---|---|
| 程序集 | `Cleat.Core`，net10.0，只依赖 BCL（含 `System.Numerics.BigInteger`、`System.Reflection.Emit`） |
| 命名空间 | 从 ClojureCLR 提取的类型保留 **`clojure.lang`**（`clojure.lang.IFn`、`PersistentHashMap`、`Var`…），放 `src/Cleat.Core/clojure.lang/`（EPL，00 D11）；新写的放 `Cleat.Core.*`（`Runtime`、`Expander`、`Interop`） |
| 不引入 | DLR、`Microsoft.Scripting.*`、spec 程序集、tools.reader |

## 2. 值表示

| Clojure 值 | 表示 | 来源 |
|---|---|---|
| `nil` | `null` | 同 ClojureCLR |
| 布尔 | 装箱 `bool`；`RT.T` / `RT.F` 单例，比较用 `RT.booleanCast` | 同 |
| 整数 | `long`（装箱）；溢出按 Clojure 规则抛 `ArithmeticException`，`+'` 升为 `BigInt` | `Numbers.cs` |
| `BigInt` / `BigDecimal` / `Ratio` | 提取；`BigInteger` 用 BCL | 同；ClojureCLR 自带的 `BigInteger.cs` 若与 BCL 重复则删 |
| 浮点 | `double`（装箱） | 同 |
| 字符 | `char`（装箱）；非 BMP 码点按 Clojure 规则是两个 `char` | 同 |
| 字符串 | `string` | 同 |
| 符号 / 关键字 | `Symbol`、`Keyword`（关键字按 `(ns, name)` 驻留，`Symbol` 不驻留，但 `GetHashCode` 按名字） | 同；哈希必须与宿主无关（08 §7） |
| 集合 | `PersistentList`、`PersistentVector`、`PersistentArrayMap`、`PersistentHashMap`、`PersistentHashSet`、`PersistentTreeMap/Set`、`LazySeq`、`Cons`、`ChunkedCons`、`Range`、transient 版本、`PersistentQueue` | 提取 |
| 引用类型 | `Var`、`Atom`、`Ref`（STM）、`Agent`、`Volatile`、`Delay`、`Future`/`Promise`（用 `Task`） | 提取 |
| 函数 | `IFn` 实现（§4） | 提取接口，重写基类 |
| 类型 / 记录 | `deftype*` / `defrecord` 生成的真实 CLR 类型 | 编译器 / stage0 生成 |
| 正则 | `System.Text.RegularExpressions.Regex` | 同 |
| 元数据 | `IObj` / `IMeta`，`with-meta` 复制 | 提取 |

## 3. Var 与 Namespace

- `Var`、`Namespace` 整体提取。`Var` 字段：`root`、`ns`、`sym`、`dynamic`、`threadBound`、`meta`；线程绑定用 `ThreadLocal`（ClojureCLR 现状）。cleat-003 实测：`AsyncLocal` 写入 25 ns 且分配 72 字节、读取 4.5 ns，`ThreadLocal` 写 1.3 ns 无分配、读 2.6 ns；且 `AsyncLocal` 会流入 `Task.Run` / 新线程，与 Clojure `binding` 的线程语义不符。**决定：用 `ThreadLocal`，不用 `AsyncLocal`。**
- **编译后代码访问 Var**：常量表持有 `Var` 对象（05 §3 编码 `(var ns name)`，加载时 `Var.intern`），读 `getRawRoot()`（非 dynamic，编译器按 Lcore 中的元数据判断）或 `deref()`；`def` 调 `bindRoot`。
- **直接链接**（00 D13）：同一编译单元内对非 `^:dynamic`、非 `^:redef`、已定义为 `fn*` 的 Var 的调用直接 `call` 该函数类的 `invokeStatic`；跨单元按接口文件（04 §6），boot 镜像自带接口文件。`*compiler-options*` 的 `:direct-linking` 第一版恒为开，可按 Var 关掉。
- 宏：`Var` 的 `:macro` 元数据；展开器（03）查它。
- `*ns*`、`*file*`、`*warn-on-reflection*`、`*unchecked-math*`、`*compile-files*`、`*compile-path*`、`*loaded-libs*` 等运行时 Var 由 `RT` 初始化时建立。

## 4. 函数模型

```csharp
public interface IFn { object invoke(); object invoke(object a); … object invoke(object a1,…,object a20, params object[] rest); object applyTo(ISeq args); }
public abstract class AFn : IFn { /* 每个 invoke 默认抛 ArityException；applyTo 按长度分派 */ }
public abstract class AFunction : AFn, IObj, IComparer, … { /* 固定参数函数的基类：元数据、比较器适配 */ }
public abstract class RestFn : AFunction { public abstract int getRequiredArity(); protected virtual object doInvoke(object a, …, object rest) …; /* invoke(k) 在 k >= required 时打包剩余参数为 ArraySeq 调 doInvoke */ }
```

- 与 ClojureCLR 一致，去掉 `IDynamicMetaObjectProvider`。两个基类的职责分明：`AFunction` 子类只重写有定义的 `invoke(k)`；`RestFn` 子类重写 `doInvoke`，`invoke(k)` 由基类实现（不再是生成代码里的 `RestFnImpl` 样板）。
- 每个 `fn*` 一个类：自由变量是实例字段（构造函数参数）；每个参数个数一个方法；无自由变量且不引用 `this` 时另生成静态 `invokeStatic`，实例方法转发（供直接链接与 stage0 内联）；`^:once` 函数调用后清空字段。
- 原始类型签名（`IFn.LL`、`IFn.OD`…）：第一版**不做**，参数与返回值全部 `object`；拆箱留到后续（04 §10）。`^long`/`^double` 提示只用于宿主互操作决议。
- `apply`、`MultiFn`、协议方法、`Keyword` 作为函数、集合作为函数（`IFn` 由集合类型实现）：提取，不改。
- `ArityException`、`demunge`：`Compiler.demunge` 的实现移到 `Cleat.Core.Runtime.Munge`（展开器与运行时共用）。

## 5. 原语表与运行时契约

- C# 原语（`RT.*`、`Numbers.*`、`Util.*` 中编译器要静态调用的方法）用 `[Primitive("name", Pure = …, Inline = …)]` 标注；`Cleat.PrimGen` 反射生成 **`src/boot/primitives.edn`**：

```clojure
{:version 1
 :primitives
 {clojure.core/+   [{:target :clr  :type "clojure.lang.Numbers" :method "add" :arity 2 :flags #{:pure :inline-arith}}
                    {:target :llvm :symbol "cleat_num_add" :arity 2 :flags #{:pure}}]
  cleat.kernel/car …}}
```

- 编译器只读这个文件（04 §5）。`llvm` 列由 `native/cleat-rt/primitives.h` 的注释生成（06 §3），两列的名字与 arity 必须一致，PrimGen 核对。
- 原语以 Var 的形式对 Clojure 可见：启动时 `PrimitiveRegistry` 把每个原语包装成 `IFn` 绑到同名 Var（`cleat.kernel/…`），`core.clj` 的 `kernel` 层直接 `def` 到它们。

## 6. 钩子与异常

- `RuntimeHooks`：`Cleat.Core` 中需要调用 Clojure 函数的地方（`print-method`、`ex-info` 的打印、`*err*` 输出、`Compiler.macroexpand` 对 `:inline` 的处理）经钩子；boot 初始化时由 Clojure 登记。取代 ClojureCLR 里 C# 直接 `RT.var("clojure.core", "…")` 查 Var 的硬编码（保留 `RT.var` 作为非热点的后备）。
- 异常：Clojure 的 `throw` 抛任意 `Exception`；`ex-info` 为 `ExceptionInfo : Exception, IExceptionInfo`。`try`/`catch` 按 CLR 类型匹配；`finally` 用 CLR finally。`monitor-enter/exit` 用 `Monitor`。

## 7. Reader

- `LispReader` 与 `EdnReader` 提取。`LispReader` 对编译器的 19 处引用改为接口 `IReaderResolver`（`CurrentNamespace`、`ResolveSymbol`、`IsSpecial`、`NamesStaticMember`）由展开器实现；`#=` 保留但只允许构造器与 Var 调用（与 Clojure 一致）。
- reader 条件特性：`:cleat`、`:cleat-clr` / `:cleat-llvm`（按编译目标）、`:cljr`（兼容别名，CLR 目标时为真）、`:default`。
- `*read-eval*`、`*data-readers*`、`*default-data-reader-fn*` 行为同 Clojure。

## 8. 宿主互操作运行时（无 DLR）

- `Reflector`（重写，规则按 IronScheme `Interop` 与 ClojureCLR `Reflector` 的交集）：`CallInstanceMethod(name, target, args)`、`CallStaticMethod(type, name, args)`、`InvokeConstructor`、`GetInstanceField/Property`、`SetInstanceField/Property`：按实参运行时类型做重载决议（数值拓宽、`null` 匹配引用类型、`IFn` → 委托、`params`），结果按（类型、名字、参数类型序列）缓存为编译好的委托（`System.Linq.Expressions` 生成一次）。
- `TypeResolver`：ClojureCLR 的 `ClrTypeSpec2` 提取（`|Dictionary[String, List<long>]|`、泛型、数组、`alias-type`），去掉对 `HostExpr.maybeSpecialTag` 的引用（移到 `Cleat.Core.Expander`）。
- 发射器决议不了的成员调用编译为对 `Reflector` 的调用，并在 `*warn-on-reflection*` 时由**展开器**警告（展开器查得到类型，04 §7）。
- 委托与事件：`gen-delegate`、`IFn` → `Func<>`/`Action<>` 转换由 `Reflector` 提供；`proxy` 第一版以 `reify` 覆盖的子集实现，`gen-class` 后续。

## 9. 加载器

- `RT.load` 拆成两半：`Cleat.Core.Runtime.Loader`（找文件 / 程序集、决定源码还是 dll、调入口）与展开 + 编译的回调（由 boot 镜像里的编译驱动登记为钩子）。
- 程序集约定（05 §6）：`[Cleat.Core.CleatModule("入口类型", "Initialize")]`；`.clj.dll` 命名与 ClojureCLR 相同（`clojure.core.clj.dll`），加载路径按 `CLEAT_LOAD_PATH` 与 `-I`。
- boot 镜像 `cleat.boot.dll`：全部 core 命名空间 + `cleat.nanopass` + `cleat.compiler`；入口按序初始化并登记钩子与编译驱动。

## 10. 从 ClojureCLR 提取的清单与改动

| 组 | 文件 | 改动 |
|---|---|---|
| 集合 | `Persistent*.cs`、`Transient*.cs`、`ASeq.cs`、`LazySeq.cs`、`Cons.cs`、`Chunked*.cs`、`Range.cs`、`LongRange.cs`、`Repeat.cs`、`Iterate.cs`、`Cycle.cs`、`ArraySeq.cs`、`StringSeq.cs`、`EnumeratorSeq.cs`、`APersistent*.cs`、`PersistentQueue.cs`、`Murmur3.cs`、`Util.cs` | `Util.TryPrimTypeToName` 移出；其余不改 |
| 数值 | `Numbers.cs`、`BigInt.cs`、`BigDecimal.cs`、`Ratio.cs`、`BigInteger.cs`（评估替换为 BCL） | 不改 |
| 名字 | `Symbol.cs`、`Keyword.cs`、`Var.cs`、`Namespace.cs` | `Symbol.GetHashCode` 核对为按名字 |
| 引用 | `Atom.cs`、`Ref.cs`、`LockingTransaction.cs`、`Agent.cs`、`Volatile.cs`、`Delay.cs`、`Reduced.cs`、`ARef.cs`、`AReference.cs` | 不改 |
| 函数 | `IFn.cs`、`AFn.cs`、`AFunction.cs`、`RestFn.cs`、`MultiFn.cs`、`MethodImplCache.cs`、`ArityException.cs`、`Fn.cs` | 去 DLR；`RestFn` 的 `invoke(k)` 实现在基类 |
| reader | `LispReader.cs`、`EdnReader.cs`、`Readers/*.cs` | 引入 `IReaderResolver` |
| RT | `RT.cs` | 拆：值操作留 `RT`；`load`、`classForName` 的编译器部分移 `Loader`；去 `Microsoft.Scripting.Hosting`；启动流程改为"登记原语 → 加载 boot 镜像" |
| 互操作 | `ClrTypeSpec*.cs`、`Reflector.cs`、`Converter.cs` | `Reflector` 重写（§8） |
| 不提取 | `CljCompiler/*`、`Runtime/Binding/*`、`ClojureContext.cs`、`GenClass.cs`、`GenProxy.cs`、`GenDelegate.cs`、`GenInterface.cs` | — |
