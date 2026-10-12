# 15 纳入 Scheme 元层：取舍、更名、可扩展语法对象、简化的异常模型

> 2026-10-10 决定：按 [14](14-three-layer-architecture.md) 方案 C 进行，IronScheme 中有用的代码**直接纳入**本仓库并更名。两条原则：
> 1. 编译器可以用 Scheme 写，但**语法对象必须可扩展**，以便在其上快速加功能；
> 2. **尽量不改 nanopass**；这一层 Scheme 不对外、没有 call/cc 的需求，异常模型简化为 try / catch。
>
> 本文是执行前的分析：§1 拿什么、怎么改名；§2 语法对象的扩展协议；§3 异常模型；§4 为什么 nanopass 可以一行不改；§5 新的里程碑与任务；§6 风险。

## §0 事实（IronScheme `src/`，2026-10-10）

**规模**：C# `IronScheme.Core` 11,579 行、`Emit` 1,996、`Stage0` 1,050；Scheme boot 14,686 行（展开器 6,008）、编译器 3,925、nanopass 4,939；文本种子 1.5 MB；丢弃 `lib/` 49,481 行与 R6RS 宿主。

**控制与异常特性的实际使用**（grep）：

| 特性 | 展开器 | 编译器 | nanopass |
|---|---|---|---|
| `call/cc` | 5（`guard` 宏的实现 2 处、`main.sls` 的 REPL / load 错误捕获 3 处） | 9（全部在 `async.sls`，async 整体丢弃） | **0** |
| `dynamic-wind` | 4 | 7（async） | **0** |
| `with-exception-handler` / `raise-continuable` | 7 / 4（同上两处 + `compat.sls`） | 6 / 1（`compiler.sls:116` 失败时转储，async） | **0** |
| `guard` | 0 | 3 | 4（2 处是模式里的字面量 `guard`，2 处是 `(guard (c [else #f]) …)`） |
| `syntax-violation` / `error` / `assertion-violation` | 47 / 11 / 80 | 3 / 5 / 11 | 93 / 24 / 0 |
| 多值 | 243 | 55 | 174 |
| 参数对象 | 58 | 12 | 3 |

运行时侧：`Control.cs` 的 `call/cc` 是逃逸续延（`EscapeContinuation` 抛 `ContinuationException`，约 60 行）；`with-exception-handler` / `raise` 是 Scheme 实现的处理器栈（`boot/exceptions.sls`），栈本身在 C# `ExceptionHandlers`（`AsyncLocal`，为 async 而设）；`dynamic-wind` 已是 `$try/finally`；`guard` 在 psyntax 里展开为 `call/cc` + `with-exception-handler`（`core.sls:1511-1523`）。

**语法对象的硬编码遍历**（`core.sls`）：`stx` 记录 `(expr mark* subst* ae*)`；对序对 / 向量 / 注解的特判分布在 `add-mark`（478）、`syntax-kind?` / `syntax-vector->list` / `syntax-pair?` / `syntax-vector?` / `syntax-null?`（526 ~ 560）、`strip` / `strip-annotations`（662 ~ 712）、`syntax-dispatch`（2305 ~ 2400，模式匹配器：`match-each` / `match-each+` / `match-each-any` 认 `pair` / `null` / `stx` / `annotation`，向量经模式 `#(vector p)`）、`syntax->datum`（4325）、`do-macro-call` 的输出检查；共约 58 处 `vector?` / `stx-expr` 类判断，130 处 `syntax-match` 使用（模式语言由 `syntax-dispatch` 解释）。

## §1 拿什么、改什么名

| IronScheme | 纳入为 | 改动 |
|---|---|---|
| `src/IronScheme.Core/`（C#，命名空间 `IronScheme.Core`） | `src/Cleat.Scheme/`，命名空间与程序集 `Cleat.Scheme`（Scheme 运行时：`Procedure`、`Symbol`、数值塔、`Record`、条件、端口、`Interop`、fasl、钩子） | 删 async（`AsyncFrame`、`Tasks`、`ExceptionHandlers` 的 AsyncLocal）、逃逸续延（§3）、FFI（`Ffi`，无需求）、调试器钩子；`[Primitive]` 原语保留；`SchemeModuleAttribute` 改用 `Cleat.Core.CleatModuleAttribute` |
| `src/IronScheme.Emit/` | 并入 `src/Cleat.Emit/`（cleat 已移植，含 `implements` / `volatile` / `switch` 扩展） | 常量 fasl 合并为一种编码：以 IronScheme 的编码为基础（它要能表达语法对象、记录、gensym），加入 cleat 的 Clojure 数据标签（关键字、持久集合、Var、正则…），`$Constants` 读回由 `Cleat.Scheme.Fasl` 统一实现；`Cleat.Core.Runtime.Fasl` 退役 |
| `src/IronScheme.Stage0/` | `src/Cleat.Stage0/`（本来就是这个名字，现在是 **Scheme** Lcore 的解释器，不再是 Clojure 的） | 去 `escape-frame` / `call/cc`，加 `$try/catch` |
| `src/IronScheme.PrimGen/` | 并入 `src/Cleat.PrimGen/` | 反射两个程序集（`Cleat.Scheme` 的 `[Primitive]` + `Cleat.Core` 的 `Primitives.Table`）生成一张表（`src/boot/primitives.ss` 供 Scheme 编译器、`primitives.edn` 保留供工具） |
| `src/scheme/boot/`（含 `expander/`、`boot-seed.core`、`primitives.ss`、构建脚本） | `src/scheme/boot/` | 库名 `(ironscheme …)` **第一阶段不改**：它们不对外（第 3 层看不见），改名要同步重生成种子；待自举在本仓库跑通后作为独立任务改为 `(cleat scheme …)` |
| `src/scheme/lib/cleat/scheme/compiler/`、`compiler.sls` | `src/scheme/compiler/` | 去 `async.sls`；`targets.sls` 的运行时类名改为 `Cleat.Scheme.*` |
| `src/scheme/lib/nanopass-framework-scheme/` | `src/scheme/nanopass/`（**原样**，含 `implementation-helpers.ironscheme.sls` 与 IronScheme 的覆盖库） | 不改 |
| `tests/IronScheme.Core.Tests/`、`tests/IronScheme.Tests/` 的运行时、发射器、stage0、自举测试 | `tests/Cleat.Scheme.Tests/` | R6RS 符合性测试（`tests/r6rs/`）不纳入：第 3 层没有 R6RS 表面；保留编译器快照测试与自举不动点测试 |
| `tools/build.cs` 的 `stage1` / `seed` / `ilverify` | 合并进 cleat 的 `tools/build.cs` | — |
| `IronScheme.Host`、`Hosting`、`lib/`（std、srfi、clr 包装、async、debugger、web…）、`examples/`、R6RS 测试 | **不纳入** | — |

**仓库形态**：`src/Cleat.Scheme`（元层运行时）与 `src/Cleat.Core`（Clojure 运行时）并列；`src/scheme/`（元层的 Scheme 源码：boot、compiler、nanopass、将来的 `clojure/`）与 `src/clojure/`（Clojure 源码）并列。许可：IronScheme 的 BSD 代码带原始版权说明放入 `NOTICE`。

**构建**：`dotnet build` 多一步 `GenerateSchemeBootImage`（stage0 执行种子 → 构建脚本 → `cleat.scheme.boot.dll`，IronScheme 实测 45 秒），再 `GenerateBootImage`（Scheme 镜像编译 `core.clj` → `cleat.boot.dll`）。自举不动点分两段，判据沿用 IronScheme iron-030 与 cleat-031。

## §2 可扩展的语法对象（原则 1）

**目标**：在不改 nanopass、少改 psyntax 核心算法的前提下，让语法对象能装 Clojure 的向量 / map / set / 元数据，并且以后再加新的数据种类或新的绑定种类时只需"注册"，不需要再找那 58 处特判。

**协议**：psyntax 里凡是按数据种类分支的地方，改为查一张**语法扩展表**；序对与向量成为表里的前两项，Clojure 的集合是 cleat 注册的后几项。

```scheme
;; (cleat scheme syntax-ext)
(define-record-type syntax-ext
  (fields kind        ; 符号，如 pair vector clj-vector clj-map clj-set
          pred        ; datum → #t/#f（认识裸数据）
          children    ; datum → list（子项；map 是 k v 交替）
          rebuild     ; (datum . new-children) → datum（保留元数据等不参与展开的部分）
          pattern?))  ; 模式语言里能否用它的字面形状做模式
(define syntax-extensions (make-parameter '()))   ; 注册表，启动时装入
```

**改动点**（对应 §0 的硬编码处）：

| 函数 | 现状 | 改为 |
|---|---|---|
| `add-mark` 的 `f` | `pair?` / `vector?` / `stx?` / `symbol?` 分支 | 先查扩展表：`(pred e)` → 对 `children` 递归，`rebuild`；`stx?` / `symbol?` 不变 |
| `syntax-kind?` 一族 | `syntax-pair?` `syntax-vector?` `syntax-null?` | 加通用 `(syntax-ext? x kind)` 与 `(syntax-ext->list x kind)`（把 wrap 下推到子项，即 `syntax-vector->list` 的推广） |
| `strip` / `strip-annotations` | 序对 / 向量 / 注解 | 扩展表 + `rebuild` |
| `syntax-dispatch` | `match-each` 等只认序对；向量经 `#(vector p)` | 模式语言加 `#(ext kind p)`：匹配时 `(syntax-ext->list e kind)` 后按列表模式匹配；`syntax-case` 的模式编译（`convert-pattern`）把裸模式里被 `pred` 认出的字面形状编成 `#(ext kind …)`，向量就是 `kind = vector` 的特例 |
| `syntax->datum` / `datum->stx` | 同 `strip` | 同上；`datum->syntax` 不变（只复制 wrap） |
| `do-macro-call` 的输出检查 | 遍历序对 / 向量找裸符号 | 扩展表 |
| quasisyntax（`syntax` 模板的生成） | 序对 / 向量模板 | 扩展表（模板里的 Clojure 向量要重建为 Clojure 向量） |
| reader 注解 | `annotation` 记录（表达式、源位置、剥离后的表达式） | Clojure 的源位置与元数据：wrap 时把 `:line` / `:column` / `:file` 转成注解；其余元数据留在数据里由 `rebuild` 保留 |

**第二个扩展维度：绑定种类**。`syntax-type` → `chi-expr` 的 `case type` 是按绑定种类分派的固定表（`core-macro` `global` `lexical` `macro` …）。加一张**绑定扩展表**：`(kind expand-reference expand-set! macroexpand-1?)`，`chi-expr` 对不认识的种类查表。Clojure 的 `clj-var`（读 Var 根 / deref、`set!` 到线程绑定）、`clj-special`（展开器核心形式，`macroexpand-1` 不展开）由此注册，不再改 `chi-expr`。

**第三个维度：错误**。`stx-error` / `syntax-violation` 经参数 `syntax-error-hook` 抛出；缺省构造 R6RS 条件，Clojure 注册为抛 `ExpanderException`。

规模：三张表 + 改造上面 8 处，约 400 ~ 600 行 Scheme，全部在 `core.sls`（我们自己维护的副本）与一个新库里；**nanopass 零改动**（§4）。改完后"给语法对象加一种数据"= 注册一条 `syntax-ext`，"加一种绑定"= 注册一条绑定扩展。

## §3 异常模型简化为 try / catch（原则 2）

元层不对外、无 call/cc 需求，所以：

| 现状 | 改为 |
|---|---|
| `call/cc`（逃逸续延：`EscapeContinuation` + `ContinuationException`，codegen 的 `escape-frame`，stage0 的对应支持） | **删除**。展开器里 5 处：`guard` 的实现改为核心形式 `$try/catch`；`main.sls` 的 3 处 REPL / load 错误捕获改为 `$try/catch`；`compat.sls` 的 `dynamic-wind` 不变 |
| `with-exception-handler` / `raise-continuable` / 处理器栈（`ExceptionHandlers`，AsyncLocal） | **删除**。`raise` = 把条件包成 `SchemeException` 抛出（CLR `throw`）；`raise-continuable` 等同 `raise`（无人依赖可继续语义：展开器的 4 处都是"报告后继续"的 REPL 场景，改为 try / catch 后在 catch 里继续）；`compiler.sls:116` 的转储改为 try / catch 再抛 |
| `guard`（psyntax 展开为 `call/cc` + `with-exception-handler`） | 新核心形式 **`($try/catch body (lambda (c) …))`**：catch `SchemeException` 取其条件（其他 CLR 异常也包成条件再交给处理器，与 IronScheme `with-clr-exception-handler` 的做法一致），子句不匹配时在 catch 处再抛（放弃 R6RS"在 raise 的动态环境里 `raise-continuable`"的细节，元层不依赖它） |
| `dynamic-wind` | 不变（`$try/finally`，无重入） |
| 条件（`conditions.sls`、`Record`） | 保留：`syntax-violation` 的条件对象是展开器错误信息的载体 |
| nanopass 的 2 处 `(guard (c [else #f]) …)` | 不改源码：`guard` 仍是同名宏，展开到 `$try/catch` |

编译器侧：特殊原语表去掉 `call/cc` / `call-with-current-continuation`，加 `$try/catch`；codegen 生成 Lcil 的 `(try (body …) (catch "Cleat.Scheme.SchemeException" …))`（Lcil 已支持）；"try 区域内不发 `tail.`"的规则不变。stage0 加 `$try/catch` 节点。Clojure 的 `try/catch`（按 CLR 类型捕获）是同一机制的推广：`($try/catch body ((type handler) …))` 允许多个按类型的子句，这样 Clojure 节点不需要另一套。

规模：删约 300 行（C# 续延 + 处理器栈 + codegen 的 `escape-frame`），改约 150 行（`guard`、`main.sls`、特殊原语、stage0）。改了 boot 源码的展开器部分，所以要**重生成种子**（IronScheme 05 §5 的两步法）——这是本项的主要成本，也是纳入后第一次验证"我们能在本仓库重生成种子"的机会。

## §4 为什么 nanopass 可以一行不改

- nanopass 只用 `(rnrs)` + `mutable-pairs`，不用 `call/cc` / `dynamic-wind` / `with-exception-handler`；它的 2 处 `guard` 经宏仍能工作（§3）。
- nanopass 的宏（`define-language` / `define-pass` / `nanopass-case`）作用于 **Scheme 数据**（它的 DSL 是 Scheme 源码，`languages.sls` 等编译器文件用 Scheme 写），从不见到 Clojure 的向量 / map。语法对象的扩展（§2）只在数据被 `pred` 认出时触发，Scheme 数据走原有分支，行为不变。
- 展开器对 nanopass 的两处适配（`implementation-helpers.ironscheme.sls`、`define-nanopass-record` 的覆盖库）原样保留。
- 验证：纳入后先跑 IronScheme 的编译器快照测试与自举不动点（它们大量使用 nanopass），作为"nanopass 未受影响"的证据；之后每次改 psyntax 都重跑。

## §5 新的里程碑与任务

替换原计划的 M2（Clojure stage0）与 M4（Clojure 编译器）；M1、M3 已完成的部分全部保留。

| 里程碑 | 任务 | 内容 |
|---|---|---|
| M2′ 纳入元层 | cleat-045 | 纳入并更名 C#：`Cleat.Scheme`、`Cleat.Stage0`、`Cleat.Emit` 合并、`Cleat.PrimGen` 合并；去 async / FFI / 调试器；测试纳入；`dotnet build` 通过 |
| | cleat-046 | 纳入 Scheme：boot、展开器、编译器（去 async）、nanopass、种子、构建脚本；`GenerateSchemeBootImage` 在本仓库产出 Scheme 镜像并通过自举不动点测试 |
| | cleat-047 | 异常模型简化（§3）：删 call/cc 与处理器栈，加 `$try/catch`，重生成种子，不动点再次通过 |
| | cleat-048 | 可扩展语法对象（§2）：三张扩展表与 8 处改造，Scheme 数据行为不变（快照与不动点通过），Clojure 集合注册并有往返测试 |
| M4′ Clojure 在元层上 | cleat-049 | psyntax 的 Clojure 扩展：命名空间模型（交互环境 + ns 表 + 别名 / refer / 重定义 / `resolve`）、交互 rib 的哈希索引、`macroexpand-1`、错误钩子（14 §6） |
| | cleat-050 | `(clojure syntax)` 库：全部特殊形式、`defmacro` 垫片、语法引用、`&env`；以 cleat-013/014 的 `ExpanderTests` 为规格（Lcore 形状可以不同，语义相同）。**用户级卫生宏（`defsyntax`）推迟到 cleat-057**：先保证 Clojure 语法兼容（2026-10-12：cleat-057 搁置，Clojure 的数据层展开契约使卫生无法保证，见该任务） |
| | cleat-051 | 编译器的 Clojure 节点：`var-ref` / `def`、`IFn.invoke(k)` 调用约定与 `ClojureFn : Procedure, IFn`、多类型 `$try/catch`、`deftype` / `reify`（Lcil `type` + `implements`）、`switch`、`keyword-invoke`、集合字面量、`loop` / `recur`；`compile-small` 的 Clojure 版本 |
| | cleat-052 | 编译驱动：`eval`（简单形式解释）、`load` / `compile` 钩子登记、`.clj.dll` + 接口文件；cleat-030 的宿主接上 |
| M5′ Clojure 自举 | cleat-053 | 用 Scheme 镜像编译 `core.clj` 等 → `cleat.boot.dll`；镜像不动点（A 编 core 得 B，B 编得 C，B == C） |
| M6 | cleat-032 | 符合性基线（不变） |
| 后续 | cleat-054 | Scheme 库名 `(ironscheme …)` → `(cleat scheme …)`，同步重生成种子 |

搁置 / 替换：cleat-015、016、018（Clojure stage0）、023（nanopass 移植）、024 ~ 029（Clojure 编译器与 Phase A）、036（Lcore 种子）、044（PoC 并入 cleat-049 的验收：命名空间模型为通过判据）。

## §6 风险

| 风险 | 应对 |
|---|---|
| 种子重生成（cleat-047 改了展开器）在本仓库跑不通 | cleat-046 先在不改任何 Scheme 源码的情况下证明"种子 → stage0 → 镜像 → 不动点"在本仓库成立，再动展开器；改展开器按 IronScheme 05 §5 的两步法 |
| §2 的改造破坏 psyntax（130 处 `syntax-match`） | 先做"零扩展"重构：扩展表只含 pair / vector，快照与不动点逐字节不变；再注册 Clojure 集合 |
| 两种 fasl 合并 | 以 IronScheme 编码为基、加 Clojure 标签；cleat 的 `FaslTests` 改为对新编码运行 |
| 命名空间模型（14 §4 的首要风险） | cleat-049 的验收即 PoC 判据；不通过则回到 14 §8 的 B 方案 |
| 更名范围 | C# 命名空间 / 程序集先改（影响 `primitives.ss` 由 PrimGen 重生成、`targets.sls` 一张表、Emit、Stage0）；Scheme 库名后改（cleat-054），避免第一次自举就同时动两边 |
