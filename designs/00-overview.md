# 00 总体设计

Cleat：.NET 上的 Clojure 实现。运行时用 C#，编译器用 Clojure 自己写（nanopass），CIL 与 LLVM IR 是两个平等的后端，自举只依靠 .NET SDK 与本仓库。

## 1. 目标

1. **实现 Clojure 核心语法**：reader、全部特殊形式、`clojure.core` 及随附命名空间（`core.protocols`、`string`、`set`、`walk`、`edn`、`pprint` 等），语义以 JVM Clojure 1.12 为准，以 jolt 的 JVM 取证语料为符合性判据（[09](09-testing.md)）。
2. **按 IronScheme 的方案分阶段自举，生成核心 boot 文件**：stage0 是一个 C# 核心形式解释器，它从源码加载 `clojure.core`，运行用 Clojure 写的 nanopass 与编译器，编译出 `cleat.boot.dll`（stage1）；新系统再编译自己到不动点（[08](08-bootstrap.md)）。
3. **把 nanopass 框架移植到 Clojure**：`cleat.nanopass`，语义等价、表面按 Clojure 习惯调整（[07](07-nanopass-port.md)）。
4. **LLVM IR 与 CIL 是平等的后端**：编译管线在闭包转换之后才分目标；每个目标一份机器描述、一个发射器、一套满足同一原语表契约的运行时（[04](04-compiler.md)、[05](05-cil-backend.md)、[06](06-llvm-backend.md)）。
5. 设计文件在 `designs/`，任务文件在 `tasks/`，索引为仓库根目录 [TASK.md](../TASK.md)。

## 2. 约束与非目标

| 项 | 说明 |
|---|---|
| 工作约定 | 每个任务完成后提交一次；提交信息不带 Co-Authored-By；不 push |
| 构建 | 仓库根目录 `dotnet build` / `dotnet test`（`Cleat.slnx`）；boot 镜像由 `Cleat.Host.csproj` 的 `GenerateBootImage` 目标运行 stage0 增量生成。LLVM 后端另需 clang / LLVM（本机 21.1.8），只在 `-p:BuildNative=true` 时参与 |
| 目标框架 | 只有 **net10.0**（本机 SDK 10.0.401）。不支持 .NET Framework、不支持 net6 / net8 |
| 外部依赖 | 运行时只用 BCL。不引入 DLR（ClojureCLR 的反射调用与 `AFn : IDynamicMetaObjectProvider` 都建在 DLR 上，这是与 ClojureCLR 最大的分歧之一）。nanopass 原框架只作为移植的参照，不作为依赖 |
| 代码来源与许可 | 本仓库 Apache-2.0。从 ClojureCLR 复制改写的 C#（持久集合、数值、Var、Namespace、reader 等）与从 Clojure 改写的 `core.clj` 等是 **EPL**。它们放在单独目录并保留 EPL 许可文件与头注释（`src/Cleat.Core/clojure.lang/`、`src/clojure/`），新写的代码是 Apache-2.0。**这是需要用户确认的决定**（D11） |
| 非目标 | 与 ClojureCLR 的二进制兼容（`Clojure.dll` 类型身份）；`gen-class` / `proxy` 的完整 Java 风格语义第一版不做（`proxy` 以 `reify` + 委托近似，`gen-class` 留到后续）；DLR 动态站点；.NET Framework；LLVM 后端上的 `eval`（第一版只做 AOT） |
| Clojure 版本 | 语法与 `core.clj` 以 Clojure 1.12 为准（ClojureCLR master 跟踪 1.13.0-alpha，取其 1.12 稳定部分） |

## 3. 总体架构

```
  源码 (.clj/.cljc/.cljr)
       │
       ▼
 ┌──────────────────────────────────────────────────────────────┐
 │ 展开器（C#，Cleat.Core.Expander；stage0 与新系统共用）          │
 │  reader → 逐个顶层形式 macroexpand-all → 符号决议 → Lcore      │
 │  （不纯：要读 Var、运行宏；是运行时的一部分）                     │
 └───────────────────────────────┬──────────────────────────────┘
                                 ▼  Lcore（S 表达式，只含特殊形式）
 ┌──────────────────────────────────────────────────────────────┐
 │ nanopass 编译器（Clojure，cleat.compiler；纯函数）              │
 │  parse-core → L0 … → L3（闭包转换后，宿主中性）                 │
 │        ├─ codegen-cil  → Lcil（文本 / S 表达式）                │
 │        └─ codegen-llvm → LLVM IR（.ll 文本）                    │
 └──────────┬───────────────────────────────┬───────────────────┘
            ▼                               ▼
 ┌─────────────────────────┐   ┌──────────────────────────────┐
 │ 发射器 Cleat.Emit（C#）    │   │ clang / llc（外部工具）           │
 │ Lcil → Reflection.Emit   │   │ .ll → .o → 可执行 / .so         │
 │ 内存程序集 / 持久化 dll    │   │ 链接 cleat-rt（C，Boehm GC）     │
 └──────────┬──────────────┘   └──────────────┬───────────────┘
            ▼                                 ▼
 ┌─────────────────────────┐   ┌──────────────────────────────┐
 │ 运行时 Cleat.Core（C#）    │   │ 原生运行时 cleat-rt（C）+        │
 │ clojure.lang.* 集合、Var、 │   │ 用 Clojure 写的集合（deftype）    │
 │ 数值、reader、互操作、原语  │   │ 满足同一原语表                   │
 └─────────────────────────┘   └──────────────────────────────┘
```

与 IronScheme 的对应：psyntax 展开器 ↔ C# 展开器（Clojure 的宏展开只是"查 Var、调函数"，不值得用 Clojure 写再靠种子启动）；Lcore ↔ Lcore（只含 Clojure 的特殊形式）；`(ironscheme compiler)` ↔ `cleat.compiler`；`IronScheme.Emit` ↔ `Cleat.Emit`；`IronScheme.Core` ↔ `Cleat.Core`；`IronScheme.Stage0` ↔ `Cleat.Stage0`。

## 4. 关键决策

| # | 决策 | 理由 | 详见 |
|---|---|---|---|
| D1 | 运行时 `Cleat.Core`（C#，net10.0，只依赖 BCL）。持久集合、数值塔、`Var`、`Namespace`、`Keyword`、`Symbol`、STM、`MultiFn`、`MethodImplCache`、`LispReader`、`EdnReader` **从 ClojureCLR 复制改写**，保留 `clojure.lang` 命名空间与类型名 | 这些类与编译器无耦合（01 §3），约 5 万行成熟代码不值得重写；大量 Clojure 库按 `clojure.lang.IFn` 等名字做 `instance?` 判断 | 02 |
| D2 | **不用 DLR**。反射互操作用自己的 `Reflector`（按 IronScheme `Interop` 的规则：按实参类型选重载并缓存）；`AFn` 不实现 `IDynamicMetaObjectProvider` | 去掉 ClojureCLR 最大的外部依赖与最慢的路径（它每次反射调用都编译一个 DLR lambda） | 02 §8 |
| D3 | 函数模型沿用 Clojure：`IFn.invoke` 0 ~ 20 参 + `applyTo`，`AFunction` / `RestFn` 两个基类；每个 `fn*` 编译成一个类，自由变量是字段；无自由变量的函数另生成静态 `invokeStatic` 供直接链接 | 与现有 Clojure 代码、`MultiFn`、协议分派的调用约定兼容；IronScheme "两个基类、`InvokeN` 统一入口"的教训同样适用 | 02 §4 |
| D4 | **展开器在 C#，在运行时里**（`Cleat.Core.Expander`）：reader → 逐个顶层形式 `macroexpand-all` → 符号决议（词法 / Var / 宿主类型与成员 / 特殊形式）→ **Lcore**。stage0 与新系统共用；**不需要文本种子**即可自举 | Clojure 宏展开只是查 Var 的 `:macro` 元数据并调用函数（jank、ClojureCLR 都在宿主语言里做）；Clojure 要求逐形式"展开—求值"交替，展开器必须与运行时同进程。IronScheme 需要种子是因为 psyntax 是 Scheme 写的 | 03 |
| D5 | 编译器输入固定为 **Lcore 文法**（03 §3），编译器是**纯函数**：不读 Var、不运行目标代码、不反射目标程序集；原语表与类型提示都是数据 | 可交叉编译（LLVM 后端靠它）、可比较不动点、stage0 上与编译后的输出一致 | 04 |
| D6 | 编译器用 Clojure 写（`cleat.compiler`），基于 `cleat.nanopass`；L0 ~ L3 宿主中性（互操作节点为 `host-*`），之后 `codegen-cil`、`codegen-llvm` 各一份，由机器描述表驱动 | 用户目标 3、4；IronScheme 11 §2 的结论：把"机器"定义在托管运行时 / 原生运行时这一层，Chez 寄存器机以下的 pass 用不上 | 04、07 |
| D7 | CIL 后端输出**文本 Lcil**，由 `Cleat.Emit` 汇编；Lcil 规格继承 IronScheme 04，扩展类型定义（`deftype*` / `reify*` 的接口实现、公开字段）、`switch`、协议调用站点 | 编译器与发射器解耦；可 diff；stage0 内部对象不混进产物 | 05 |
| D8 | 常量用编译器自己定义的中立编码（符号、关键字、持久集合、正则、Var 引用……），发射器转二进制 fasl；运行时读回 | 取代 ClojureCLR 以 `EmitValue` 逐项生成 IL 的做法；LLVM 后端用同一编码生成数据段 | 05 §3 |
| D9 | 宿主成员的重载决议放在**发射器**（有真实 `Type`）；决议不了的落到运行时 `Reflector`（按实参类型缓存）。类型提示在 Lcore 里是符号 | 编译器保持纯函数（IronScheme D8） | 04 §7、05 §5 |
| D10 | **LLVM 后端第一版只做 AOT，由 .NET 宿主交叉编译**：.NET 上的 cleat 以 `target = llvm` 展开并编译源码，输出 `.ll`，clang 链接 `cleat-rt`；原生侧没有 `eval`、没有展开器。原生运行时 = C 内核（对象、GC、数、字符串、符号 / 关键字、Var、函数调用、异常、IO）+ **用 Clojure 写的持久集合**（`deftype*` + 协议，ClojureScript 的做法） | 把整个 Clojure 运行时再用 C 写一遍不现实（jank 的 C++ 运行时约 5 万行且仍无协议 / deftype）；ClojureScript 已证明集合可以用 Clojure 自己写；交叉编译是 IronScheme 11 D8 的结论 | 06 |
| D11 | **许可分区**：EPL 派生代码（ClojureCLR C#、Clojure 的 `.clj`）放 `src/Cleat.Core/clojure.lang/` 与 `src/clojure/`，带 EPL 文件与头注释；其余 Apache-2.0。待用户确认 | 两种许可都允许这样组合，但必须显式 | 01 §5 |
| D12 | stage0 的 `deftype*` / `reify*`：用 `Cleat.Emit` 生成**真实 CLR 类型**，方法体跳转到解释器闭包（`proxy` 式的方法表） | `core.clj` 的 `deftype` / `reify` 实现 `ISeq`、`IDeref` 等接口，C# 运行时会直接调用；不生成真实类型则 `instance?` 与接口分派全失效 | 08 §4 |
| D13 | Var 是全局值槽：编译后的代码从常量表取 `Var`，读 `getRawRoot()`（非 `^:dynamic`）或 `deref()`；非 `^:dynamic`、非 `^:redef` 的库内函数调用**直接链接**到静态方法 | 等同 Clojure 的 direct linking 与 IronScheme 的"已知过程直接调用" | 02 §5、04 §6 |
| D14 | 尾调用：`recur` 是循环；**不做通用尾调用优化**（与 Clojure 一致，CIL 不发 `tail.`，LLVM 不发 `musttail`） | Clojure 语义没有 TCO；`tail.` 在 try 区域内不可用且较慢；保留后续按需开启的余地（04 §8） | 04 |
| D15 | `core.clj` 按 jolt 的方式**分层**：`kernel`（编译器与 nanopass 依赖的最小集合，先加载）→ `core` 主体 → `core_deftype` / `protocols` / `core_print` / `gvec` / `instant` / `uuid` → 其余命名空间按需加载。宿主差异用 reader 条件 `#?(:cleat-clr … :cleat-llvm …)`；`:cljr` 作为 `:cleat-clr` 的别名 | 编译器要先于 `core.clj` 的大部分可用（jolt `00-kernel` 的教训）；两个后端共用一份 `core.clj` | 03 §5 |
| D16 | 符合性以 **jolt 的 `corpus.edn`**（JVM 取证、宿主中性、约 5000 条）为主判据，另跑 jank 的 `clojure-test-suite` 与 ClojureCLR 移植的 `test_clojure` | 三套互补：语料是按值比较的规格，另两套覆盖库与宿主 | 09 |

## 5. 仓库布局（目标形态）

```
Cleat.slnx
Directory.Build.props              ; net10.0、Release 默认、Nullable、警告级别
designs/  tasks/  TASK.md
src/
  Cleat.Core/                      ; 运行时（C#）
    clojure.lang/                  ; EPL：从 ClojureCLR 改写的集合、数值、Var、Namespace、reader…
    Runtime/                       ; 原语、钩子、Reflector、ModuleLoader、fasl
    Expander/                      ; reader 之上：macroexpand、决议、Lcore 输出
  Cleat.Emit/                      ; Lcil → Reflection.Emit
  Cleat.Stage0/                    ; Lcore 解释器 + 启动器
  Cleat.Host/                      ; 命令行、REPL、`GenerateBootImage`
  Cleat.PrimGen/                   ; 反射生成 primitives.edn
  clojure/                         ; EPL：core.clj 等（按 D15 分层）
  cleat/
    nanopass/                      ; cleat.nanopass（07）
    compiler/                      ; languages、parse、passes、codegen-cil、codegen-llvm、driver
    targets.edn                    ; 机器描述表
  boot/                            ; 构建脚本、primitives.edn、生成的 cleat.boot.dll（忽略）
native/
  cleat-rt/                        ; C 内核运行时（06）
  core/                            ; 原生目标专用的 Clojure 源码（集合等）
tests/
  Cleat.Core.Tests/  Cleat.Emit.Tests/  Cleat.Bootstrap.Tests/
  conformance/                     ; corpus.edn、runner、已知偏离表
  clojure-test-suite/              ; 子模块（jank）
  test_clojure/                    ; 从 ClojureCLR 移植
tools/build.cs                     ; stage1 / seed / ilverify / native 等维护命令
spikes/                            ; 每个验证任务一个目录
```

## 6. 里程碑

> 2026-10-10 决定：**先完成 CIL 能力，LLVM 后端（M8）整体搁置**，设计保留，任务标为搁置。cleat-011 的原语表仍按两列格式设计，`llvm` 列暂为空。

| 里程碑 | 内容 | 产出 |
|---|---|---|
| M0 准备与验证 | 仓库骨架；许可分区；验证 .NET 10 能力（沿用 IronScheme iron-003 的结论，补 Clojure 需要的项：`switch`、接口类型生成、PersistedAssemblyBuilder 上的 `deftype`）；验证 LLVM 工具链（`.ll` 调 C 运行时、EH、`musttail`） | 结论与最小复现 |
| M1 新运行时 | `Cleat.Core`：从 ClojureCLR 提取集合等；`IFn` 模型；Var / Namespace；无 DLR 的 `Reflector`；reader 带决议接口；原语与 PrimGen；钩子 | 可单元测试的运行时 |
| M2 展开器与 stage0 | 展开器输出 Lcore；stage0 解释器（含 `deftype*` 生成真实类型）；`core.clj` 分层适配；在 stage0 上加载全部 core；语料子集在 stage0 上通过 | 能跑 Clojure 的解释器 |
| M3 Lcil 与发射器 | Lcil 规格（继承 + 扩展）；`Cleat.Emit`；常量编码与 fasl；加载约定 | 手写 Lcil → dll 可运行 |
| M4 nanopass 与编译器 | `cleat.nanopass`；L0 ~ L3；pass 链；`codegen-cil`；驱动；全部在 stage0 上开发运行 | 能编译单个命名空间 |
| M5 首次自举 | stage0 编译 core + nanopass + 编译器 → `cleat.boot.dll`；新宿主与 REPL | 新系统首次启动 |
| M6 自举闭合与符合性 | stage2 == stage3；语料、clojure-test-suite、test_clojure 基线 | 不动点、测试数字 |
| M7 工程 | 命名空间 AOT（`.clj.dll`）、加载路径、`compile`、自包含发布、REPL 行编辑、嵌入 API | 可日常使用 |
| M8 LLVM 后端 | 机器描述表；`cleat-rt` 内核；`codegen-llvm`；原生集合（Clojure）；交叉编译驱动；语料 `:common` 子集原生通过 | 原生可执行文件 |
| 后续 | 性能（拆箱、协议内联缓存、跨库直接链接）、异步、`gen-class`、调试信息 | — |

## 7. 主要风险

| 风险 | 影响 | 应对 |
|---|---|---|
| stage0 解释执行 `core.clj`（8500 行）+ nanopass 宏展开太慢 | 自举耗时过长 | 解释器"编译成闭包树"；IronScheme 实测 stage0 加载 nanopass 约 4.5 s，可接受；`kernel` 分层让编译器尽早可用；可选的预展开种子作为缓存（08 §5） |
| `deftype*` / `reify*` 在 stage0 上需要生成真实类型 | 没有它 `core.clj` 加载不了 | D12：复用 `Cleat.Emit` 生成转发类；M0 先验证 PersistedAssemblyBuilder / 内存程序集上接口实现的生成 |
| 从 ClojureCLR 提取的代码与编译器 / DLR 有隐性耦合（`RT.load`、`LispReader` 的符号决议、`Reflector`） | 提取工作量超预期 | 01 §3 已列清单：reader 引入决议接口、`RT` 拆成运行时与加载器两半、`Reflector` 重写 |
| 编译器是纯函数，但 Clojure 的 `case*`、`deftype*` 等宏在展开期就依赖反射（`case` 预哈希、`deftype` 查接口） | 纯度被破坏 | 这些反射在**展开器**里做（它本来就不纯），结果以数据进入 Lcore；编译器只看数据 |
| LLVM 后端的原生运行时规模 | M8 拖得过长 | D10：内核极小，集合用 Clojure 写；第一版只求语料 `:common` 子集通过，不求性能 |
| 不动点被非确定性破坏（gensym、哈希遍历顺序、MVID） | 自举无法闭合 | 08 §7：规范改名、符号按名字哈希、MVID 回填；Lcil 文本可 diff |
| EPL / Apache 混用 | 法律与贡献问题 | D11 分区，待用户确认；或后续逐步替换 EPL 部分 |
