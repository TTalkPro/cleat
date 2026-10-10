# Lcil 第 1 版规格（cleat-019 冻结；cleat-045 加 runtime 子句与联合常量编码；cleat-051 加无歧义的 Clojure 常量编码）

继承 IronScheme `designs/04-lcil-and-emitter.md` §2 的全部内容，本文只写完整规格与 cleat 的扩展（标 **[cleat]**）。设计背景见 [designs/05-cil-backend.md](../../designs/05-cil-backend.md)、[designs/15 §1](../../designs/15-scheme-core-vendoring.md)。

## §1 词法

S 表达式，UTF-8。读取器 `Cleat.Emit.SExprReader` 只认：列表 `( … )` / `[ … ]`（等价）、符号（除空白、括号、`"`、`;` 以外的字符序列且不是数）、字符串 `"…"`（转义 `\" \\ \n \r \t \xHH;`）、整数（十进制或 `#x…`，64 位）、浮点（十进制小数 / 指数；`+inf.0` `-inf.0` `+nan.0` 只在 `ldc.r8` 操作数处接受）、布尔 `#t` `#f`、注释 `;` 到行尾与 `#| … |#`。

## §2 顶层

```scheme
(lcil 1
  (assembly "名字"
    (references "Cleat.Scheme" …)?           ; 额外引用的程序集（名字或 dll 路径）
    (entry "入口类型" "入口方法")?            ; 入口：public static object 方法()（§6）
    (runtime scheme|clojure)?                ; [cleat] 互操作指令与常量表所属的运行时，缺省 clojure（§5、§8）
    (attribute "T" "字符串参数" …)*)         ; [cleat] 程序集级自定义属性，构造函数只取字符串参数
  (constants (0 编码) (1 编码) …)?            ; 常量表（§8 的编码），下标从 0 连续
  (live-constants n)?                        ; 内存模式：常量由宿主直接提供（与 constants 互斥）
  (type …) …)
```

**隐含引用**：`Cleat.Core`（`CleatModuleAttribute`、Clojure 互操作、活常量池）总是引用；`Cleat.Scheme`（Scheme 互操作、编码常量表的读回 `Cleat.Scheme.Fasl.ReadPackedTable`）在程序集或任一方法的 `runtime` 为 `scheme`、或有 `constants` 时引用。

## §3 类型、字段、方法

```scheme
(type "名字" (base "基类型")? (attrs 属性…)? (interfaces "接口" …)? (attribute "T" "参数" …)*
  (field "名字" "类型" 属性…)
  (method "名字" (attrs 属性…)
    (params "类型" …) (ret "类型")
    (locals "类型" …)?
    (clr-namespaces "命名空间" …)?
    (runtime scheme|clojure)?                ; [cleat] 覆盖程序集的 runtime（只影响本方法的互操作指令）
    (implements "接口" "接口方法名")?        ; [cleat] 显式接口实现
    (source "文件")?
    (body 指令 …)))
```

- 类型属性：`public`（默认）`sealed` `abstract` `interface`。`interface` 类型没有基类、没有实例字段、方法必须 `abstract`（或 `static`）。嵌套类型不支持。`base` 缺省 `System.Object`。
- 字段属性：`static` `public`（默认）`private` `readonly` **`volatile`**（[cleat]：`modreq(System.Runtime.CompilerServices.IsVolatile)`，供 `^:volatile-mutable`；读写仍用普通 `ldfld`/`stfld`，发射器自动加 `volatile.` 前缀）。
- 方法属性：`static` `virtual` `override` `abstract` `public`（默认）`private` `ctor`（名字须为 `.ctor`，不写 `ret`）`cctor`（名字 `.cctor`）。
- **[cleat] `implements`**：本方法用 `DefineMethodOverride` 绑定到 `(interfaces …)` 中所列接口的该方法；方法仍按自己的名字定义（可为 `private`，用于显式实现与名字冲突的接口方法）。不写 `implements` 的 `override`/`virtual` 公开方法按名字与签名隐式实现接口。
- 实例方法的 `ldarg 0` 是 `this`，`params` 只列显式参数。构造函数体须自己调用基类构造函数。

## §4 类型引用

字符串 `"T"`：本 Lcil 中定义的类型名；否则全名在已加载程序集与 `references` 中查找（`System.Object`、`clojure.lang.AFunction`、`Cleat.Scheme.Procedure`）；`"T, 程序集名"` 指定程序集；后缀 `[]` 一维数组、`&` 按引用（只用于参数）；`System.Void` 只用于返回类型；泛型类型名写 `"Name<A,B>"`（互操作指令的类型操作数经所属运行时的 `TypeResolver` 解析；§5）。

方法引用：`"T" "名字" ("P" …) "R"`，返回类型用于核对；构造函数 `"T" ("P" …)`（`newobj`）。字段引用：`"T" "名字"`。

## §5 指令

**普通 CIL 指令**：`System.Reflection.Emit.OpCodes` 中的全部指令按名字书写（`(add)` `(ldc.i4 3)` `(ldstr "s")` `(ldarg 1)` `(ldloc 2)` `(br L)` `(castclass "T")` `(ldfld "T" "f")` `(call "T" "m" ("P"…) "R")` `(newobj "T" ("P"…))` `(switch (L1 L2 …))`）。短格式不必写；发射器自动选择。`(tail)` 是 `tail.` 前缀，须紧接 `call`/`callvirt` 且其后为 `ret`。

**伪指令**：

| 伪指令 | 含义 |
|---|---|
| `(label L)` | 定义标签，方法内唯一 |
| `(ldconst k)` | 载入常量表第 k 项（`ldsfld $Constants::c; ldc.i4 k; ldelem.ref`） |
| `(try (body …) (catch "T" …)… (finally …)?)` | 结构化异常；至少一个 catch 或 finally；catch 入口栈上是异常对象；各块末尾的 `leave`/`endfinally` 由发射器加入；进入 try 时栈须为空 |
| `(line l1 c1 l2 c2)` | 序列点，需方法有 `source` |

**互操作指令**（参数先按 `object` 压栈；决议规则见 designs/05 §5；IronScheme 04 §5）：

| 指令 | 栈 |
|---|---|
| `(clr-call T 成员 static? argc [("A"…)])` | 实例? 参数×argc → 结果。`T` 为 `#f` 表示按实例运行时类型；成员为 `"名字"`、`("名字" ("P"…))` 或 `("名字" ("P"…)|#f ("G"…))`；可选 `("A"…)` 为实参类型提示 |
| `(clr-field-get T "f" static?)` / `(clr-field-set T "f" static?)` | 实例? → 值 / 实例? 值 → nil（scheme：未指定值） |
| `(clr-new T argc [("A"…)])` / `(clr-new-array T)` | 参数×argc → 对象 / 长度 → 数组 |
| `(clr-cast T)` `(clr-is T)` `(clr-type T)` | 值 → 值 / 布尔对象 / `System.Type` |

**[cleat] 两个运行时**（方法的 `runtime`，缺省程序集的，再缺省 `clojure`）决定互操作指令的类型解析、运行时决议与值转换：

| | `clojure` | `scheme` |
|---|---|---|
| 类型名 | `Cleat.Core.Runtime.TypeResolver`（ClojureCLR 类型名语法、`clr-namespaces`） | `Cleat.Scheme.TypeResolver`（IronScheme 别名表；`Name<A,B>` 转成符号说明） |
| 运行时决议与转换 | `Cleat.Core.Runtime.Reflector`（`Call` / `GetField` / `SetField` / `New` / `NewArray` / `Cast` / `Is` / `Convert<T>` / `ConvertFrom`） | `Cleat.Scheme.Interop`（同名方法） |
| 返回值 | `void` → `nil`；`long` / `double` / `bool` / `char` 只装箱；其余值类型与 `object` 按 `Reflector.ConvertFrom`（`int` → `long` 等） | `void` → `Unspecified.Instance`；`string` 经 `SchemeString.Normalize`（含代理码元的转宽串）；值类型装箱后按 `Interop.ConvertFrom`（`long` → fixnum / bignum 等） |

## §6 校验（读取时报错，带行列）

未知指令或操作数个数 / 类型不对；标签重复或未定义（含 `switch` 表）；参数、局部变量下标越界；`ldconst` 越界或常量下标不连续；`(tail)` 后不是调用或调用后不是 `ret`；`(line …)` 无 `source`；`implements` 的接口不在 `interfaces` 中；接口类型含实例字段或非抽象方法；`ctor`/`cctor` 名字不符；重复的类型、字段、方法；`runtime` 不是 `scheme` / `clojure`；类型或成员找不到（发射时报错）。

## §7 程序集约定

入口 `(entry …)` 须是 `public static object M()`；发射器写程序集属性 `Cleat.Core.CleatModuleAttribute("类型", "方法")`，加载器（`Cleat.Core.Runtime.Loader`、`Cleat.Scheme.ModuleLoader`）据此调用（designs/05 §6）。

## §8 常量编码（[cleat] 联合编码，designs/15 §1）

`(constants …)` 的每项是中立编码，`Cleat.Emit.FaslEncoder` 写成二进制 fasl（magic `CLFASL`，版本 1，Deflate 压缩后作为 `$Constants` 的初始化数据），运行时 `Cleat.Scheme.Fasl.ReadPackedTable` 读回。标签字节两套互不重叠，一张表里可以混用：

| 来源 | 编码 | 读回 |
|---|---|---|
| 两边共用 | `(nil)` `(true)` `(false)` `(def n x)` `(ref n)` | `null`、装箱 bool、共享与环 |
| IronScheme | `(unspecified)` `(eof)` `(fix n)` `(big "十进制")` `(rat "n/d")` `(flo "位模式")` `(cpx 实 虚)` `(chr 码点)` `(mstr "…")` `(gensym "…")` `(pair a d)` `(dlist e … 表尾)` `(bv "十六进制")` `(rtd …)` `(rec rtd f …)` `(ann …)` `(eqht k v …)` `(cnd …)` `(rcd rtd 父)` | Scheme 值（IronScheme 04 §3） |
| cleat | `(long n)` `(double "位模式")` `(bigint "…")` `(bigdec "…")` `(ratio "n/d")` `(char 码元)` `(kw "name")` / `(kw "ns" "name")` `(var "ns" "name")` `(map k v …)` `(set e …)` `(sorted-map k v …)` `(sorted-set e …)` `(queue e …)` `(meta m x)` `(regex "…")` `(inst "…")` `(uuid "…")` `(type "全名")` `(record "全名" k v …)` | clojure.lang 值（designs/05 §3） |
| **按 runtime 解释** | `(str "…")` `(sym …)` `(list e …)` `(vec e …)` | `scheme`：`string`（含 BMP 外字符为宽串）/ `Cleat.Scheme.Symbol`（一个名字）/ `Cons` 表 / `object[]`；`clojure`：`string` / `clojure.lang.Symbol`（`(sym "name")` 或 `(sym "ns" "name")`）/ `PersistentList` / `PersistentVector` |
| cleat（无歧义，cleat-051） | `(cstr "…")` `(csym "name")` / `(csym "ns" "name")` `(clist e …)` `(cvec e …)` | 总是 Clojure 的 `string` / `clojure.lang.Symbol` / `PersistentList` / `PersistentVector`，与程序集的 runtime 无关：Scheme 单元（Scheme 编译器的产物都是 `(runtime scheme)`）里的 Clojure 常量用它们 |

常量表所属的运行时是程序集的 `runtime`（方法级的 `runtime` 只影响互操作指令）。
