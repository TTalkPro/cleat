# 05 CIL 后端：Lcil 与发射器 Cleat.Emit

> 继承 IronScheme `designs/04-lcil-and-emitter.md` 的全部设计（文本格式、校验、常量 fasl、持久化 / 内存两种模式、确定性回填、加载约定），只记录差异与扩展。IronScheme 在 .NET 10 上验证过的结论（`PersistedAssemblyBuilder`、内存程序集分块、MVID 回填）直接采用。

## 1. 继承项

- 文本 Lcil（S 表达式）与自带读取器；`(lcil 1 (assembly …) (constants …) (type …) …)` 顶层结构；`type`/`field`/`method` 子句；全部 `OpCodes` 按名书写；伪指令 `label`、`ldconst`、`try`、`line`；互操作指令 `clr-call`/`clr-field-get`/`clr-field-set`/`clr-new`/`clr-new-array`/`clr-cast`/`clr-is`/`clr-type`；读取时校验；发射器决议规则（§5）；`EmitInMemoryData` 直接接收 S 表达式的 eval 快速路径；内存模式每 1000 个类型一个动态程序集；MVID 与时间戳按 SHA256 回填；可选 ILVerify；Portable PDB。
- 发射器项目 `src/Cleat.Emit/`，只依赖 BCL；对 `Cleat.Core` 按名字解析。

## 2. 扩展

| 扩展 | 原因 |
|---|---|
| `(type … (interfaces "I" …))` 与 `(method … (attrs override) (implements "I" "名字"))` | `deftype*`/`reify*` 实现接口；显式接口实现用 `DefineMethodOverride` |
| `(type … (attrs interface))` 与抽象方法 | `definterface` |
| 字段属性 `volatile`（`modreq IsVolatile`）、公开可变字段 | `^:volatile-mutable`、`^:mutable` |
| `(switch (L …))` 指令 | `case*` 的 `:int` 与哈希 switch |
| `(ldtoken "T")` + `Type.GetTypeFromHandle` 的 `(clr-type T)` 已有；加 `(clr-static-field …)` 对 const 字段内联 | 互操作 |
| 常量编码（§3）加 Clojure 数据 | — |
| 程序集属性 `[Cleat.Core.CleatModule]`；入口 `Initialize` 返回 `object` | 加载约定 |
| 类型的 `[Serializable]` 不加；`deftype` 类加 `IType`，`defrecord` 加 `IRecord` | 与 Clojure 一致 |

cleat-003 实测（2026-10-10）：以上扩展在内存与持久化两条路径都可行；`CreateType` 的耗时随同一模块内的类型数平方增长（1 万个类型单模块 7.6 s，每 1000 个一个程序集 0.9 s），所以内存模式按 1000 个类型分块（继承 IronScheme 的做法）；跨动态程序集引用的类型必须是 public；全部动态程序集统一用 `RunAndCollect`（`Run` 程序集引用 `RunAndCollect` 类型会在 `Emit` 时抛 `NotSupportedException`）。

## 3. 常量编码

编译器 `cleat.compiler.constants/encode` 输出中立编码，发射器 `FaslEncoder` 转二进制并嵌入 `$Constants` 的 RVA 数据（与 IronScheme 相同），运行时 `Cleat.Core.Runtime.Fasl` 读回：

| 编码 | 含义 |
|---|---|
| `(nil)` `(true)` `(false)` | 单例 |
| `(long n)` `(double "位模式")` `(bigint "十进制")` `(bigdec "…")` `(ratio "n/d")` `(char n)` `(str "…")` | 标量 |
| `(sym ns name)` `(kw ns name)` | 符号 / 关键字；关键字按 `Keyword.intern` 取回 |
| `(var ns name)` | Var 引用：加载时 `Var.intern(ns, name)`（不创建命名空间以外的副作用） |
| `(list e …)` `(vec e …)` `(map k v …)` `(set e …)` `(sorted-map …)` `(sorted-set …)` `(queue …)` | 持久集合；map / set 按键的打印形式排序写出，保证确定 |
| `(meta m e)` | 带元数据的对象 |
| `(regex "…")` `(inst "…")` `(uuid "…")` `(type "T")` | 其他字面量；`type` 加载时 `Type.GetType` |
| `(record "T" e …)` | `defrecord` 实例（`#ns.R{…}` 字面量）：按类型的 `create` 静态方法 |
| `(def n x)` / `(ref n)` | 共享 |

无法编码的（函数、原子、宿主对象）报编译错误（Clojure 也不允许）。

## 4. 代码生成约定（codegen-cil 的输出形状）

| L3 节点 | Lcil |
|---|---|
| `fn`（有自由变量） | `(type "ns$name__N" (base AFunction|RestFn) (field 自由变量 …) (method ".ctor" …) (method "invoke" (params Object × k) …) … (method "getRequiredArity" …))`；调用点 `newobj` + 传自由变量 |
| `fn`（无自由变量） | 同上 + `(method "invokeStatic" (attrs static) …)`，实例 `invoke` 转发；类型有 `(field "Instance" static)`，在 `.cctor` 建单例 |
| `label`/`jump` | 方法内标签 + 局部变量重赋 + `br` |
| `let` | 局部变量 |
| `box` | `clojure.lang.Box`（或新 `Cleat.Core.Box`）实例 |
| `if` | `RT.booleanCast` 内联：`null` / `RT.F` 为假 |
| `try` | `(try (body …) (catch "T" …) (finally …))`；表达式位置的 `try` 先提升为语句存临时变量（IronScheme lift-try） |
| `throw` | `castclass Exception; throw` |
| `monitor-enter/exit` | `Monitor.Enter/Exit` |
| `case` | `switch` + 哈希比较；与 Clojure `CaseExpr` 相同 |
| `def` | `ldconst var; …; callvirt Var::bindRoot`；元数据 `setMeta` |
| `var-ref`（非 dynamic） | `ldconst var; callvirt Var::getRawRoot`；dynamic 用 `deref` |
| `deftype` | `(type "ns.Name" (interfaces …) (field …) (method ".ctor") (method 接口方法 (attrs override)) …)` + 静态 `getBasis` |
| 顶层形式 | 每个一个 `Init<k>` 静态方法；模块入口 `Initialize` 顺序调用（方法大小不受 CIL 限制，但保持可读） |

## 5. 发射器的决议规则

IronScheme 05 §5 的规则 1 ~ 4 不变，另加：
- 实参类型提示 `(tag …)` 来自展开器；提示与形参类型完全匹配时静态决议，否则规则 3（运行时 `Reflector`）。
- 属性 / 字段 / 索引器按 Clojure 规则（`(.name x)` 无参时先找字段、再属性、再无参方法）。
- `params` 数组、`ref`/`out`（`by-ref` 提示）、泛型方法（显式类型参数或推断）。
- 委托参数：`IFn` 实参按形参委托类型包装（`Reflector.MakeDelegate`）。

## 6. 加载约定

- `[Cleat.Core.CleatModule("ns.__init", "Initialize")]`；`Initialize` 执行全部顶层形式。
- `.clj.dll` 旁的接口文件 `名字.ifc`（EDN）：已知函数的类名、arity；boot 镜像的 `cleat.boot.ifc` 含镜像 id。
- 内存程序集（eval）不写接口文件。
