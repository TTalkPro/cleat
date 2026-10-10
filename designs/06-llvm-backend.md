# 06 LLVM IR 后端

> 原则来自 IronScheme 11 §2："每个目标 = 一份机器描述 + 一个发射器 + 一套满足原语表契约的运行时"，自举靠交叉编译。jank 的经验（01 §3）：直接写 LLVM IR 的两个痛点是"IR 太低级做不了 Clojure 级优化"与"异常落地垫难写"。前者由 nanopass 前端（L0 ~ L3 在进入后端前已完成闭包转换、循环识别、box、已知调用）吸收；后者是 M0 验证的重点（§5）。

## §0 事实

- 本机：clang / LLVM 21.1.8，`lli`、`opt`、`llc` 可用。
- jank 旧 LLVM 后端的 ABI（`c_api.h`）：全部值为不透明 `ptr`；每个 arity 是 `ptr(ptr self, ptr …)`，最多 10 个固定参数；函数对象 `jank_function_create(flags)` + `set_arityN(fnptr)`；闭包 `jank_closure_create(flags, ctx)`，每函数一个 `ctx` 结构体，各 arity 共享；常量在 `jank_global_init` 构造函数里初始化；Var 的根在 `jank_load_<ns>` 里 `bind_root`；`jank_throw` 抛 C++ 异常，生成代码手写 landing pad；Boehm GC。
- jolt `TARGET-CONTRACT.md`：移植运行时必须自己钉死的语义（动态绑定的线程继承、锁的可重入性、一次性逃逸与 `finally` 的交互）。
- ClojureScript：全部持久集合用 `deftype` + 协议在 Clojure 里实现，运行时内核极小。

## §1 决策

**D1 第一版只做 AOT，由 .NET 宿主交叉编译**。`cleat --target llvm compile ns…` 在 .NET 上展开（宏在 .NET 上运行）、编译、输出 `.ll`，`clang` 编译并链接 `libcleat-rt.a` 得到可执行文件或 `.so`。原生侧没有 `eval`、没有 reader（`read-string` 后续由 C 实现或由 Clojure 写的 reader 提供）。不动点判据改为"原生运行的程序与 .NET 运行的程序对语料给出相同结果"，而不是"原生系统编译自己"（后者需要原生侧有展开器与编译器——即 jank 的全部工作量，不在本设计范围内）。

**D2 原生运行时 `cleat-rt` 用 C 写，极小**（`native/cleat-rt/`，Apache-2.0）：

| 部分 | 内容 |
|---|---|
| 对象与 GC | `cleat_obj` 头：类型标签（u32）+ 哈希缓存；Boehm GC（`gc.h`，`GC_MALLOC`）；后续可换 |
| 标量 | `nil`/`true`/`false` 单例；`long` 装箱（小整数缓存 -128 ~ 1023）；`double` 装箱；bigint（第一版用 libgmp 或自写有限实现，M8 决定）；ratio；char；字符串（UTF-8 存储 + 码点索引缓存；语义按 Clojure 的 UTF-16 索引**不保证**，记入偏离表） |
| 名字 | `symbol`、`keyword`（驻留表）、`var`（根、dynamic 标志、线程绑定栈：`pthread` TLS）、`namespace`（仅名字 → var 表） |
| 函数 | `cleat_fn`：`arity_flags` + `fnptr[0..10]` + `ctx`；`cleat_callN`（分派、变长打包为数组序列、arity 错误）；`cleat_apply` |
| 控制 | `cleat_throw`、异常对象、`try` 的落地垫（§5）；`cleat_monitor_enter/exit`（`pthread_mutex` 可重入） |
| 数值 | `cleat_num_add/sub/mul/quot/rem/lt/…`（long 溢出检查、double、bigint 升级）、比较与哈希（与 CLR 侧**相同的哈希算法**：Murmur3 + `hash-ordered-coll` / `hash-unordered-coll`，否则语料中与哈希顺序有关的用例会分歧） |
| 原始数组 | `object[]`、`long[]`、`double[]`、`char[]`、`byte[]`；`aget`/`aset`/`alength`/`aclone` |
| IO | `*out*`/`*err*`/`*in*` 的最小端口；`print`/`read-line`；文件读写（`slurp`/`spit`） |
| 类型系统 | `cleat_type`：名字、字段表、协议表（§4）；`instance?` 按标签 / 类型指针 |
| 入口 | `cleat_rt_init`、`cleat_load_<ns>` 登记、`main` 生成 |

规模目标 5000 ~ 8000 行 C。**不包含**持久集合、惰性序列、STM、agent、多方法、打印、字符串库——这些用 Clojure 写（D3）。

**D3 持久集合与大部分 core 在原生目标上用 Clojure 写**（`native/core/`）。参照 ClojureScript 的 `core.cljs`：`PersistentVector`、`PersistentArrayMap`、`PersistentHashMap`（HAMT）、`PersistentHashSet`、`PersistentTreeMap`、`List`、`Cons`、`LazySeq`、`ChunkedSeq`、`Range`、transient、`Atom`、`Volatile`、`Delay`、`MultiFn`、打印……全部 `deftype` + 协议（`ISeq`、`ISeqable`、`ICounted`、`IIndexed`、`ILookup`、`IAssociative`、`IMap`、`ISet`、`IStack`、`IReduce`、`IKVReduce`、`IFn`、`IDeref`、`IMeta`、`IWithMeta`、`IEquiv`、`IHash`、`IComparable`、`IPrintWithWriter`…）。`clojure.core` 主体（`src/clojure/core.clj`）两个目标共用，差异用 `#?(:cleat-llvm …)`；CLR 目标上集合在 C# 里（02），LLVM 目标上 `cleat.kernel` 的原语指向 `cleat-rt` 的 C 符号，集合的 Var 指向 `native/core/collections.clj` 的实现。

**D4 原生对象模型与调用约定**：

| 项 | 决定 |
|---|---|
| 值 | 统一 `ptr`（指向 `cleat_obj`）。第一版**不做 NaN-boxing / 立即数**；留给后续（jank 做了，收益明确） |
| 函数值 | `cleat_fn` 对象；生成的每个 arity 是 `define ptr @ns$name__N_arityK(ptr %self, ptr %a1, …)`；`%self` 是函数对象，捕获变量从 `self->ctx` 读（`ctx` 为生成的 `%ns$name__N.ctx = type { ptr, ptr, … }`） |
| 调用已知函数 | 直接 `call @…_arityK`（04 §6 的直接链接），`%self` 传函数对象常量或 `null`（无捕获时不读 ctx） |
| 调用未知函数 | `call ptr @cleat_callK(ptr %f, …)`；K > 10 走 `cleat_calln(ptr, i32, ptr*)` |
| `if` | `call i1 @cleat_truthy(ptr)` 内联为"`%v != @cleat_nil && %v != @cleat_false`"（两个全局单例地址比较） |
| `let` / `label` / `jump` | SSA：`alloca` + `mem2reg`（由 `opt -O1` 完成），不自己算 phi；`jump` = 存参数 + `br` |
| `box` | `cleat_box` 对象（单字段） |
| Var | `@ns/name.var` 全局，`cleat_load_<ns>` 开头 `cleat_var_intern`；非 dynamic 读 `load ptr, ptr (var->root)`（内联），dynamic 调 `cleat_var_deref` |
| 常量 | 05 §3 的同一编码 → `cleat_load_<ns>` 开头由 `cleat_fasl_read(@ns.constants_blob, len)` 一次性重建为 `@ns.constants` 数组；代码里 `load ptr, ptr getelementptr(@ns.constants, k)` |
| `deftype` | `%ns.Name = type { %cleat_obj, 字段… }`；`@ns.Name.type` 描述符（名字、字段、协议实现表）；方法为普通函数 `@ns.Name.method_K(ptr %this, …)`；可变字段 `store` |
| 协议 | 每个协议一个描述符；`cleat_type` 的协议表：协议 id → 方法指针数组；调用 `(proto-fn x …)` 经 `cleat_proto_lookup(type, proto_id, method_idx)` 得函数指针后 `call`（后续内联缓存）；`extend-type` 向类型的协议表追加；原生类型（数、字符串、nil、fn）各有一个 `cleat_type` |
| `case` | `switch i64`（int）/ 哈希后 `switch`；与 CIL 相同的预处理数据 |
| `try` | §5 |
| `throw` | `call void @cleat_throw(ptr)`，`noreturn` |
| `monitor-*` | C 调用 |
| 模块入口 | `define void @cleat_load_<ns>()`：登记 Var、重建常量、按序执行顶层形式（每个一个 `@ns.init_k`）；全局构造函数不用（jank 的三种策略里选"入口内按序"，最可预测） |
| `main` | 驱动生成：`cleat_rt_init(argc, argv)` → `cleat_load_clojure_core()` … → 调 `-main` |

**D5 机器描述表 `targets.edn` 的 `:llvm` 项**给出：C 符号前缀、`max-positional-arity 10`、类型描述符命名、是否 `musttail`、数据布局字符串与 triple（第一版只 `x86_64-unknown-linux-gnu`，由驱动参数覆盖）。

**D6 原语表的 `llvm` 列**由 `native/cleat-rt/primitives.h` 中的标注注释生成（`// @primitive clojure.core/+ arity=2 pure`），PrimGen 把两列合并，名字或 arity 对不上时构建失败。这是 02 §5 "原语表是宿主契约" 在第二个宿主上的体现。

**D7 不做的（记入偏离表）**：`eval` / `read-string` / 宏（原生）、CLR 互操作（`host-*` 节点在 `:llvm` 目标编译错误，04 §7）、`proxy` / `gen-class` / `definterface`、STM（`ref`）、agent、`future`（第一版同步执行）、正则（第一版用 PCRE2 或不支持，M8 决定）、BigDecimal、UTF-16 索引语义、反射（`clojure.reflect`）。

## §2 管线

```
.NET 宿主（stage1 之后的任何 cleat）
  展开器（宏在 .NET 上跑，reader 条件 :cleat-llvm）→ Lcore
  cleat.compiler：parse-core → … → L3 → codegen-llvm → .ll 文本（每命名空间一个）
  clang -O1 -c ns.ll → ns.o ；clang main.ll …o libcleat-rt.a -lgc -o app
```

- 交叉编译纪律：编译器纯函数（04 §1）；原语信息只来自 `primitives.edn` 的 `llvm` 列；常量只经中立编码；**展开期执行的宏看到的是 .NET 上的 core**，所以宏不得依赖宿主专有行为（与 ClojureScript 的 `clj` 宏 / `cljs` 运行时分离完全相同的纪律）。
- 原生 core 的构建：`native/core/*.clj` + `src/clojure/core.clj`（`:cleat-llvm` 分支）→ `libcleat-core.a`，一次性生成后缓存。

## §3 与 CIL 后端的共享

| 共享 | 不共享 |
|---|---|
| L0 ~ L3 全部 pass；常量编码；原语表格式；`case` 预处理；`deftype` 节点；接口文件格式 | codegen；运行时；集合实现（C# vs Clojure）；异常机制；互操作 |

## §4 性能（后续，不在第一版）

NaN-boxing 或小整数立即数；`musttail` 对同签名调用；协议内联缓存；`hoist-var-derefs`（jank 的 IR 优化）；`fastcc`；LTO。

## §5 M0 验证项（任务 cleat-004）

1. **异常**：手写 `.ll` 用 `invoke` / `landingpad` + `__gxx_personality_v0`（运行时用 C++ 的 `throw` 实现 `cleat_throw`，`catch` 匹配用 `cleat_exception_matches`）与 `setjmp`/`longjmp` 两种方案各做一个"try/catch/finally + 跨两层函数抛出 + finally 中再抛"的用例，比较生成难度、`-O1` 下的正确性、性能。**决定采用哪种**。倾向：Itanium EH（可与 C++ 库互操作，不破坏 SSA 寄存器状态），jank 的旧 `generate_catch_dispatch` 是参照。
2. **调用约定**：`ptr(ptr self, ptr…)` 11 个固定 arity + `calln`；`musttail` 在同签名下深度一千万层不溢出。
3. **GC**：Boehm 下生成代码的 `alloca` 与全局指针可被扫描；`GC_MALLOC` 分配 `ctx`；多线程 `GC_THREADS`。
4. **常量 fasl**：C 读回 05 §3 编码（先只做标量、符号、关键字、列表、向量）。
5. **工具链**：从 .NET 调用 `clang`（`Process`），错误回传；`opt -O1` 的 `mem2reg` 把 `alloca` 转成 SSA。
6. 结论写回本文 §1 D4 与任务文件。
