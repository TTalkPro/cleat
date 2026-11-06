# 08 自举

> **历史（2026-11-05，cleat-182）**：文本种子 `src/boot/seed/` 与 Clojure 编译器的自举（§0）、`selfhost` / `clojure-seed` 已删除。
> 现在 `dotnet build` 由 C# 编译器从源码两步编出镜像（`--no-boot` 编 stage1，再在其上编），构建两次逐字节相同；见 [30](30-csharp-compiler.md) D8 与 [docs/boot.md](../docs/boot.md)。

> **2026-10-15 改版（cleat-091，[19 D7](19-clojure-self-hosting.md)）**：Clojure 镜像与 Clojure 写的编译器自举，信任根是 Lcil 文本种子。
> §0 是现行设计；§1 ~ §8 是 Scheme 元层时期（cleat-046 ~ 092）的设计，Scheme 元层已在 cleat-093 删除，只作历史保留。

## 0. Clojure 编译器的自举（cleat-091 起）

**组成**

| 组成 | 是什么 | 由谁产生 |
|---|---|---|
| `src/boot/seed/*.gz` | 自举到不动点的镜像（`cleat.boot.lcil` 与 `.ifc` / `.inl`）与编译器包（`cleat.compiler.lcil`，嵌入资源 `.ifc` / `.inl` 在旁边）的 Lcil 文本，gzip；约 2 MB | `tools/build.cs clojure-seed`（自举两级、验证不动点、种子发射回来与 stage3 逐字节比较） |
| stage1 | 种子发射出的 `cleat.boot.dll` 与 `cleat.compiler.dll`（`cleat-emit --seed`，`obj/seed-stage1/`） | `EmitSeedStage1`（`Cleat.Host.csproj`） |
| `src/boot/cleat.boot.dll` | Clojure 镜像 | `GenerateBootImage`：stage1 在 stage1 镜像上 `--compile-boot` |
| `src/boot/cleat.compiler.dll` | Clojure 编译器的包程序集 | `GenerateCompilerPackage`：改名的工具编译器在新镜像上编 |

**改名的工具编译器**：正在运行的编译器（命名空间 `cleat.compiler.*` / `cleat.nanopass.*`）不能在同一进程里重新加载自己的源码编自己——
记录类型、接口被重定义，新旧代码混用（`CljFnName` 的类变了而旧的谓词还在用）。所以先把源码改名成 `cleat.compiler0.*` /
`cleat.nanopass0.*`（`cleat --rename-compiler-sources`），由正在运行的编译器编成工具，再由工具（`CLEAT_COMPILER_NS=cleat.compiler0`）
编真正的 `cleat.compiler`。两套命名空间互不相干。

**包与镜像**：包程序集带镜像核对（只能在编它时的那个镜像上运行）。工具以下一级镜像为目标编出（`--compile-package … --target-image 镜像.dll`），
运行在下一级镜像上编编译器；编译器包里的直接链接与内联只看运行着的镜像的接口文件，所以编译器包只取决于镜像与源码。

**流程与判据**（`tools/build.cs selfhost`）

```
 stage1 = 种子（或 src/boot 的镜像与编译器包）
 镜像 N+1 = 编译器 N 在镜像 N 上 --compile-boot
 工具 N   = 编译器 N 在镜像 N 上编改名的源码（--target-image 镜像 N+1）
 编译器 N+1 = 工具 N 在镜像 N+1 上编源码
 判据：stage2 与 stage3 的镜像（dll、Lcil、.ifc、.inl）与编译器包（dll、Lcil）逐字节相同；
      从种子构建（dotnet build）得到的镜像与编译器包与种子发射出的 stage1 相同
```

宿主加载镜像后 `require` 编译器的驱动 `cleat.compiler.driver`：镜像旁有编译器包就登记它、从包加载；自举时 `CLEAT_COMPILER_NS=cleat.compiler0`
指定改名的工具编译器，从 `-I` 的源码加载（cleat-093 起没有别的编译器：没有镜像或镜像旁没有编译器包时 `eval` 不可用，`--compile-*` 报错）。
编译器源码改了而包没重编时，`dotnet build` 的 `GenerateCompilerPackage` 按输入增量重编（宿主不再比较源码与包的时间）。

**种子的纪律**：种子是构建的起点，`dotnet build` 出的镜像与编译器包必须与种子发射出的 stage1 逐字节相同（Host 测试
`ClojureBootstrapTests.BuildFromTheSeedReproducesTheSeed`）。所以改了 src/clojure（core.clj 等进镜像的源码、编译器源码）之后、提交之前
要 `tools/build.cs clojure-seed` 重生成种子（约 3 分钟）；只改 C# 运行时不必（种子是 Lcil 文本，不含运行时的实现）。

**运行时契约的两步法**：生成的代码调用 Cleat.Core 的 C# 方法（`ClojureOps`、`ClojureStaticCalls`、`RT.Box` …），种子里的
Lcil 也一样。改这些签名时：① 先加新入口、保留旧入口，让旧种子发射出的 stage1 仍能运行，编译器改用新入口；② `tools/build.cs clojure-seed`
重生成种子（新种子只用新入口）；③ 再删旧入口。发射器的改动同理：种子是 Lcil 文本，发射器的行为变化会改变 stage1 的字节，重生成种子即可。


> **2026-10-10 改版**：按 [15](15-scheme-core-vendoring.md) 的方案，自举分两段：Scheme 元层的镜像（种子 + Scheme stage0，自 IronScheme 纳入，cleat-046 已在本仓库达到不动点）与 Clojure 的镜像（由 Scheme 镜像编译 `core.clj`，cleat-053）。原设计（Clojure 的 C# stage0 解释器、Lcore 种子）作废，保留在文末"已作废"一节供查阅；§7 确定性措施继续有效。

## 1. 原则

- **只依靠 .NET SDK 与本仓库**：纳入的元层连同其文本种子都在仓库里，不需要 IronScheme、ClojureCLR 或 JVM。
- **两段不动点**：第一段 Scheme 镜像自编译（stage1 == stage2 == stage3），第二段 Clojure 镜像由稳定的 Scheme 镜像编译并自复现（镜像 A 编 core 得 B，B 编得 C，B == C）。
- **可验证**：Lcil 文本与 dll 逐字节比较；ILVerify。

## 2. 组成

| 组成 | 是什么 | 由谁产生 |
|---|---|---|
| `src/scheme/boot/boot-seed.core` | Scheme boot 源码（含 psyntax）展开后的核心形式，文本 | 新系统或 stage0 自己再生成（`tools/build.cs seed [--stage0]`，IronScheme 05 §5 的两步法） |
| `Cleat.Stage0` | Scheme Lcore 解释器（自 IronScheme.Stage0），`--boot` 时也可作为 Scheme 镜像的运行器 | 本仓库 |
| `src/boot/cleat.scheme.boot.dll` | Scheme 镜像：(rnrs) 库 + 展开器 + nanopass + 编译器（+ cleat-048 ~ 050 的语法扩展与 `(clojure syntax)`） | `GenerateSchemeBootImage`（`Cleat.Host.csproj`，增量，`-p:BuildBootImage=false` 跳过） |
| `src/boot/cleat.boot.dll` | Clojure 镜像：`cleat.kernel`、`clojure.core` 及随附命名空间 | `GenerateBootImage`（cleat-053） |

## 3. 流程

```
 第一段：Scheme 镜像（cleat-046，已成立）
   stage0 执行 boot-seed.core → 得到展开器与 (rnrs) → 从源码展开 nanopass 与编译器（解释执行）
   → 编译全部 boot 源码 → cleat.scheme.boot.lcil → Cleat.Emit → stage1
   stage1 运行构建脚本 → stage2 → stage3；判据：stage1 == stage2 == stage3（Lcil 与 dll，2026-10-10 实测逐字节相同）
   改了 boot 源码（尤其展开器）时按两步法重生成种子；种子不动点：stage0 与镜像生成的种子相同

 第二段：Clojure 镜像（cleat-053）
   Scheme 镜像加载 (clojure syntax) → 按 boot-order.edn 逐文件、逐形式 展开 → 编译 → 执行（宏定义后立即可用）
   → cleat.boot.lcil → Cleat.Emit → cleat.boot.dll + cleat.boot.ifc
   判据：用镜像 A 编得 B，用 B 编得 C，B == C
```

与 IronScheme 的差别：第二段没有自己的 stage0，Clojure 从来不需要被解释执行；`eval` 的"简单形式解释"（04 §9）由一个只认简单 Lcore 的 C# 小解释器承担（cleat-052；stage0 的解释器不认 Clojure 节点）。

## 4. 耗时（2026-10-10 实测，cleat-046）

Phase A 56 s（展开 boot 4.9 s、nanopass + 编译器 16.9 s、编译 8.5 s、写 Lcil 6.1 s、发射 1.3 s）；stage2 / stage3 各 21 s；种子生成 30 s（stage0）/ 14 s（镜像）；`dotnet build` 首次 71 s、增量跳过 4 s。第二段的耗时在 cleat-053 记录。

## 5. 交叉编译纪律

| 规则 | 原因 |
|---|---|
| 编译器不读运行时状态（04 §1） | 两段里"解释执行的编译器"与"编译后的编译器"输出一致 |
| 原语信息只来自 `primitives.ss`（PrimGen 生成，含 `Cleat.Scheme` 与 `Cleat.Core` 两组） | 同上 |
| 常量只经中立编码（05 §3，联合 fasl） | 同上；stage0 的内部对象不进产物 |
| Clojure 的 `deftype` 签名由展开期反射得到并作为数据进入 Lcore | 两段相同的运行时 |

## 6. 种子管理

| 阶段 | 种子 |
|---|---|
| 现在 | `src/scheme/boot/boot-seed.core`（自 IronScheme 纳入，经 cleat-046 的类型名改名后与本仓库 stage0 / 镜像再生成的结果逐字节相同） |
| 改展开器（cleat-047、048、049） | 每次按两步法再生成；`Bootstrap` 测试类别守住两段不动点 |
| 库名更名（cleat-054） | 同步再生成 |
| Clojure 侧 | 不需要种子：`core.clj` 永远从源码由 Scheme 镜像编译 |

## 7. 确定性

| 来源 | 处理 |
|---|---|
| `gensym`（`G__123`）与自动 gensym（`x__123__auto__`） | 构建时的 `gensym` 计数在每个顶层形式开始时**按（命名空间, 形式序号）重置**（jolt `set-name-counter!` 的做法）；展开器的局部变量唯一化用（名字, 定义序号），不用全局计数 |
| 类名 | `ns$name__N`，N 按单元内定义顺序（04 §3 pass 5） |
| `Symbol` / `Keyword` 哈希 | 按名字（Murmur3），与分配顺序无关 |
| map / set 常量的写出顺序 | 编码时按键排序（05 §3） |
| `PersistentHashMap` 的遍历顺序 | 按哈希，哈希按名字 → 稳定；展开器与编译器中需要稳定顺序处一律用 `sorted-map` 或向量 |
| MVID、时间戳 | 发射器按内容哈希回填（05 §1） |
| `Random`、时间 | 构建脚本不用 |

## 8. 种子管理与产物

| 阶段 | 产物 |
|---|---|
| M5 | stage0 → stage1 的 `cleat.boot.dll` 放 `src/boot/`（忽略，不提交） |
| M6 | stage3 作为工作区二进制缓存；`Cleat.Bootstrap.Tests`：stage0 → 1 → 2 → 3，断言 2 == 3 的 Lcil 与 dll、1 == 2 的 dll、ILVerify |
| 从零重建 | `dotnet build`（C# 项目 + `GenerateBootImage`）；约 2 ~ 4 分钟 |

## 已作废（2026-10-10 前的设计）

原 §3 ~ §5 描述 Clojure 的 C# Lcore 解释器 stage0（cleat-015 / 016）、Phase A 在其上解释运行 Clojure 写的编译器、可选的 Lcore 种子（cleat-036）。这些任务已搁置，细节见 git 历史中本文的早期版本。
