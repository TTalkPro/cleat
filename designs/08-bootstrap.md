# 08 自举

## 1. 原则

- **只依靠 .NET SDK 与本仓库**：不需要 ClojureCLR、JVM Clojure 或任何已有 Clojure 实现来生成第一个 boot 镜像。
- **stage0 是精简的 C# Lcore 解释器**（`Cleat.Stage0`），跑在 `Cleat.Core` 上，与新系统共用 reader 与展开器（00 D4）。它的唯一任务是从源码加载 `core.clj`、`cleat.nanopass`、`cleat.compiler`，完成第一次编译。
- **不需要文本种子**即可从零重建（与 IronScheme 的差别：Clojure 的展开器在 C# 里，不需要先有展开后的自己）；可选的 Lcore 种子只是加速缓存（§5）。
- **可验证**：Lcil 文本与 dll 的不动点。

## 2. 组成

| 组成 | 是什么 |
|---|---|
| `Cleat.Core` | 运行时 + reader + 展开器（02、03） |
| `Cleat.Stage0` | Lcore → 闭包树的解释器；`deftype*`/`reify*` 经 `Cleat.Emit` 生成真实类型；命令行 |
| `Cleat.Emit` | Lcil → dll（05）；stage0 也用它生成 `deftype` 类型 |
| `src/clojure/`、`src/cleat/` | core 库、nanopass、编译器（Clojure 源码） |
| `src/boot/build-boot.clj` | 构建脚本：按 03 §5 顺序编译全部 boot 命名空间，写 Lcil 文本与 `cleat.boot.dll` |

## 3. 流程

```
 Phase A：stage0 编译出新 boot 镜像（dotnet build 的 GenerateBootImage 目标）
 ┌────────────────────────────────────────────────────────────────┐
 │ Cleat.Stage0（C#，运行在 Cleat.Core 上）                           │
 │  1. 登记原语；建立 clojure.core / cleat.kernel 命名空间             │
 │  2. 逐形式 读 → 展开 → 解释 加载 cleat/kernel.clj、clojure/core.clj │
 │     及 03 §5 的全部 boot 命名空间（宏定义后立即可用）               │
 │  3. 同样加载 cleat/nanopass、cleat/compiler（nanopass 宏在此展开）    │
 │  4. 运行 build-boot.clj：对每个 boot 命名空间的每个顶层形式            │
 │     展开 → Lcore → 编译器（解释执行）→ Lcil；**同时解释执行该形式**    │
 │     （后面的宏与 deftype 要用）；收集全部 Lcil                        │
 └──────────────────────────────┬─────────────────────────────────┘
                                ▼  boot.lcil（文本，可 diff）
                   Cleat.Emit → cleat.boot.dll（stage1）+ cleat.boot.ifc

 Phase B：新系统自举
   Cleat.Host --boot stage1 build-boot.clj → stage2（Lcil + dll）
   Cleat.Host --boot stage2 build-boot.clj → stage3
   判据：stage2 与 stage3 的 Lcil 文本一致，dll 逐字节一致；ILVerify 通过
```

Phase A 第 4 步的"编译的同时解释执行"对应 Clojure `compile` 的语义（`*compile-files*` 时既写 class 又执行）；在 stage0 上执行的是解释器闭包，编译产物只进 Lcil。与 IronScheme 不同，没有"bootstrap 库集合 vs 运行中系统"的双份展开问题：Clojure 的展开器无状态地作用于当前运行的命名空间。

## 4. stage0 解释器

| 项 | 决定 |
|---|---|
| 输入 | Lcore（03 §3），由同进程的展开器产生；`--core <file.edn>` 可直接解释 Lcore 文本 |
| 实现 | 核心形式先分析成闭包树（"编译成闭包"）：`local` 解析为（层数, 下标），Var 解析为对象引用，原语按名字绑定到 `IFn`；执行时不查名字 |
| 值与原语 | 直接用 `Cleat.Core` 的对象；`host-*` 节点经 `Reflector` 反射执行（类型按 `TypeResolver`） |
| 函数 | 解释器闭包是 `AFunction` / `RestFn` 子类（`InterpretedFn`），可与原语、编译后代码、`MultiFn` 互相调用；`invoke(k)` 按 arity 表分派 |
| `recur` | `label`/`jump` 在解释器循环里重赋参数；不增长栈 |
| `deftype*` / `reify*` | 用 `Cleat.Emit` 内存模式生成真实类型：字段按声明、接口按展开器查出的签名；每个方法体 = `ldsfld 方法表[i]; ldarg…; callvirt IFn::invoke(k)`（`proxy` 式）；构造函数填字段；`reify` 的闭包捕获放在额外字段。解释器把方法体闭包放进方法表。**不动点无关**：这些类型只存在于 stage0 进程 |
| `try`/`throw`/`monitor` | C# `try`/`throw`/`Monitor` |
| `case` | 预哈希数据 → `Dictionary` 查找 |
| 不支持 | `import*` 以外的任何 ClojureCLR 专有形式；`host-*` 中的 `by-ref`、泛型方法推断失败的情况（报错） |
| 命令行 | `stage0 [--time] [-I dir] <script.clj> args…`；`stage0 --core <file.edn>`；在 512 MB 栈的线程上运行 |
| 规模 | 1500 ~ 2500 行 C#（IronScheme 1000 行 + `deftype` 生成 + `case`/`try`/`letfn`）；只求正确 |

cleat-003 实测：方法表转发每次调用比直接 `callvirt` 多约 4 ns（2.4 → 6.2 ns），stage0 可以接受；方法表在 `CreateType` 之后再填、运行时可替换。

**速度要求**：IronScheme 实测种子 0.3 s、nanopass 4.5 s。cleat 的 stage0 多了从源码展开 `core.clj`（8500 行，宏展开本身是解释执行的宏函数）；预计 Phase A 总耗时 1 ~ 3 分钟，可接受（只在 core / 编译器源码变化时重跑）。若超过 5 分钟：启用 Lcore 种子（§5）。

## 5. Lcore 种子（可选缓存）

- `tools/build.cs seed` 用新系统或 stage0 把 03 §5 全部 boot 命名空间展开为 Lcore 文本（`src/boot/boot-seed.edn`，常量用 05 §3 编码），stage0 读种子时跳过 reader 与宏展开。
- 它**不是**从零重建的必要条件；用途是 (a) 加速 Phase A，(b) 作为编译器快照测试的真实输入，(c) 审阅展开结果。
- 种子不动点：新系统与 stage0 生成的种子逐字节相同（gensym 编号按 §7 规范化）。

## 6. 交叉编译纪律

| 规则 | 原因 |
|---|---|
| 编译器不读运行时状态（04 §1） | Phase A（解释执行的编译器）与 Phase B（编译后的编译器）输出一致 |
| 原语信息只来自 `primitives.edn` | 同上 |
| 常量只经中立编码 | 同上；stage0 的 `InterpretedFn`、临时类型不会进产物 |
| `deftype` 的接口签名由展开器查反射 | 展开器两边相同（都是 `Cleat.Core`） |

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
