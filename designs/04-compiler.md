# 04 nanopass 编译器 `cleat.compiler`

## 1. 位置与原则

| 项 | 决定 |
|---|---|
| 源码 | `src/cleat/compiler/`：`languages.clj`（L0 ~ L3）、`parse.clj`（parse-core）、`analyze.clj`、`passes.clj`、`closures.clj`、`codegen_cil.clj`、`codegen_llvm.clj`、`lcil.clj`、`llvm_ir.clj`、`constants.clj`、`targets.edn`、`driver.clj`（`cleat.compiler`） |
| 进入 boot 镜像 | 是：`eval` 与 `load` 要用编译器 |
| 纯函数原则（00 D5） | 输入只有 Lcore、`primitives.edn`、`targets.edn`、接口文件、编译选项；不读 Var、不运行目标代码、不反射。展开器已把需要运行时的信息做成数据（03 §2） |
| 框架 | `cleat.nanopass`（07） |

## 2. 中间语言

| 语言 | 相对上一层的变化 |
|---|---|
| L0 | parse-core 输出：Lcore 的记录形式。`(local x)`、`(var-ref v)`、`(prim p)`/`(primcall p e …)`（Var 决议为原语表项的调用）；`(fn name (clause (x …) rest body) …)`；`(call e e …)`；`host-*` 节点；`case`、`try`、`deftype` 等原样 |
| L1 | `letfn` 只绑定 `fn`；被 `set!` 的词法变量标记；`loop`/`recur` 变为 `(label l (x …) init body)` / `(jump l e …)`；`fn` 子句内的 `recur` 同样（函数级循环） |
| L2 | 既被赋值又被捕获的变量改为 `box` / `unbox` / `set-box!`（`deftype` 可变字段除外，它们是宿主字段） |
| L3 | 每个 `fn` 带自由变量表与"已知调用"标记（本单元内对非 dynamic、非 redef Var 的 `fn` 的调用）；`deftype` 方法体同样带自由变量表（应为空，`this` 与字段是宿主成员）；每个 `fn` 带唯一类名 |

L3 之后分目标（§4、[05](05-cil-backend.md)、[06](06-llvm-backend.md)）。

## 3. pass 链

| # | pass | 输入 → 输出 | 做什么 |
|---|---|---|---|
| 1 | `parse-core` | Lcore → L0 | 固定文法解析；`(var-ref v)` 若 `v` 在原语表中且在调用位置 → `primcall`；`(call (var-ref v) …)` 保持 `call` 并记 `v`；常量进常量表（按 `=` 与类型去重；`-0.0` 不去重）；源位置附在节点 |
| 2 | `recognize-loops` | L0 → L1 | `loop`/函数级 `recur` → `label`/`jump`；`letfn` 规范化；标记被赋值变量 |
| 3 | `cp0-lite` | L1 → L1 | `if` 常量测试、`do` 中的常量、纯原语的常量折叠（只对原语表 `:pure` 项）、单次使用 `let` 内联。第一版可为空 pass |
| 4 | `convert-assignments` | L1 → L2 | 被赋值且被捕获 → box |
| 5 | `uncover-free` | L2 → L3 | 自由变量；已知调用标记；类名分配（`ns$name__N`，N 按单元内定义顺序，确定） |
| 6 | `codegen-cil` | L3 → Lcil | 05 |
| 6' | `codegen-llvm` | L3 → LLVM IR | 06 |

与 IronScheme 的 15 步相比合并了很多：Clojure 没有 `letrec` 的 SCC 问题（`letfn` 语义就是"先建后填"），没有 `case-lambda` 规范化（`fn*` 子句已规范），没有尾调用判定。闭包转换与类提升合在 codegen 里（IronScheme 第一版同样如此）。

## 4. 机器描述表 `targets.edn`

```clojure
{:clr  {:object "System.Object" :fn-base "clojure.lang.AFunction" :restfn-base "clojure.lang.RestFn"
        :var "clojure.lang.Var" :keyword "clojure.lang.Keyword" :rt "clojure.lang.RT" :numbers "clojure.lang.Numbers"
        :max-positional-arity 20 :boxed-long "System.Int64" :boxed-double "System.Double"
        :host-call :cil-call :host-new :cil-new :host-field :cil-field …}
 :llvm {:ptr "ptr" :fn-create "cleat_fn_create" :closure-create "cleat_closure_create" :call-prefix "cleat_call"
        :max-positional-arity 10 :var-intern "cleat_var_intern" …}}
```

codegen 只经表项取类名、原语符号、互操作指令名；驱动的 `*compile-target*`（缺省 `:clr`）选表项并同步给运行时（reader 条件的 `:cleat-clr` / `:cleat-llvm` 由它决定）。

## 5. 原语的编译

原语表 `src/boot/primitives.edn`（02 §5）。每项按目标有 `:type`+`:method`（clr）或 `:symbol`（llvm），标志 `:pure`、`:inline`（codegen 有内联模板）、`:unchecked`。

| 种类 | CIL | LLVM |
|---|---|---|
| 内联模板 | `first`/`next`/`nth`/`count`/`get` 的接口快速路径（`isinst ISeq` → `callvirt`，否则 `call RT.first`）；`=`/`<`/`+` 等对 `long`/`double` 的拆箱算术（`isinst Int64` → `unbox` → `add.ovf` → `box`，溢出抛）；`nil?`/`identical?`/`instance?` | `cleat_first` 等 C 调用；数值内联按 06 §4 的表示 |
| 静态调用 | `call` C# 静态方法 | `call @cleat_xxx` |
| 作为值 | 读同名 Var 的根 | 同 |

与 Clojure 的 `Intrinsics.cs`（把 `Numbers.add` 等映射到裸指令）对应；第一版只做 `Intrinsics.cs` 已有的那组。

## 6. 调用与直接链接

| 调用形态 | 编译结果 |
|---|---|
| `(call (var-ref v) args…)`，`v` 是本单元已知 `fn`，参数个数匹配某固定 arity | `call 类::invokeStatic`（CIL）/ `call @类_arityN`（LLVM） |
| 同上，`v` 在接口文件中（boot 镜像或已加载 `.clj.dll`） | 跨单元直接 `call`（类名带程序集），单元入口先核对镜像 id；镜像重建后旧产物报错并回退源码（IronScheme iron-034e 的做法） |
| `v` 是 `^:dynamic` / `^:redef` / 非 `fn` / 被 `set!` / 参数个数不匹配 | 取 Var 根 → `callvirt IFn::invoke(k)` |
| 参数 > 20 | `invoke(20 + params)` |
| `(keyword-invoke k e)` | `callvirt ILookup::valAt` 快速路径，否则 `RT.get`（与 Clojure 的 `KeywordLookupSite` 等价但不生成站点类） |
| 协议方法 | 普通 Var 调用（`core_deftype.clj` 的分派函数）；后续做内联缓存 |
| `apply` | `applyTo` |

## 7. 宿主互操作节点

`host-call T member e args {tag…}` 等在 L0 ~ L3 保持符号化（类型是字符串、成员是名字、实参类型提示是展开器给的 `(tag …)`）。codegen-cil 翻成 Lcil 的 `(clr-call T member static? argc (hints…))` 等，由发射器决议（05 §5）；决议不了的发射为 `Reflector` 调用。codegen-llvm 对 `host-*` 节点：只支持原语表 `llvm` 列中有对应 C 符号的（即"宿主"就是 `cleat-rt` 的 C API），其他报编译错误"LLVM 目标不支持 CLR 互操作"。

## 8. `deftype*` / `reify*`

- L3 的 `deftype` 节点：类名、字段（含 `^:mutable`/`^:volatile-mutable`、类型提示）、接口列表、方法（接口 + 名字 + 参数类型 + 返回类型 + 体）。`reify*` 是带自由变量（构造函数参数）的匿名 `deftype`。
- codegen-cil → Lcil `(type …)`，含 `interfaces`、公开字段、`override` 方法、`IType`/`IRecord` 标记；`defrecord` 的额外方法由宏生成（与 Clojure 相同）。
- codegen-llvm → 06 §4 的结构体 + 方法表。

## 9. 编译驱动

| 入口 | 行为 |
|---|---|
| `(cleat.compiler/compile-form lcore opts)` | Lcore → Lcil S 表达式（不经文本）→ `Cleat.Emit` 内存模式 → 返回无参 `IFn`；`eval` 与 `load` 用它 |
| `(compile 'ns)` / `(compile-file path)` | 展开整个文件（逐形式；`def` 的宏要立即生效，所以**展开—编译—执行**逐形式交替，与 Clojure 相同），收集所有单元的 Lcil → 一个 `.clj.dll`（持久化模式）+ 接口文件；`*compile-files*` 语义同 Clojure |
| 构建脚本 `src/boot/build-boot.clj` | 按 03 §5 的顺序编译全部 boot 命名空间 → `cleat.boot.dll`；写出 Lcil 文本用于比较不动点 |
| `--target llvm` | 同样的驱动，产物是 `.ll`（06 §6） |

**REPL / `eval` 的简单形式不编译**（借 Chez 的 `compile-interpret-simple`，`s/compile.ss:1820`：Chez 的 `current-eval` 缺省就是 `compile`，每个顶层形式跑完整的 nanopass 管线生成机器码，但不创建过程的简单表达式改走解释器）：`compile-form` 对不含 `fn` / `deftype` 的 Lcore（常量、Var 引用、直接调用、`def` 常量）不生成程序集，直接解释求值——内存模式每次 `Reflection.Emit` 几百微秒且类型数有平方级开销（cleat-003）。解释器在方案 A 是 stage0，在方案 C 是 IronScheme 的 `compile-core`，在方案 B 要单写一个小的。

## 10. 第一版不做、后续做

- 类型推断与拆箱（`^long` 局部、原始类型 `IFn.LL` 签名）；局部变量清除；协议调用内联缓存；`:inline` 之外的内联；跨单元直接链接到用户 `.clj.dll`（第一版只对 boot 镜像）。
- 通用尾调用（00 D14）：如需，CIL 可发 `tail.`（IronScheme 已验证可行），LLVM 可在同签名时用 `musttail`；在 `targets.edn` 留 `:tail-calls?` 开关。

## 11. 验证

- 每个 pass 后 `unparse` 快照（`tests/compiler/snapshots/*.l0 … .l3`），输入取 `core.clj` 展开后的真实 Lcore。
- Lcil / `.ll` 文本可 diff。
- nanopass 文法检查节点不变量；应用产生式带关键字（`call`），防止吞掉同形形式（IronScheme 03 §9）。
