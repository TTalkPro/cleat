# 13 探讨：语法对象、Scheme 元语言与"Clojure 建在 Scheme 核心之上"

> 状态：**探讨**，不是决定。起因是 `notes/jolt-macro-architecture.md` 的两个发现（jolt 的宏系统接缝只有一处；整个 Clojure 建在 17 个特殊形式 + 87 个 native op + 一道宿主契约之上）。本文回答三个问题：
> 1. 能否按"增加语法对象"的思路重塑 Clojure 的底层流程；
> 2. 直接用 nanopass（Scheme 版）为我们编译，最少需要哪些 Scheme 元语言；
> 3. 能否在这些元语言上构建 Clojure，并给 Clojure 加卫生宏。
> 结论先行：三者都可行，而且指向同一个架构（§4 的方案 C："Clojure on IronScheme"）；它与现设计（方案 A）和 F# 方案（B）的取舍在 §5；建议先做一个两周的 PoC（§6）再定。

## §0 事实

**psyntax 的语法对象**（`~/workspace/IronScheme/src/scheme/boot/expander/core.sls`，4391 行）
- `stx` = (expr, mark*, subst*, annotation*)：`make-stx`、`gen-mark`、`add-mark`、rib（`make-rib`、`extend-rib!`）、`id->label`（标识符 → 标签）、标签 → 绑定（lexical / macro / global / core / library）。`free-identifier=?` 比较标签，`bound-identifier=?` 比较名字 + 标记。
- 语法对象的遍历只认序对、向量（`syntax-vector?`）与注解；没有 map / set。
- 有顶层交互环境：`interaction-environment`、`eval-top-level`、`top-level-context`（`main.sls`），顶层 `define` 可重定义——这正是 Clojure 命名空间需要的动态语义。
- 展开结果是 IronScheme 的 Lcore：`quote if begin set! case-lambda letrec letrec* library-letrec* annotated-call 应用 host-*`（designs 中 IronScheme 03 §2），约 10 种形式。

**nanopass 依赖的宿主能力**（[07 §0](07-nanopass-port.md)）：`syntax-case` 一族、`datum->syntax` 不卫生捕获、`generate-temporaries`、`free-identifier=?`、展开期 `let-syntax`、过程式记录 + 3-D 语法、编译期环境、多值、条件、哈希表。不用 `call/cc`、`dynamic-wind`、`eval`。**这个集合就是 psyntax + R6RS 记录 + `(rnrs base)` 的数据原语**，IronScheme 全部具备且已自举（stage0 约 1000 行 C#、boot 镜像含 psyntax + nanopass + 编译器）。

**IronScheme 的可嵌入面**：`IronScheme.Hosting.SchemeRuntime`（`Initialize` / `Read` / `Eval` / `Import` / `GetProcedure` / `Call`）；`Procedure`（`Invoke0..4` / `InvokeN`）；互操作形式 `host-call` / `host-new` / `host-field` 已在其 L0（iron-057）；Lcil 已支持 `interfaces`（AsyncFrame）；编译器有 `target` 参数与机器描述表（iron-057）；原语表由 PrimGen 反射 `[Primitive]` 生成。**没有的**：运行时生成 CLR 类型（D11，记录是 `Record`）、按 CLR 类型捕获的 `try/catch`（只有 `$try/finally` + 逃逸续延）、`switch`。cleat 的 `Cleat.Emit` 已补了 `implements` / `volatile` / `switch`，可以回流。

**Clojure 今天的"约定式卫生"**：语法引用把自由符号限定到定义处命名空间（引用的卫生）+ `x#` 自动 gensym（绑定的卫生，但要宏作者记得写 `#`）+ `~'x` 有意捕获。缺的只是：不写 `#` 时的绑定捕获、`free-identifier=?` 式的字面量比较、以及用户可用的 `syntax-case` / `syntax-rules`。

**jolt 的结论为何不适用于 cleat**：jolt 不能用宿主展开器，因为 (1) 它的编译器用 Clojure 写且编译自己，展开器若在宿主里就循环依赖；(2) IR 要发射到多个 Scheme。cleat 不同：展开器本来就不用 Clojure 写（C# 或 Scheme），后端消费的是展开后的 Lcore；所以**委托给 psyntax 没有结构性障碍**。

## §1 问题 1：用语法对象重塑 Clojure 的底层流程

**现状（cleat-013）**：reader 产出裸数据（符号带 `:line`/`:column`/`:tag` 元数据），展开器按名字查词法环境与命名空间，宏收裸数据、交裸数据。

**改造后的流程**：

```
reader → 裸数据 → wrap（顶层标记 + 命名空间 rib）→ 语法对象
  → 展开（标识符 → 标签 → 绑定；宏按种类收语法对象或裸数据）
  → Lcore（纯数据，与现在相同）→ 编译器
```

| 要点 | 做法 |
|---|---|
| 语法对象的形状 | 沿用 psyntax：`stx(expr, marks, substs, annotations)`。Clojure 的集合（向量 / map / set，含元数据）要加入遍历：`syntax-vector?` 的做法推广为"语法集合"钩子，map 对键与值都 wrap，元数据原样保留。约 100 ~ 200 行 |
| 命名空间 | 一个命名空间 = 一个顶层交互环境：一个可变 rib（符号 → 标签）+ 标签 → Var。`refer` / `alias` / `import` = 向 rib 加条目；重定义 = 换标签的绑定；`resolve` 在运行时查同一张表。psyntax 的 `interaction-environment` 机制已有，缺的是"多个命名的交互环境 + 别名"，要加 |
| `defmacro` 兼容 | Racket 的 defmacro 垫片：`(define-syntax m (lambda (stx) (datum->syntax (use-site stx) (apply f (syntax->datum (cdr stx))))))`：输入剥上下文，输出挂**使用处**上下文——与今天完全相同的（不卫生）语义，旧宏零改动。约 20 行 |
| 语法引用 | 按 `notes/` 的三条映射：`` `foo `` 自动限定 = 挂定义处上下文（即 quasisyntax 的缺省）；`~'x` = 挂使用处上下文；`x#` = `generate-temporaries`。实现为 quasisyntax 的 Clojure 版（要遍历 Clojure 集合） |
| 新增 `defsyntax` / `syntax-case` / `syntax-rules` | 直接是 psyntax 的能力，只需 Clojure 语法外衣（向量当模式、`...` 省略号已合法） |
| `&env` | 从当前 rib 生成"符号 → 绑定"的 map（只含词法绑定），喂给 defmacro 垫片；`core.async` 的 `go` 这类遍历 `&env` 的宏照常工作 |
| 特殊形式 | 仍是展开器的核心形式；`deftype*` 的签名反射、`case*` 的预哈希、`recur` 的尾位置检查不变 |
| 产物 | Lcore 不变（符号已唯一化、Var 已决议），编译器与后端不受影响 |

**两种实现路径**：
- A′：把 psyntax 的 stx 模型移植进现在的 C# 展开器（`Expander.cs` 再加约 2000 ~ 3000 行：标记、rib、标签、`datum->syntax`、`syntax-case` 的模式匹配与省略号）。保持"展开器在 C#"，但等于用 C# 重写半个 psyntax。
- C：直接用 psyntax（§3），展开器用 Scheme 写在 IronScheme 的库里。

Clojure 语义中与 psyntax 冲突、需要专门设计的只有命名空间（动态、可重定义、运行时 `require`）与 `eval`（宏体内 `eval`、`*ns*` 相关的读）。其余（`&env`、字符串拼出来的符号、`~'x`）都落在 defmacro 垫片的"使用处上下文"里，自然成立。

## §2 问题 2：驱动 nanopass 最少需要哪些 Scheme 元语言

按 [07 §0](07-nanopass-port.md) 的统计倒推，**最小集合**是四层，而不是"几个宏"：

| 层 | 内容 | 规模 | 在 .NET 上从哪来 |
|---|---|---|---|
| 核心形式 | `quote if begin set! lambda / case-lambda letrec letrec* library-letrec*` + 应用 + 注解 | 10 种 | IronScheme.Stage0（约 1000 行 C# 解释器）或 IronScheme 编译器 |
| 卫生展开器 | `syntax-case` `with-syntax` quasisyntax `datum->syntax` `syntax->datum` `free-identifier=?` `bound-identifier=?` `generate-temporaries` `let-syntax` `define-syntax`（展开期求值）`make-compile-time-value` / `define-property`（用 `syntactic-property.sls` 模拟） | psyntax 约 6000 行 Scheme，**自身用宏写成，要种子才能启动** | IronScheme boot 镜像（`boot-seed.core` + stage0 → 不动点，已完成） |
| 记录与数据 | `define-record-type` + 过程式记录（展开期建 RTD、3-D 语法）、序对、向量、符号 / gensym、字符串、字符、数、哈希表、`values` / `let-values`、`apply`、`map` / `fold-left` / `for-all` / `assq`、条件与 `guard`、端口（错误输出） | `(rnrs)` 的大半，IronScheme 约 1.5 万行 boot 源码里的一半 | IronScheme.Core + boot 镜像 |
| nanopass 本身 | 4939 行 | 原样 | IronScheme 子模块，已验证可直接运行（iron-002） |

**结论**：没有"只借几个元语"的方案——nanopass 站在整个 R6RS 宏系统上。把这四层搬到 cleat 等于把 IronScheme 的 Core + Stage0 + boot 镜像搬过来；**既然 IronScheme 就是 .NET 上现成的这四层（同一作者、同一 SDK），正确的做法是把 IronScheme 当作 cleat 的元层来引用，而不是复制。** 那样 cleat 的编译器 pass 用 Scheme 写（`.sls`），`define-language` 直接定义 cleat 的 L0 ~ L3，而且 IronScheme 的编译器已经把一个与 Clojure 的 Lcore 高度相似的核心语言编译到 CIL——闭包转换、`label` / `jump` 循环、box、`host-*` 互操作、常量 fasl、Lcil 发射全在。差异只剩 Clojure 特有的节点（§3）。

## §3 问题 3：在 Scheme 元语言上构建 Clojure，并加卫生宏（方案 C）

**一句话**：Clojure = 一个 psyntax 库 + 一个 C# 运行时 + IronScheme 编译器的几个扩展。jolt 在 Chez 上做的事，在 IronScheme 上再做一遍，但把展开器和编译器都交给宿主，换来卫生宏与现成的 CIL 后端。

```
 .clj 源码 ──C# reader（现有 LispReader）──▶ Clojure 数据 ──wrap──▶ 语法对象
      │
      ▼ psyntax 展开（库 (clojure syntax)：Clojure 的特殊形式 = 对 Scheme 核心形式的宏；
      │   defmacro 垫片；defsyntax = syntax-case；语法引用 = quasisyntax）
      ▼
   IronScheme Lcore + Clojure 扩展节点（var-ref / deftype / try-catch / switch / keyword-invoke）
      │
      ▼ IronScheme nanopass 编译器（Scheme 写的 pass 链）+ cleat 扩展 pass / codegen 表项
      ▼
   Lcil ──IronScheme.Emit（+ cleat 回流的 implements / volatile / switch）──▶ CIL
      │
      ▼ 运行时：IronScheme.Core（过程、符号、数、条件）+ Cleat.Core（clojure.lang 集合、Var、Namespace、Reflector）
```

**各层怎么做**

| 层 | 做法 | 规模估计 |
|---|---|---|
| 语法对象 | §1：psyntax 加 Clojure 集合的遍历钩子（向量 / map / set / 元数据） | 200 行 Scheme（改 psyntax，以补丁或覆盖库维护） |
| 命名空间 | 命名的顶层交互环境：rib + 别名 + refer；`in-ns` 切换当前 rib；`def` 在当前 rib 建标签 → Var；`resolve` / `ns-map` 查同一张表 | 500 行 Scheme，是 C 的**首要风险** |
| 特殊形式 | `(clojure syntax)` 库：`def` → 建 Var 并 `bindRoot`；`fn*` → `case-lambda` 包成 `IFn`；`let*` / `letfn*` → `let` / `letrec`；`loop*` / `recur` → 命名 let（IronScheme 已降为循环，尾位置由宏保证）；`if` → `if` + Clojure 真值；`do` → `begin`；`.` / `new` / `set!` → `clr-call` / `clr-new` / `clr-field-set!`（已有）；`throw` → `$throw`（已有）；`var` → 常量；`import*` → `clr-using`；`monitor-*` → 原语 | 800 行 Scheme |
| 新核心形式 | `try/catch` 按 CLR 类型捕获（IronScheme 只有 `$try/finally`）；`deftype*` / `reify*`（生成真实 CLR 类型，cleat 的 `Cleat.Emit` 已能发射）；`case*` → `switch`；`keyword-invoke`；`var-ref`（读 Var 根 / deref）；调用约定 `IFn.invoke(k)`（0 ~ 20）与 IronScheme 的 `Invoke0..4` / `InvokeN` 并存：Clojure 的 fn 类 `ClojureFn : Procedure, IFn` | 编译器扩展 1500 行 Scheme；codegen 表项与 Lcil 扩展回流 IronScheme |
| 运行时 | Cleat.Core 现状（clojure.lang、Reflector、TypeResolver、Fasl、Loader）作为 IronScheme 的原语来源：PrimGen 反射第二个程序集；`cleat.kernel` 原语照旧 | 已有；加 `[Primitive]` 标注或表 |
| 宏 | `defmacro` 垫片（使用处上下文）；`defsyntax` / `syntax-rules`；语法引用 = Clojure 集合版 quasisyntax；`&env` 由 rib 生成；`macroexpand` / `macroexpand-1` 由 psyntax 提供 | 300 行 Scheme |
| core.clj | cleat-017 的源码基本不动（它只用特殊形式 + 互操作 + 原语）；`core_deftype` 的协议分派用 `MethodImplCache`（C#） | 已有 |
| 自举 | **不需要 Clojure 的 stage0**：IronScheme 镜像（已自举）展开并编译 `core.clj` → `cleat.boot.dll`（Clojure 侧的"库"）；不动点 = 镜像 A 编 core 得 B，B 再编得 C，B == C。cleat 的编译器扩展本身也可由 IronScheme 编译（它们是 Scheme 库，走 `compile-library`） | 构建脚本 |
| 卫生宏对 Clojure 的收益 | 不写 `#` 的绑定也不捕获；`free-identifier=?` 让 `case` / 模式字面量按绑定而非名字比较；用户可写 `syntax-case`；旧 `defmacro` 不变 | — |

**语义冲突清单**（必须在 PoC 中逐条验证）

1. 命名空间是动态表，psyntax 的库是静态的：只能用交互环境模型，`require` 在运行时加载并把导出写进调用方 rib；AOT 的 `.clj.dll` 要能在加载时重建 rib（接口文件记录导出）。
2. `&env` 的内容：Clojure 宏期望 `符号 → LocalBinding`；垫片从 rib 生成裸符号键即可，值给一个占位对象（jolt 的做法）。
3. 用字符串拼符号的宏（`(symbol (str "make-" name))`）：垫片的使用处上下文让它们按今天的方式解析——保持不卫生，符合预期。
4. `eval` 与 `*ns*`：`eval` 在当前交互环境求值；宏体内的 `(resolve 'x)` 查当前 rib。
5. 调用约定与性能：Scheme 编译的代码调 Clojure 函数走 `IFn.invoke(k)`；Clojure 调 Scheme 原语走 `Procedure.InvokeK`；两种对象都要能当作函数值（`ClojureFn : Procedure, IFn` 解决一半，Scheme 过程作为 Clojure 函数值需要包装或让 `Procedure` 实现 `IFn`——改 IronScheme）。
6. 尾调用：IronScheme 发 `tail.`；对 Clojure 代码可用机器描述表关掉（栈跟踪与 JVM 一致）或保留（更深递归），需决定。
7. 异常：Clojure 的 `try/catch` 捕获 CLR 异常类型；IronScheme 的条件系统与逃逸续延是另一套；Clojure 代码只用前者，两者在 CLR 异常层面兼容（条件是 `SchemeException`）。
8. `deftype` 生成 CLR 类型：与 IronScheme D11（记录不生成类型）并存：Clojure 的 `deftype` 走新核心形式与 codegen；IronScheme 的记录不受影响。
9. 两套"符号"：IronScheme 的 `Symbol`（驻留、带值槽）与 clojure.lang 的 `Symbol`（不驻留、带元数据）：语法对象里的标识符用前者，Clojure 数据用后者，`syntax->datum` 时转换；`datum->syntax` 反向。开销在展开期，可接受。
10. 依赖：cleat 依赖 IronScheme 仓库（子模块或 NuGet）。两者同一作者，可接受，但 00 D15 的"只依赖本仓库"要改。

## §4 三个方案

| | A：现设计（编译器用 Clojure，stage0 自举） | B：编译器用 F# | C：Clojure on IronScheme |
|---|---|---|---|
| nanopass | 移植约 2000 行 Clojure（07） | F# DU + 手写遍历（每层约 50 行） | **直接用 Scheme 版** |
| 展开器 | C#（已完成，cleat-013/014） | C#（已完成） | psyntax + `(clojure syntax)` 库；C# 展开器退役（它的规则与测试成为 Scheme 版的规格） |
| 编译器 | Clojure，M4 全新 | F#，M4 全新 | IronScheme 编译器 + 约 1500 行 Scheme 扩展 |
| stage0 | 需要（C# Lcore 解释器 + deftype 转发类） | 不需要 | 不需要（IronScheme 已自举） |
| 卫生宏 | 需移植 psyntax 到 C#（约 3000 行）或不做 | 同 A | **免费**，且 `defmacro` 兼容 |
| 发射器 | Cleat.Emit（已完成） | Cleat.Emit | IronScheme.Emit + cleat 扩展回流 |
| 运行时 | Cleat.Core（已完成） | 同 | 同 + IronScheme.Core |
| 外部依赖 | 无 | 无 | IronScheme 仓库 |
| "编译器用 Clojure 写" | ✓ | ✗ | ✗（用 Scheme 写，但宏系统对用户是 syntax-case） |
| 主要风险 | stage0 解释 8500 行 core.clj + 宏密集 nanopass 的速度与正确性 | 两种语言；nanopass 失去自动生成 | 命名空间 ≠ 库；两套符号 / 过程对象；性能（Var、invoke）要专门做；IronScheme 的演进节奏 |
| LLVM 后端（搁置） | 06 原样 | 06 原样 | IronScheme 已有 `target` 表与 `host-*` 中性节点，更近 |
| 已完成工作的复用 | 全部 | 全部 | 运行时、reader、Reflector、fasl、Loader、宿主全部复用；C# 展开器降为规格；Cleat.Emit 的扩展回流 |
| 剩余工作量（粗估） | M2 余下 + M4 + M5/6：最大 | M4（F#）+ M5/6：中 | 命名空间模型 + `(clojure syntax)` + 编译器扩展 + 自举脚本：中，但不确定性最高 |

## §5 判断

- 问题 1、2、3 的答案都是"能"，而且 2 的答案（元语言 = IronScheme 的全部四层）决定了 3 只有一种经济的做法：以 IronScheme 为元层。
- C 是三者中**概念上最统一**的：Clojure 成为"一个卫生 Scheme 核心之上的库"，宏系统、编译器、后端都来自同一套已自举的基础设施；它也是唯一把卫生宏作为副产品拿到的方案。
- C 的不确定性集中在**命名空间模型**与**两套对象模型的接缝**，这两点不做 PoC 无法判断；A 的不确定性集中在 stage0 的速度，B 几乎没有技术不确定性但放弃了两个目标（Clojure 自举、卫生宏）。
- 不建议 A′（把 psyntax 移植进 C# 展开器）：工作量接近重写 psyntax，却得不到 C 的编译器复用。

## §6 建议的 PoC（两周，`spikes/cleat-044/`，在 IronScheme 仓库内运行）

1. **语法对象**：给 psyntax 加 Clojure 向量 / map / set 的遍历（补丁），用 C# reader 读 `(let [x 1] [x {:a x}])` → wrap → `syntax->datum` 往返。
2. **命名空间原型**：两个命名的交互环境 + `refer` + 重定义 + `resolve`，验证 `(ns a) (def x 1) (ns b (:require [a :as q])) q/x`。
3. **`(clojure syntax)` 最小库**：`def` `fn*` `let*` `loop*` `recur` `if` `do` `.` `new` + `defmacro` 垫片 + 语法引用，编译 `(defn fib [n] ...)` 与一个用 `clojure.lang.PersistentVector` 的函数，经 IronScheme 编译器产出 CIL 并运行。
4. **卫生**：`(defsyntax my-or ...)` 用 `syntax-case` 写，验证 `(let [tmp 1] (my-or false tmp))` 不被捕获；同一例子用 `defmacro` 复现捕获，证明兼容层语义不变。
5. **接缝**：`ClojureFn : Procedure, IFn`；从 Scheme 调 Clojure 函数、从 Clojure 调原语；`try/catch` 按类型的新核心形式原型（只做发射）。
6. **度量**：展开 `core.clj` 前 1000 行的耗时（估计 IronScheme 镜像上为秒级）；记录 10 条冲突清单的逐条结论。

通过判据：1 ~ 5 全部跑通且 6 的耗时可接受 → 采用 C，重写 00 的 D4 / D6 / D14、04、07、08 与任务表；否则在 A / B 之间定。

## §7 方案 C 下 Scheme 会不会侵入 Clojure

语言层面不会，前提是封死三道边界；边界之外有一处美观问题、一处正确性风险。jolt 在 Chez 上的已知偏离表（字符串按码点、整数无身份、打印差异）就是边界没封严的后果；cleat 的条件更好——Clojure 的值本来就是 `clojure.lang` 的 CLR 对象。

| 边界 | 要求 | 不封的后果 |
|---|---|---|
| 语法 | 用户只写 Clojure；`macroexpand` / `macroexpand-1` 在 Clojure 特殊形式处停止，不展到 `case-lambda` / `letrec`；`&form` 与 `defsyntax` 的输出经 `syntax->datum` 只含 Clojure 集合 | 宏作者看到 Scheme 核心形式 |
| 值 | Clojure 代码只对 Cleat.Core 的原语与 `Reflector` 转换规则编译（机器描述表按**源语言**分项，不复用 IronScheme 的 `Interop`）：IronScheme 的 fixnum 是 32 位 `int`、列表是 `Cons`、空表 `null`、`Unspecified`、可变字符串、`SchemeChar`、互操作把 `long` 转 Scheme 数 / 枚举转符号——这些都不能碰到 Clojure 的值 | jolt 式偏离 |
| 诊断 | psyntax 的语法错误包装成 `Syntax error (file:line:col)`；条件 → `ExceptionInfo`；函数类名 `ns$name`；`(class f)` 为 `ClojureFn`，`(fn? f)` 为真 | 错误信息与栈是 Scheme 风格 |

会露出来的：
- 美观：CLR 栈里夹着 `IronScheme.Core.*` 的帧（过程分派、展开器），像 Clojure 过滤 `clojure.lang.*` 帧一样过滤。
- 正确性：Scheme 过程作为**值**流进 Clojure 时是 `Procedure` 不是 `IFn`。要么让 `Procedure` 实现 `IFn`（改 IronScheme），要么规定 Clojure 只拿 `cleat.kernel` 包装过的函数。列入 PoC 第 5 项的判据。
