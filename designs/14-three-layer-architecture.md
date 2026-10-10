# 14 探讨：三层架构（VM / Scheme 元层 / Clojure）会不会有问题

> 状态：探讨。用户提出的分层：
> 1. 最底层：LLVM、CLR、JVM 等虚拟机；
> 2. 中间：nanopass 的 Scheme 语法对象、必要的 Scheme 语法与 rnrs 库（可裁剪），并提供 Clojure 语法对象；
> 3. 最顶层：与 Clojure 完全对齐的语法与库，完全看不到 Scheme。
>
> 结论：方向成立（它就是 [13](13-syntax-objects-and-scheme-core.md) 方案 C 的推广），但按字面做会出四个结构性问题，每个都有明确的修正；修正后的分层在 §3。风险集中在两处接缝，见 §4。

## §1 字面分层的四个问题

**问题 1：第 2 层把"编译期"和"运行期"混在一起。** rnrs 库有两种身份：psyntax、nanopass、编译器**运行时需要**的库（它们是 Scheme 程序，跑在 Scheme 运行时上），与 Clojure **代码运行时**可能调用的库。前者必须有，后者必须没有——[13 §7](13-syntax-objects-and-scheme-core.md) 的值边界：Clojure 代码只对 Clojure 运行时（`clojure.lang` + 原语表）编译，Scheme 的 32 位 fixnum、`Cons`、`Unspecified`、可变字符串碰不到 Clojure 的值。如果第 2 层被理解为"Clojure 的运行基底"，就得到 jolt 的偏离表。**修正**：第 2 层只是**编译期元层**；运行期契约单独成层（§3 的 2b）。

**问题 2：第 1 层写成"虚拟机"不够，一个目标 = VM + 运行时契约 + 发射器。** 把 L3 之后的代码生成交给目标时，目标必须提供：(a) 原语表的实现（IronScheme 是 484 个 C# 原语；Clojure 另加 `clojure.lang` 的集合、Var、Reflector），(b) 发射器（Lcil → dll；`.ll` → clang；`.class` 写出器），(c) 机器描述表项（类名、调用约定、是否 `tail.`、异常机制）。"LLVM / CLR / JVM"三个词各代表一整套这样的东西，其中运行时契约是最大的一块（IronScheme 11 D7 "运行时契约 = 原语表"）。**修正**：第 1 层定义为"目标 = VM + 运行时契约实现 + 发射器 + 机器描述"，首个目标是 CLR；JVM / LLVM 先靠交叉编译只做 AOT（11 D8、06 D1）。

**问题 3：第 2 层在运行期也要在场，"完全看不到 Scheme"是纪律不是自动的。** Clojure 的 `eval`、REPL、`macroexpand`、运行时 `require` 源码、宏在编译期执行——这些都要求展开器与编译器**随运行时一起发布**（Clojure 自己也是把编译器装在 `clojure.jar` 里）。所以第 2 层不是"构建工具"，它是 boot 镜像的一部分（IronScheme 的镜像约 2 MB，可接受）。既然它在场，"看不到"就只能靠三道边界（13 §7）：`macroexpand` 在 Clojure 特殊形式处停止、值边界、诊断边界（错误信息、栈、类名）。每一道都要专门做并用测试守住，jolt 的经验是最容易漏的是诊断边界。

**问题 4：两套对象模型在一个进程里。** 元层操作的是 Scheme 符号（驻留、带值槽）、Scheme 列表、`Procedure`；第 3 层操作的是 `clojure.lang.Symbol`（不驻留、带元数据）、持久集合、`IFn`。接缝在两处：(a) 展开器入口 / 出口（reader 数据 → 语法对象 → Lcore → `syntax->datum` 给宏），每次宏调用都要转换；(b) 函数值跨层（Scheme 过程流进 Clojure 不是 `IFn`，Clojure 函数被元层调用要走 `Procedure.InvokeK`）。前者是性能问题（展开 `core.clj` 的耗时，PoC 第 6 项），后者是正确性问题（`ClojureFn : Procedure, IFn`，或让 `Procedure` 实现 `IFn`）。**修正**：把转换集中在展开器的两个入口函数里，禁止其他地方跨层传对象；函数值跨层只允许一种表示。

## §2 几个次要问题

- **"裁剪 rnrs"的下限**：[13 §8](13-syntax-objects-and-scheme-core.md) 的表是下限——psyntax、nanopass、编译器导入的那些库不能裁，否则元层自己跑不起来（它们是自举链的一部分，裁了要重生成种子）；能裁的是 IronScheme 的 `lib/`（std、srfi、clr 包装库），对 Clojure 发行版一律不装。
- **语法引用有两种**：Clojure 的 `` ` `` 是 **reader 级**的数据变换（`read-string` 就得到已限定的 `(seq (concat …))`），语义必须保留，供 `defmacro`；卫生宏的模板是展开期的 quasisyntax，要另一个拼写（如 `(syntax …)` 或 `#'`？`#'` 已被 `var` 占用，待定）。两者不能合并，否则 `read-string` 的行为变了。
- **"与 Clojure 完全对齐"受宿主限制**：`deftype` 生成宿主类型、`proxy` / `gen-class`、互操作语法、异常是宿主异常、反射警告——这些本来就按宿主各一份（ClojureCLR 与 JVM 也不同）。分层能做到的是"Clojure 语义的可移植部分完全对齐 + 宿主部分按目标各一份"，语料的 `:common` / `:jvm` 划分就是这条线。
- **命名空间**：Clojure 的 ns 是运行时可变表，psyntax 的库是静态的；元层要提供"命名的顶层交互环境"（13 §3 第 1 条）。这是第 2 层为 Clojure 加的最大一块，不是语法对象本身。
- **依赖与演进**：第 2 层在 IronScheme 仓库里，cleat 依赖它的编译器与展开器；两者由同一人维护可以接受，但更合理的形态是把元层抽成独立的包（§3）。

## §3 修正后的分层

```
 3  表面语言（各一份，互相看不见）
    ├─ Clojure：C# reader、(clojure syntax) 特殊形式库、defmacro 垫片 + defsyntax、命名空间模型、core.clj…、clojure.lang 运行时契约实现
    └─ R6RS：IronScheme 现有的 (rnrs) 表面与 lib/
 ─────────────────────────────────────────────────────────────────
 2a 编译期元层（一份，随镜像发布）：psyntax 语法对象（+ 向量 / map / set 遍历钩子）、
    nanopass、L0 ~ L3 pass 链、codegen 按机器描述表分派、常量中立编码
 2b 运行期契约（接口，一份；实现按目标）：原语表（Scheme 的 484 个 + Clojure 的）、
    调用约定（Procedure / IFn）、异常、Var、类型描述、加载约定
 ─────────────────────────────────────────────────────────────────
 1  目标（各一份）：VM + 2b 的实现 + 发射器 + 机器描述表项
    ├─ CLR：IronScheme.Core + Cleat.Core（clojure.lang）、IronScheme.Emit（+ implements / volatile / switch）
    ├─ LLVM（搁置）：cleat-rt + 用 Clojure 写的集合、clang
    └─ JVM（未计划）：Java 运行时 + .class 写出器；先交叉编译
```

与字面分层的差别只有两点：第 2 层拆成 2a / 2b，第 1 层明确包含运行时契约的实现。IronScheme 在这个图里 = 2a + 2b 接口 + CLR 目标 + R6RS 表面；cleat = Clojure 表面 + CLR 目标的 Clojure 部分。因此最自然的仓库形态是：**元层（2a + 2b 接口）独立成包，IronScheme 与 cleat 都依赖它**；短期内可以先让 cleat 以子模块引用 IronScheme。

## §4 剩余风险（PoC 要回答的）

| 风险 | 为什么是风险 | 判据 |
|---|---|---|
| 命名空间模型 | psyntax 没有"多个命名的、可变的顶层环境 + 别名 + refer + 重定义" | PoC 第 2 项：`(ns a) (def x 1) (ns b (:require [a :as q])) q/x` 与重定义后 `resolve` |
| 展开速度 | psyntax 的标记 / rib 比 Clojure 的"查 Var 调函数"重；每次宏调用两次对象转换 | PoC 第 6 项：`core.clj` 前 1000 行的展开耗时，目标秒级 |
| 函数值跨层 | `Procedure` ≠ `IFn` | PoC 第 5 项：两方向调用都成立且只有一种表示 |
| 诊断边界 | 最容易漏 | 错误黄金文件（09 §2）覆盖 reader / 展开 / 宏 / 运行时四类 |
| `macroexpand` 停止点 | 第 2 层里特殊形式是宏 | 对每个特殊形式断言 `macroexpand-1` 返回原形式 |

## §5 结论

三层的方向是对的，它把 [13](13-syntax-objects-and-scheme-core.md) 方案 C 说成了可以同时承载 R6RS 与 Clojure 两个表面语言、多个目标的通用结构。按字面做会在"第 2 层是什么"上出错：它是**编译期元层加一份运行期契约接口**，不是 Clojure 的运行基底；"看不到 Scheme"靠三道边界的纪律。接受这两条修正后，剩下的不确定性与 13 §6 的 PoC 完全重合，所以下一步仍是 cleat-044。

## §6 风险能否靠扩展 psyntax 解决

能，而且扩展点都已定位（IronScheme 的展开器是 psyntax 的派生副本，在 `boot/expander/core.sls` 里自行维护，改它不是给上游打补丁）。逐条：

| 风险 | psyntax 里的挂钩点（`core.sls`） | 扩展 | 规模 |
|---|---|---|---|
| 命名空间模型 | `interaction-env` 记录 `(rib r locs)`、参数 `top-level-context`、`new-interaction-environment`（从库的 subst 建 rib）、`gen-define-label+loc`（顶层 `define` 在 env 里"伪造"一个 `lexical` 绑定并 `extend-rib!`）、`label->binding-no-fluids` 的 `top-level-context` 分支 | 命名空间 = 带名字的 `interaction-env` + 一张 ns 表；新增绑定类型 `(clj-var . Var)`：`def` 建标签 → Var（重定义复用同一标签，Var 对象可变，所以无需改 rib）；`refer` / `alias` / `import` = 向 rib 加条目或在 `id->label` 前加别名拆解；`chi-expr` 的 `case type` 加 `clj-var` 分支生成 Var 读；私有性放在 Var 元数据 | 300 ~ 500 行 |
| 展开速度 | `id->label`：未封印的 rib 按 `rib-sym*` 线性查找，只有 `seal-rib!` 后才有哈希索引（`rib-sealed/freq`）；交互环境的 rib 永不封印，`core.clj` 几百个顶层定义后每次标识符查找都是 O(n) | 给交互 rib 加可变哈希索引（`extend-rib!` 时同步更新），`id->label` 先查索引；`do-macro-call` 的 `return` 遍历与 `add-mark` 要认识 Clojure 集合（map / set）以免退化为逐项复制 | 100 ~ 200 行；是 PoC 第 6 项的前提 |
| `macroexpand` 停止点 | `chi-expr` 用 `syntax-type` 分派；宏调用集中在 `do-macro-call`（输入加反标记，输出 `gen-mark`） | 新 API `(macroexpand-1 stx env)`：`syntax-type` 后只对 `macro` / `global-macro` 类型调一次 `do-macro-call` 并返回，其余原样返回；Clojure 特殊形式注册为新类型 `clj-special`（实现仍是 core-macro 转换器），`macroexpand-1` 对它不展开，`chi-expr` 对它展开 | 50 行 |
| 诊断边界 | `stx-error` 宏（855 行）→ `syntax-violation`；语法对象带注解 `stx-ae*`（源位置） | wrap 时把 reader 的 `:line` / `:column` / `:file` 元数据转成注解；`stx-error` 改为经钩子抛 `ExpanderException`（Clojure 文本：`Syntax error (file:line:col)`，`:clojure.error/*` 数据）；宏体抛出的异常在 `do-macro-call` 处按 Clojure 的 phase 分类包装 | 100 行 + 错误黄金文件 |
| 函数值跨层 | 不在 psyntax 里 | 运行时：`ClojureFn : Procedure, IFn`，或让 `Procedure` 实现 `IFn`（IronScheme.Core 几十行） | 运行时改动 |
| `defmacro` 的输出 | `do-macro-call` 的 `return` 拒绝输出里的裸符号（"raw symbol encountered in output of macro"） | 垫片用 `datum->syntax` 挂使用处上下文后再返回，自然满足；Clojure 集合要进入 `return` 的遍历 | 含在上两项里 |

结论：五个风险里四个在 psyntax 的既有挂钩点上扩展即可，总量约 600 ~ 900 行 Scheme；"展开速度"的真正隐患不是标记 / rib 本身，而是交互 rib 的线性查找，加索引即可消除。PoC（cleat-044）据此可以把工作内容写得更具体。

## §7 把 IronScheme 的代码直接纳入仓库（不作为依赖）：与原计划（方案 A）的比较

用户决定：元层不以依赖引用，而是把 IronScheme 中能用的代码直接拿进 cleat。实测规模（`~/workspace/IronScheme`）：

| 纳入 | 行数 | 丢弃 | 行数 |
|---|---|---|---|
| `IronScheme.Core`（C# 运行时：过程、符号、数值塔、记录、条件、端口、互操作、fasl、钩子） | 11,579 | `src/scheme/lib/`（std、srfi、clr 包装、async、debugger…） | 49,481 |
| `IronScheme.Emit`（cleat 已移植并扩展，合并即可） | 1,996 | R6RS 的宿主 / REPL / Hosting（cleat 有自己的） | 1,156 |
| `IronScheme.Stage0`（Lcore 解释器） | 1,050 | | |
| boot 源码（含展开器 6,008、数值、unicode、端口…） | 14,686 | | |
| 编译器（nanopass pass 链 + codegen + Lcil 写出） | 3,925 | | |
| nanopass | 4,939 | | |
| 文本种子 `boot-seed.core` | 1.5 MB | | |
| C# 测试 | 4,347 | | |

纳入约 3.8 万行（其中 C# 1.5 万、Scheme 2.3 万）+ 种子；丢弃 5 万行。

**纳入而不是依赖的得失**：得——可以裁剪 rnrs 与 boot（只保 13 §8 的下限）、自由改 psyntax（§6 的扩展）而不受 R6RS 表面约束、一个构建、一个仓库自举（"只依赖本仓库与 .NET SDK"仍成立）；失——与 IronScheme 仓库分叉，修复要两边手工同步（两者同一作者，可接受）；改动 boot 源码后要用 cleat 自己的 stage0 重生成种子（IronScheme 05 §5 的流程照搬）。

**与方案 A（编译器用 Clojure、cleat 自己的 stage0 与 nanopass 移植）比较**

| | 方案 A | 纳入 IronScheme 的方案 C |
|---|---|---|
| 剩余工作 | stage0 解释器（015 / 016）、在 stage0 上加载 core（018）、nanopass 移植（023）、L0 ~ L3 与全部 pass（024 ~ 027）、驱动（028）、自举（029 / 031） | 纳入与裁剪、psyntax 四项扩展（§6，约 600 ~ 900 行）、`(clojure syntax)` 库（约 800 行）、编译器的 Clojure 节点（`var-ref`、`IFn` 调用约定、`try/catch`、`deftype`、`switch`、`keyword-invoke`，约 1,500 行）、自举脚本 |
| 已验证的部分 | 运行时、reader、展开器、发射器、fasl、Loader、宿主（cleat 现有） | 同上，**再加**已自举的展开器、nanopass、编译器、stage0 与种子（IronScheme 现有） |
| 不确定性 | stage0 解释 8,500 行 `core.clj` + 宏密集 nanopass 的速度与正确性；移植 nanopass 的语义等价 | 命名空间模型、交互 rib 的查找速度、两套对象模型的接缝、诊断边界（§4，有挂钩点与判据） |
| 目标达成 | 编译器用 Clojure ✓；nanopass 移植到 Clojure ✓；卫生宏 ✗（另需约 3,000 行） | 编译器用 Scheme ✗；nanopass 原版 ✓；**卫生宏 ✓**（`defmacro` 兼容 + `defsyntax`） |
| REPL / `eval` | M5 之后才有 | 纳入即有（`compile-core`、内存发射） |
| 生成代码质量 | 第一版全装箱、无直接链接（04 §10 后续） | 继承 IronScheme 的拆箱、已知过程直接调用、跨库直接链接（iron-034）；但 Clojure 的 `IFn` 约定与 Var 读要新做，初期可能不如专用 codegen 紧 |
| 进程内的运行时 | 一套（Cleat.Core） | 两套（IronScheme.Core 供元层 + Cleat.Core 供 Clojure）：启动与内存开销，数值塔 / 字符串 / 哈希表各一份；靠值边界避免混用 |
| 自举链 | stage0 → stage1 → stage2 == stage3 | 文本种子 → Scheme stage0 → Scheme boot 镜像 → 编译 `core.clj` → Clojure boot；不动点分两段，都已有判据 |
| LLVM（搁置） | 06 原样 | IronScheme 已有 `target` 表与 `host-*` 中性节点，更近 |
| 团队门槛 | Clojure + C# | Clojure + C# + Scheme / psyntax（改编译器要懂 Scheme） |
| 沉没成本 | — | cleat-013 / 014 的 C# 展开器退役为规格与测试（约 1,000 行）；cleat 的 fasl 与 IronScheme 的 fasl 要合并成一种编码 |

**判断**：纳入方案用"工程量换不确定性"——剩余工作量明显更少（约 4,000 行新 Scheme 对比 A 的解释器 + 移植 + 整条 pass 链），卫生宏与 REPL 是额外收益；代价是编译器语言换成 Scheme、进程内两套运行时、以及四个要靠 PoC 回答的接缝。四个接缝都有定位好的挂钩点（§6），所以建议把 cleat-044 从"是否可行"改成"按 §6 的挂钩点做出最小可运行的 Clojure 编译"，通过后直接转入正式任务。

## §8 建议（2026-10-10）

选纳入 IronScheme 的方案 C：它是唯一与"nanopass 驱动、三层结构、卫生宏"三个目标自洽的方案；风险全部前置、可测（§6 的挂钩点与 §4 的判据）；剩余工作量比 A 少一个数量级。**前提**：命名空间模型在 PoC 里跑通且实现干净（`ns` / `require` / `refer` / 重定义 / `resolve`）——它是 Clojure 与 psyntax 静态库模型相差最远的一块；若做出来是一堆特判，转推荐 B（F#）。若"编译器用 Clojure 写、Clojure 自举"是硬目标，则选 A。下一步：启动 cleat-044，定为"最小可运行的 Clojure 编译"，命名空间模型为唯一的通过判据。
