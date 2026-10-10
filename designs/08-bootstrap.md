# 08 自举

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
