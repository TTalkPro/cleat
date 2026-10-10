# cleat-005 提取试验：ClojureCLR 的 clojure.lang 不带 DLR 能否编译

- 里程碑：M0　依赖：cleat-001　状态：完成
- 设计依据：[01 §2](../designs/01-reference-analysis.md)、[02 §10](../designs/02-runtime.md)

## 目标
在正式提取（cleat-006 ~ 009）之前，用一个一次性项目确认 02 §10 清单的文件在去掉 DLR 与编译器引用后能编译，并列出每个文件需要改的位置。

## 工作内容
1. `spikes/cleat-005/`：复制 02 §10 "集合、数值、名字、引用、函数、reader" 六组文件，去掉 `using Microsoft.Scripting*`、`IDynamicMetaObjectProvider`、对 `Compiler`/`HostExpr`/`CljCompiler` 的引用（先以 `NotImplementedException` 占位）。
2. 逐文件记录改动点（行号、原因）到本文件；统计占位数量与种类（符号决议、`demunge`、`TryPrimTypeToName`、`load`…）。
3. 核对 `Symbol.GetHashCode`、`Keyword` 驻留、`PersistentHashMap` 哈希与 JVM 一致（Murmur3，`hasheq`）。
4. 评估 `BigInteger.cs`（ClojureCLR 自带）能否替换为 `System.Numerics.BigInteger`。

## 验收标准
- spike 项目编译通过，`PersistentHashMap`/`PersistentVector`/`Numbers` 的几条冒烟测试运行正确。
- 改动点清单成为 cleat-006 ~ 009 的工作内容。

## 结论（2026-10-10）

完成。ClojureCLR（master `a49f038b`，2026-10-09）的 `Lib/` + `Readers/` 去掉 DLR 与编译器后**可以作为独立类库编译**；冒烟程序 48 项全部通过，另有 2 项记录为已知差异。spike 位于 `spikes/cleat-005/`（`ClojureLang/` 类库 + `smoke/` 控制台，自带 `Directory.Build.props` 放宽样式检查；不入解决方案）。

### 编译与冒烟
- `ClojureLang.csproj`：148 个文件 56,231 行，`dotnet build` 0 错误、1 警告（`RT.DoInit` 占位后的不可达代码）。无 `DynamicLanguageRuntime` 包；仅保留 `Microsoft.Extensions.DependencyModel`（`RT.classForName` 用 `DependencyContext` 找运行时程序集，与 DLR 无关）。
- 占位：13 处 `throw new NotImplementedException("cleat-005 stub: …")`，全部标有 `// CLEAT-005:`（共 51 处标记）。按种类：加载器 7（`load-file`、`LoadSpecCode`、`RT.Init`、`Compiler.LoadAssembly`×2、`Compiler.load`、`Compiler.Compile`）、`#=` 与记录字面量 4（`Reflector.InvokeConstructor`/`InvokeStaticMethod`、`Compiler.maybeResolveIn`、`NamesStaticMember`）、语法引用无 resolver 的旧回退分支 2（已不可达）。另有去掉而不占位的：`IDynamicMetaObjectProvider`、DLR `ScriptRuntime` 引导、`Compiler.FindDuplicateType`、`TryLoadInitType`、`CompileFilesVar`×2、`CompilerOptionsVar`/`DirectLinkingKeyword`×3、`ClrTypeSpec`×2、`Reflector.prepRet`×2（恒等函数）。
- `demunge`、`TryPrimTypeToName` 不占位：与 01 §2 的判断一致，它们是纯名字处理，从 `Compiler.cs` 原样搬到新文件 `Lib/Munge.cs`。
- 冒烟（`smoke/Program.cs`，约 0.5 s）：`PersistentHashMap` 10 万条 assoc/valAt/transient；`PersistentVector` conj/nth/transient/equiv；`Numbers.add(long.MaxValue,1)` 抛 `ArithmeticException`，`addP` 得 `9223372036854775808N`；`1/3 + 1/6 = 1/2`，`1/3 * 3 = 1N`（与 JVM 一致）；`BigDecimal`；`Keyword.intern` 同一对象；`Symbol` 不同对象相等且哈希相等；`Var.intern`/`bindRoot`/另一线程 `pushThreadBindings` 互不影响；`Atom.swap` 8 线程 8100；`Ref` 事务、`Delay`；`ArityException` 消息经 `Munge.demunge`；`LispReader` 读 `(defn f [x] ^String (str x) {:a 1} #{1 2} [1 2.0 3N 4M 1/2] 'q `(a ~b) #"re")` 并用 `RT.printString` 打回同一文本，语法引用在固定命名空间 resolver 下得 `(quote smoke.ns/a)`、默认 resolver 下 `if` 不限定、`str` → `clojure.core/str`、`String/Empty` → `System.String/Empty`（命名空间默认导入）；`#(+ % %2 %&)`、`x#`、`::kw`、`::s/kw`、`#?(:cljr …)`；`#=` 命中占位；`EdnReader` 全部字面量 + `:readers` 标签；`Range`/`LazySeq`/`StringSeq`/`ArraySeq`/`Repeat`/`PersistentTreeMap`/`PersistentQueue`；`MultiFn` 只测构造（派发依赖 `clojure.core/isa?`、`parents` 两个 Var，需要 core.clj）。
- 已知差异（不算失败）：`RT.print` 没有 `bool` 分支，`RT.printString(true)` 得 `"True"`（ClojureCLR 靠 core_print 的 `print-method Boolean`）——cleat-006/012 的 `RT.print` 要补 `bool`；`Regex` 是引用相等，读回的 `#"re"` 与原 form 不 `Util.equals`（JVM `Pattern` 同样）。

### 改动点清单（行号为 ClojureCLR `a49f038b` 原文件行号）

**DLR 去除**

| 文件 | 行 | 改动 | 原因 |
|---|---|---|---|
| `AFn.cs` | 11–14 | 删 `using clojure.lang.Runtime.Binding; System.Dynamic; System.Linq.Expressions` | DLR |
| `AFn.cs` | 22 | 基类表去掉 `IDynamicMetaObjectProvider` | DLR |
| `AFn.cs` | 465–471 | 删 `GetMetaObject`（返回 `MetaAFn` 绑定器） | DLR |
| `RT.cs` | 11–14 | 删 `using CljCompiler.Context / clojure.lang.Runtime / TypeName2 / Microsoft.Scripting.Hosting` | DLR + 编译器 |
| `RT.cs` | 420–433 | 删静态构造器里 `ScriptRuntimeSetup`/`LanguageSetup`/`ScriptRuntime`/`ClojureContext` 引导 | DLR 宿主 |
| 不复制 | — | `Runtime/*`（`ClojureContext`、`ClojureOps`、`ClojureOptions`、`Converter`、`ConversionWrappers`、`Reflector`、`Printf`、`ImmutableDictionaryEnumerator`、`Binding/*`）、`Lib/ClrTypeSpec.cs`、`ClrTypeSpec2.cs`（引用 `HostExpr.maybeSpecialTag`） | 互操作组，按 02 §8 重写（cleat-010） |
| 复制 | — | `Runtime/Properties.cs`（`RT._versionProperties` 用）、`CljCompiler/ArrayIter.cs`（`RT.iter` 用，纯运行时代码放错了目录） | 运行时依赖 |

**编译器引用**

| 文件 | 行 | 改动 | 原因 |
|---|---|---|---|
| `ArityException.cs` | 45 | `Compiler.demunge` → `Munge.demunge` | 名字处理属运行时 |
| `Util.cs` | 832 | `Compiler.TryPrimTypeToName` → `Munge.TryPrimTypeToName`（`arrayTypeToSymbol`） | 同上 |
| 新 `Munge.cs` | — | 从 `Compiler.cs` 1095–1215、1030–1046 原样搬 `CHAR_MAP`/`DEMUNGE_MAP`/`DEMUNGE_PATTERN`/`munge`/`demunge`/`_primTypeNamesMap` | 供上两处与 cleat-012 |
| `RT.cs` | 346 | `LoadFileFn`：`Compiler.loadFile` → 占位 | 加载器 |
| `RT.cs` | 434 | `Properties.Resources.version`（resx → `Bootstrap/version.properties`）→ 字面量 | 去 resx |
| `RT.cs` | 498–520 | 删 `Compiler.InitializeCompilerOptions()` 与 `_doRTBootstrap` 块（绑定 `CompilerOptionsVar`/`DirectLinkingKeyword` 后 `load("clojure/core")`） | 启动流程改为"登记原语 → 加载 boot 镜像" |
| `RT.cs` | 524–536 | `LoadSpecCode`：`clojure.clr.api.Clojure.var/read` → 占位 | 依赖 api 与 spec 程序集 |
| `RT.cs` | 553–571 | `DoInit`：删 `CompilerOptionsVar` 绑定与 spec dll `Assembly.LoadFile` → 占位 | 加载器 |
| `RT.cs` | 720 | `ArrayIter` 来自 `CljCompiler/ArrayIter.cs` → 复制进 Lib | 目录放错 |
| `RT.cs` | 1140、1231 | `Reflector.prepRet(t, x)` → `x` | `prepRet` 在 ClojureCLR 里是恒等函数 |
| `RT.cs` | 2910–2916 | 删 `Compiler.FindDuplicateType(p)`（本次编译会话生成的类型） | 编译器要通过钩子登记生成类型 |
| `RT.cs` | 2949–2966 | `ClrTypeSpec.Parse(p)`、`.AssemblyName`、`.Name.DisplayName` → 取 `` ` ``、`[`、`,` 之前的前缀 | ClrTypeSpec 属互操作组 |
| `RT.cs` | 3064–3072 | 删 `ClrTypeSpec.GetTypeFromParsedName`（泛型/数组/别名类型规格） | 同上，cleat-010 提供 |
| `RT.cs` | 3586、3592、3595 | `.Map(...)` 扩展方法（在 `CljCompiler/Context/DynInitHelper.cs`）→ LINQ `Select` | 不引编译器 |
| `RT.cs` | 3605、3662 | `Compiler.LoadAssembly(FileInfo / byte[])` → 占位 | 加载 `.clj.dll` |
| `RT.cs` | 3619、3689 | 删 `Compiler.CompileFilesVar` 判断（`*compile-files*`），固定走 `LoadScript` | 编译驱动归 cleat-028 |
| `RT.cs` | 3632 | 删 `Compiler.TryLoadInitType` | 已加载编译命名空间的 `__Init__` 查找 |
| `RT.cs` | 3725、3736 | `Compiler.load(TextReader)`、`Compiler.Compile` → 占位 | 加载器钩子 |
| `LispReader.cs` | 1867、1873、1960、1970 | `Reflector.InvokeConstructor`/`InvokeStaticMethod`（`#=(Ctor. …)`、`#=(Type/m …)`、`#ns.Rec[…]`、`#ns.Rec{…}`）→ 占位 | Reflector 重写 |
| `LispReader.cs` | 1869、1876 | `Compiler.NamesStaticMember`、`Compiler.maybeResolveIn` → 占位 | `#=` 需要求值器 |

**reader 决议**

| 文件 | 行 | 改动 | 原因 |
|---|---|---|---|
| 新 `IReaderResolver.cs` | — | `IReaderResolver`（= JVM `LispReader.Resolver` 的 `CurrentNS/ResolveClass/ResolveAlias/ResolveVar` + `IsSpecial`）；`DefaultReaderResolver` 按 `*ns*` 的映射与别名实现 `Compiler.resolveSymbol` 的规则（特殊形式表暂硬编码 24 个；不处理数组类型符号 `HostExpr.MaybeArrayType`） | 02 §10 |
| `LispReader.cs` | 73–79 | 删嵌套接口 `Resolver`；16 处 `(Resolver)RT.ReaderResolverVar.deref()` / `Resolver resolver` 改为 `IReaderResolver`（192、198、229、407、545、550、595、1202、1395、2120、2147…） | 统一接口 |
| `LispReader.cs` | 635、637、1209、1220 | `Compiler.CurrentNamespace` → 本地 `CurrentNamespace`（`RT.CurrentNSVar`） | 本不是编译器依赖 |
| `LispReader.cs` | 1388–1389 | `Compiler.IsSpecial` → `ResolverOrDefault().IsSpecial`；`Compiler.QuoteSym` → `QUOTE` | 特殊形式表由展开器经 resolver 提供 |
| `LispReader.cs` | 1395 | `resolver = *reader-resolver* ?? DefaultReaderResolver` | 消灭空 resolver 分支 |
| `LispReader.cs` | 1419、1455–1467 | 无 resolver 回退（`Compiler.resolveSymbol`、`CurrentNamespace.GetMapping` 判 Type）→ 不可达占位 | 规则已在 `DefaultReaderResolver` |
| `LispReader.cs` | 1469、1521 | `Compiler.QuoteSym` → `QUOTE` | — |
| `LispReader.cs` | 1781、1785 | `Compiler.AmpersandSym`、`Compiler.FnSym` → 本地 `AMPERSAND_SYM`、`FN_SYM` | `#(…)` |
| `LispReader.cs` | 150、160 | `OPT_READ_COND`、`COND_ALLOW` 改 public | 冒烟用，可保留 |

**RT 拆分（给 cleat-012 的事实）**：`RT.cs` 3956 行里与编译器/加载耦合的只有四块——静态构造器引导（418–520）、`Init`/`DoInit`/`PostBootstrapInit`/`LoadSpecCode`（524–571，含 socket server 启动）、`classForName` 族 + `_runtimeAssemblyNames`/`ResolveAssembly`（357、2772–3080）、`load`/`LoadScript`/`Compile`/`TryLoadFromEmbeddedResource`/`LoadCljScript`/`FindFile`/load path（3570–3760）。其余值操作（seq/cons/nth/get/assoc/print/…）无耦合，可整体留在 `RT`。另：`RT.print` 缺 `bool` 分支；`MultiFn` 的静态字段 `RT.var("clojure.core","isa?"/"parents")` 使派发依赖 core.clj（JVM 相同）。

**BigInteger 决策**：ClojureCLR 自带 `BigInteger.cs`（3432 行，无 DLR 依赖，仅注释提到 `Microsoft.Scripting.Math`）。`Numbers`/`BigInt`/`BigDecimal`/`Ratio`/两个 reader 用到的成员：
- 有 BCL 直接对应：`Zero/One/NegativeOne`、`Create(long/ulong/int/uint/double/decimal)`（显式转换）、`Parse(string)`、`ToString()`、`Add/Subtract/Multiply/Divide/Negate/Abs`、`CompareTo/Equals`、`Signum`（`Sign`）、`IsZero/IsNegative/IsPositive`、`IsOdd`（`!IsEven`）、`DivRem`、`Mod`（ClojureCLR 的 `Mod` 就是截断余数，与 `%` 一致，无 floor 语义差异）、`Power`（`Pow`）、`Gcd`（`GreatestCommonDivisor`）、`ToDouble/ToInt64/ToUInt64/ToDecimal`（显式转换；`ToDouble` 舍入需测试）、`TestBit`（移位）。
- 无直接对应、要写适配：`Parse(string, int radix)`（`LispReader` 879、`EdnReader` 575，`2r1010`/`36rZZ`）；`AsInt64/AsUInt64/AsInt32/AsUInt32(out)`（`BigInt` 54/132/164，`Numbers`）；`Precision`（十进制位数）与 `UIntPrecision`（`BigDecimal` 282、2151）；`GetMagnitude()`（`BigDecimal` 1116）；`new BigInteger(sign, uint[])`、`GetDoubleSignificand/GetDoubleBiasedExponent/DoubleShiftBias/DoubleExponentBias`（`BigDecimal` 331–342，double 精确转 BigDecimal 的静态位操作，可搬到 `BigDecimal`）；`Ten/Five` 常量；`GetHashCode`——ClojureCLR 实现就是 Java `BigInteger.hashCode` 的算法（`31*h + mag[i]`，再乘符号），`BigInt.hasheq` 超出 long 时直接用它，与 JVM 哈希一致，BCL 的不一致。
- 结论：**可以替换**，代价是一个约 150 行的 `BigIntegerOps` 适配（radix 解析、`TryGetInt64` 等、十进制位数、Java 兼容哈希）和 `BigDecimal.cs` 内约 15 处改写；收益是少 3432 行、用 BCL 的优化算法。建议在 cleat-006 替换，并为 radix 解析、`BigDecimal` 精度/舍入、`(double big)` 舍入、大整数 `hash` 加测试；若 cleat-006 时间紧，先原样保留，替换不影响接口。

### 核对
- `Symbol.GetHashCode()` = `Util.hashCombine(Murmur3.HashString(name), Util.hash(ns))`，`hasheq()` 同式用 `Util.hasheq(ns)` 并缓存——**按名字**，不同对象相等则哈希相等（冒烟验证）。注意 ClojureCLR 对字符串用 `Murmur3.HashString(s)` 直接哈希字符，JVM 是 `Murmur3.hashInt(s.hashCode())`，所以字符串/符号/关键字的具体哈希值与 JVM **不同**（.NET 的 `string.GetHashCode` 随机化，无法照搬）；只能要求"规则同构"，不能要求数值相同。
- `Keyword`：`JavaConcurrentDictionary<Symbol, WeakReference>` 驻留（与 JVM 一样弱引用），`intern` 返回同一对象；哈希 = `sym.hasheq() + 0x9e3779b9`，与 JVM `Keyword.hasheq` 相同。
- `PersistentHashMap.hash(k)` = `Util.hasheq(k)`（`IHashEq` → `hasheq`，数字 → `Numbers.hasheq`，字符串 → Murmur3）；`APersistentMap.hasheq` = `Murmur3.HashUnordered`，向量/列表 = `HashOrdered`——与 JVM 的 `hasheq` 路径同构（冒烟：向量与列表 `[1 2]`/`(1 2)` hasheq 相等）。

### 复制文件与行数（含 2 个新文件；spike 总计 148 文件 56,231 行）

| 组 | 文件数 | 行数 | 文件 |
|---|---|---|---|
| 集合 | 79 | 17,688 | `APersistent{Map,Set,Vector}`、`AMapEntry`、`ASeq`、`ATransient{Map,Set}`、`ArrayChunk`、`ArrayHelper`、`ArrayIter`、`ArraySeq`、`Box`、`ChunkBuffer`、`ChunkedCons`、`Cons`、`Cycle`、`EnumeratorSeq`、`Iterate`、`LazilyPersistentVector`、`LazySeq`、`LongRange`、`MapEntry`、`MapEnumerator`、`MapEquivalence`、`Murmur3`、`Obj`、`Persistent{ArrayMap,HashMap,HashSet,List,Queue,StructMap,TreeMap,TreeSet,Vector}`、`Range`、`RecordEnumerable`、`Repeat`、`SeqEnumerator`、`StringSeq`、`TransformerEnumerator`、`Tuple`、`Util`；接口 `Associative`、`Counted`、`IChunk`、`IChunkedSeq`、`IDrop`、`IEditableCollection`、`IHashEq`、`IKVReduce`、`ILookup`、`IMapEntry`、`IMapEnumerable`、`IMeta`、`IObj`、`IPending`、`IPersistent{Collection,List,Map,Set,Stack,Vector}`、`IReduce`、`IReduceInit`、`ISeq`、`ITransient{Associative,Associative2,Collection,Map,Set,Vector}`、`Indexed`、`IndexedSeq`、`Named`、`Reversible`、`Seqable`、`Sequential`、`Sorted` |
| 数值 | 6 | 11,683 | `Numbers`（4742）、`BigInteger`（3432）、`BigDecimal`（2580）、`BigInt`、`Ratio`、`WarnBoxedMathAttribute` |
| 名字 | 12 | 3,346 | `Symbol`、`Keyword`、`Var`（1297）、`Namespace`、`AReference`、`ARef`、`IRef`、`IDeref`、`IReference`、`Settable`、`IKeywordLookup`、`KeywordLookupSite` |
| 引用 | 17 | 3,624 | `Atom`、`Ref`、`LockingTransaction`、`Agent`、`Volatile`、`Delay`、`Reduced`、`Future`、`CountDownLatch`、`IAtom`、`IAtom2`、`IBlockingDeref`、`Atomic{Boolean,Integer,Long,Reference}`、`JavaConcurrentDictionary` |
| 函数 | 18 | 11,174 | `IFn`（2829）、`RestFn`（4952）、`AFn`、`AFnImpl`、`AFunction`、`RestFnImpl`、`MultiFn`、`MethodImplCache`、`ArityException`、`Fn`、`FuncTypes`、`IFnArity`、`IFnClosure`、`ILookupSite`、`ILookupThunk`、`IProxy`、`IRecord`、`IType` |
| reader | 8 | 4,272 | `LispReader`（2287）、`EdnReader`（1170）、`ReaderConditional`、`TaggedLiteral`、`JReMatcher`、`Readers/LineNumberingTextReader`、`Readers/PushbackTextReader`、`Readers/PushbackInputStream` |
| 异常 | 4 | 240 | `ClojureException`、`ExceptionInfo`、`IExceptionInfo`、`TypeNotFoundException` |
| RT | 2 | 3,979 | `RT`（3891，占位后）、`Runtime/Properties` |
| 新增 | 2 | 225 | `Munge`、`IReaderResolver` |

ClojureCLR `Lib/` 共 143 文件，未复制 2 个（`ClrTypeSpec.cs`、`ClrTypeSpec2.cs`）；`CljCompiler/`、`Runtime/`（除 `Properties.cs`）均未复制。`AFnImpl`/`RestFnImpl`/`FuncTypes`/`IFnClosure`/`Tuple`/`PersistentStructMap` 等不在 02 §10 清单但无耦合，一并复制以便编译，正式提取时可按需裁剪。

### 对 cleat-006 ~ 009、012 的输入
- cleat-006：按上表复制集合与数值组；`Util.cs` 832 改 `Munge`；决定是否替换 `BigInteger`（见上）；`RT.print` 补 `bool`。
- cleat-007：名字与引用组原样复制，`Symbol`/`Keyword` 哈希无需改。
- cleat-008：`AFn.cs` 三处去 DLR；`ArityException` 改 `Munge.demunge`；`Munge.cs` 入 Cleat.Core。
- cleat-009：`IReaderResolver` + `DefaultReaderResolver` 按本 spike；`#=` 与记录字面量留给 Reflector/求值器钩子；特殊形式表改由展开器（cleat-013）经 resolver 提供。
- cleat-012：`RT.cs` 按"RT 拆分"一节四块切到 `Loader`；`classForName` 的 `ClrTypeSpec` 部分等 cleat-010；`ArrayIter.cs` 归集合组。
