# 01 蓝本分析

> 2026-10-10 对五个蓝本的实地阅读。每节先列事实，再列"取什么、不取什么"。路径相对各仓库根目录。

## §1 IronScheme（`~/workspace/IronScheme`，nanopass 分支）

**事实**
- 结构：`src/IronScheme.Core`（C# 运行时）、`IronScheme.Emit`（Lcil → Reflection.Emit）、`IronScheme.Stage0`（约 1000 行 C# 核心形式解释器）、`IronScheme.Host`、`IronScheme.PrimGen`；Scheme 侧 `src/scheme/boot/`（进入 boot 镜像的库、展开器、原语表、种子）、`lib/cleat/scheme/compiler/`（nanopass 编译器，约 3500 行）。
- 自举：旧系统一次性生成文本种子 `boot-seed.core`（boot 源码展开后的核心形式）→ stage0 解释种子得到展开器 → 从源码展开并解释运行 nanopass 与编译器 → 编译全部 boot 源码为 Lcil → `IronScheme.Emit` 汇编为 `ironscheme.boot.dll`（stage1）→ 新系统编译自己到 stage2、stage3，Lcil 文本与 dll 逐字节相同。
- 关键决策（`designs/00-overview.md` D1 ~ D15）：新运行时与旧并存；每个 lambda 一个 `Procedure` 子类，`Invoke0..4`/`InvokeN`；符号值槽；编译器输入固定文法、纯函数；输出文本 Lcil；常量中立编码 + fasl；原语表是数据文件；重载决议在发射器；`tail.` 前缀；逃逸续延；记录不生成 CLR 类型；手写 reader；运行时钩子；stage0 是精简解释器。
- 可移植性（`designs/11-portability.md`）：互操作三层（宿主绑定 / 可移植包装 / 核心只用原语）；"把托管运行时当作目标机器"：L0 ~ L3 宿主中性，codegen 按 `target` 查机器描述表；另一个宿主靠交叉编译自举，不写第二个 stage0。
- 确定性（`designs/05-bootstrap.md` §7，iron-030）：gensym 规范改名；`Symbol.GetHashCode` 用名字的 FNV-1a；哈希表遍历按插入顺序或排序；MVID 与时间戳按内容哈希回填。
- 测试分层（`designs/06`）：运行时单测 → stage0 → 发射器 → pass 快照 → 单库 → 自举 → 符合性。
- 文档约定：`designs/NN-*.md` 按主题，决策编号 D<n>，`tasks/iron-NNN-*.md` 含目标 / 工作内容 / 验收标准 / 结论，`TASK.md` 只做索引。

**取**：整套工程方法（设计 / 任务 / 里程碑 / 不动点判据）；Lcil 与发射器的设计（[05](05-cil-backend.md) 直接继承）；常量编码与 fasl；原语表与 PrimGen；stage0 的"闭包树解释器"做法；确定性措施；运行时钩子；可移植性分层与"机器描述表"。
**不取**：psyntax 式展开器与文本种子（Clojure 不需要，00 D4）；符号值槽（Clojure 用 Var，语义不同：动态绑定、元数据、`^:dynamic`）；`Invoke0..4` 的上限（Clojure 约定是 20）；`tail.`（00 D14）；逃逸续延（Clojure 没有 call/cc）；记录的统一表示（Clojure 的 `deftype` 就是要生成真实类型）。

## §2 ClojureCLR（`~/workspace/clojure-clr`，master，1.13.0-SNAPSHOT）

**事实**
- 规模：运行时 `Clojure/Clojure/Lib/` 143 个文件约 5.7 万行；编译器 `CljCompiler/` 77 个文件约 1.8 万行（`Compiler.cs` 2600 行，`Ast/` 62 个 Expr 类约 1.2 万行）；`Runtime/`（Reflector、DLR 绑定器）约 4700 行。
- 目标框架 `net6.0;net8.0;net9.0;net10.0;net11.0;net462;net481`；依赖 DLR 1.3.5、spec、tools.reader。
- 编译器是 JVM `Compiler.java` 的直译：分析与发射合在 Expr 对象里，分析状态放在动态 Var 中；特殊形式表 `def loop* recur if case* let* letfn* do quote var import* . set! deftype* reify* try throw monitor-enter monitor-exit new parse-eval*`（net11 加 `await*`）；发射用 Reflection.Emit；静态决议不了的互操作走 DLR `CallSite`，运行时 `Reflector.CallMethod` 每次调用编译一个 DLR lambda。
- fn → 类：按参数个数一个 `FnMethod`，基类 `AFunction` / `RestFn`，`MaxPositionalArity = 20`；无自由变量时生成静态 `invokeStatic`（direct linking 的目标）；原始类型签名 `IFn.LL`、`IFn.OD` 等；闭包字段由构造函数填；`letfn` 先建对象再 `stfld` 回填；`loop*` 标签 + `recur` 跳转；`case*` 由宏预哈希后发 `switch`；`deftype*` / `reify*` 生成实现接口的真实类型；协议用 `MethodImplCache`。
- 加载：`RT.load` 先找 `.clj.dll`（net9+ 用 `PersistedAssemblyBuilder` 保存），否则从源码（嵌入资源 `Clojure.Source.dll`）编译；`core.clj` 8484 行，末尾按序加载 `core_clr`、`core_proxy`、`core_print`、`genclass`、`core_deftype`、`core/protocols`、`gvec`、`instant`、`uuid`；CLR 差异 384 行带 `;;;` 原 JVM 代码注释；`core.clj` 直接调用编译器内部 `HostExpr/MaybeType`。
- 与编译器无耦合、可直接提取：`Var`、`Namespace`、`Keyword`、`Symbol`、`Numbers`、`BigInt/BigDecimal/Ratio`、全部 `Persistent*`/`Transient*`/`ASeq`/`LazySeq`、`Atom`、`Ref`/`LockingTransaction`、`Agent`、`MultiFn`、`MethodImplCache`、`Murmur3`、`EdnReader`。
- 有耦合：`LispReader`（19 处：语法引用与 `#=` 用 `Compiler.CurrentNamespace`、`resolveSymbol`、`IsSpecial`）、`RT`（`load`、`classForName`、编译选项）、`Util.TryPrimTypeToName`、`ArityException.demunge`、`ClrTypeSpec2.maybeSpecialTag`、`Reflector`（DLR）。
- 测试：`Clojure.Tests/clojure/test_clojure/`（JVM 测试套件移植，73 项）、`Csharp.Tests/`（NUnit）；`build.proj -t:Test`。

**取**：§3 列出的无耦合类整体提取（00 D1）；`IFn`/`AFn`/`RestFn` 调用约定（00 D3）；`core.clj` 及随附文件作为 cleat `core.clj` 的底本（CLR 差异已经做好）；`deftype*`/`reify*`/`case*`/`letfn*`/`loop*` 的发射方式作为 codegen-cil 的参照；`test_clojure` 移植；`docs/what-a-load-of-clojure.md` 作为加载器设计的参考。
**不取**：编译器（分析与发射耦合、状态在动态 Var、不纯，无法做第二后端）；DLR；多目标框架；`Clojure.Source.dll` 嵌入资源的加载方式（改为 boot 镜像 + 加载路径）。

## §3 jank（`~/workspace/jank`，HEAD `e1c3fef3b`）

**事实**
- C++ 宿主，Boehm GC，immer 持久集合；`object_ref` 是 NaN-boxing 的指针包装；约 70 个对象类型标签；`clojure.core` 为 `src/jank/clojure/core.jank`（7800 行，455 行用 `cpp/` 互操作）。
- **2026-04 删除了直接生成 LLVM IR 的 `codegen/llvm_processor.cpp`（4398 行）**，改为自己的 SSA IR（`src/cpp/jank/ir/`，指令见 `book/src/dev/ir.md`：`literal`、`persistent-*`、`function`/`closure`/`capture`、`var-deref`、`branch`/`branch-set`/`branch-get`（upsilon/phi）、`loop`、`case`、`try`/`catch`/`finally`、`truthy`、`type-erase`）→ 降到 C++ → Clang → ORC JIT。理由：LLVM IR 太低级，Clojure 级优化（Var 解引用提升、闭包、transient）做不了；异常落地垫（landing pad）手写太痛苦。旧发射器仍在历史中（`git show 7b9cb983b^:…/llvm_processor.cpp`）。
- 旧 LLVM 后端的 ABI 是 **C API**（`include/cpp/jank/c_api.h`，仍存在）：`jank_call0..N`、`jank_var_intern`、`jank_var_bind_root`、`jank_deref`、`jank_keyword_intern`、`jank_*_create`、`jank_function_create`/`set_arityN`、`jank_closure_create(flags, ctx)`、`jank_truthy`、`jank_throw`、`jank_box`/`unbox`；每个函数 arity 是 `ptr(ptr self, ptr …)`；最多 10 个固定参数；闭包为每函数一个 `ctx` 结构体，各 arity 共享。
- 分析器：36 种节点，每节点带 `expression_position`（value / call / statement / tail / type）与 `needs_box`；`local_frame` 记录跨越的 fn 帧以登记捕获；`recur` 不能跨 `try`；`named_recursion` 直接调用。
- 两阶段构建：phase 1 把 `core.jank` 编译成 `.o`，phase 2 链接进二进制并以 `jank_load_clojure_core` 登记；AOT 为每个命名空间生成 `jank_load_<ns>()` 入口与 `main`。
- 不支持：协议、记录、`deftype`、`reify`、`import`、`set!`、`new`、`.`、`&env`。

**取**：C API 的形状作为 `cleat-rt` 原语表 `llvm` 列的参照（[06 §3](06-llvm-backend.md)）；"每函数一个 ctx 结构体、每 arity 一个 C 函数指针"的闭包表示；`jank_load_<ns>` 入口与 `main` 生成；`expression_position` + `needs_box` 的拆箱思路；错误信息风格指南 `doc/error-message-style-guide.md`；`clojure-test-suite`。
**不取**：放弃直接 LLVM IR 的决定本身（用户目标 4 要求 LLVM IR 后端；我们的管线在 L3 之前已做完 Clojure 级优化，LLVM IR 只承担降级，jank 遇到的"IR 太低级"问题由 nanopass 前端吸收）；C++ 运行时（无协议 / deftype，MPL，变化快）；immer。**警示**：EH 落地垫是 LLVM 后端最难的一块，M0 必须先做验证（[06 §5](06-llvm-backend.md)）。

## §4 jolt（`~/workspace/jolt`）

**事实**
- Clojure-on-Chez（另有 Gambit 目标）：Scheme 写的 reader（`host/chez/reader.ss`）与运行时（`values.ss`、`collections.ss`、`rt.ss`、`protocols.ss`…），Clojure 写的分析器（`jolt-core/jolt/analyzer.clj`）、IR（`ir.clj`：带 `:op` 的 map，有 schema 与验证器）、pass（`passes.clj`："nanopass-lite"，单一 IR + 注解）、后端（`backend_scheme.clj` 发射 Scheme 文本）。
- 自举：种子 `host/chez/seed/{prelude,image}.ss` 入库；`bootstrap.ss` 加载种子后重编 `jolt-core`；`remint.sh` 重复直到逐字节不变（最多 8 轮）；`make selfhost` 检查重建结果等于入库种子。
- `clojure.core` **分层**：`00-kernel`（分析器自己要用的函数，最先加载、直接编译）、`00-syntax`（`let`/`loop`/`fn`/`defn`/`ns` 等宏）、`10-seq`、`20/21/22-coll`、`25-sorted`、`30-macros`（**`deftype`/`defrecord`/`defprotocol`/`reify`/`extend-type` 全是宏，展开为运行时调用，不是特殊形式**）、`40-lazy`、`50-io`、`60-gvec`、`90-docs`。
- 宿主契约：分析器与后端只经 `jolt.host`（`form-*`、`resolve-global`、`form-expand-1`）访问宿主，`ctx` 不透明；`host/scheme-adapter/TARGET-CONTRACT.md` 列出移植时必须自己钉死的语义。
- 测试：`test/chez/corpus.edn` 约 5000 条 `{:suite :label :actual :expected :portability}`，**`:expected` 由 JVM Clojure 取证**（`regen-corpus.clj`），`certify.clj` 门禁，`known-divergences.edn` 分类白名单，`profile.edn` 按宿主能力分级；另跑 jank 的 `clojure-test-suite`（`cts.sh`，逐命名空间基线）与 sci 源码压力测试；错误报告黄金文件。
- 闭包 / 开放世界：只对不可重定义的 Var 内联（`^:dynamic`、`^:redef` 例外）。

**取**：`corpus.edn` + 认证流程（[09](09-testing.md) 的主判据）；`core.clj` 分层与 "kernel 先行"（00 D15）；IR schema 验证器的思路（在 nanopass 里天然具备）；"直接链接只对不可重定义 Var" 的规则；`TARGET-CONTRACT.md` 作为 `cleat-rt` 必须钉死的语义清单；错误黄金文件。
**不取**：Scheme 运行时与文本后端；`deftype` 作为宏（Clojure 语义要求 `deftype` 生成宿主类型，CLR 上必须如此；但 **LLVM 目标上可以借鉴**：原生目标的类型就是"带字段的结构体 + 协议表"，06 §4）。

## §5 nanopass-framework-scheme

见 [07 §0](07-nanopass-port.md)。结论：重写约 2000 行 Clojure，语义等价；模板用 `(out …)` + `~`；上下文切换用宏自己遍历；语言注册表取代编译期环境；每产生式一个 `deftype`。

## §6 许可事实

| 来源 | 许可 | 用途 |
|---|---|---|
| cleat | Apache-2.0 | 新代码 |
| ClojureCLR | EPL-1.0（`epl-v10.html`） | C# 运行时类、`core.clj` 等 |
| Clojure（`core.clj` 原始来源） | EPL-1.0 | 同上 |
| jank | MPL-2.0 | 只参考，`clojure-test-suite` 作为测试子模块 |
| jolt | EPL-2.0 | 只参考，`corpus.edn` 作为测试数据（复制时保留来源与 EPL-2.0 说明） |
| nanopass-framework-scheme | MIT | 参照重写 |

决定 00 D11：EPL 代码分区并保留许可；需用户确认。
