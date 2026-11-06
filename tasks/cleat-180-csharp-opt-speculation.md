# cleat-180 优化·推测与分层

- 里程碑：M20　依赖：cleat-179　状态：实现完成，G4 待 181（单项开 / 关的聚焦配对与全表 1 轮冒烟已跑，见实施记录；56×5 全表未跑）
- 设计依据：[30 §4](../designs/30-csharp-compiler.md)；现行 `codegen.clj` 的 `collapse-entry-labels`（:1154）、`speculate-entries`（:1580）、`speculate-loops`（:1416：scalar-label :1322、spec-label :989、array-spec-label :1087）、`mark-tier-classes!`（:2708）

## 工作内容

每项一个 L4 → L4 的独立 pass，做完后重跑 `InferTypes`（规则同现在 codegen:3473-3484）：
1. `CollapseEntryLabels`（`collapse`）。
2. `SpeculateEntries`（`typed`）：无提示 defn 的类型化入口、`TypedSelf` / `TypedEntry`、`TypedBailout`。
3. `SpeculateLoops`（`loopspec`）：long / double 快慢两份循环。
4. `SpecializeArrays`（`arrayspec`）：无提示数组变量按元素类型推测。
5. `ScalarReplace`（`scalar`）：循环变量的 deftype 标量替换。
6. `MarkTierClasses`（`imagetier`；`tiered` 由 `EmitIL` 读）：分层编译的方法属性、`optimize`（AggressiveOptimization）。

推测 pass 的先后顺序与现在一致（每个循环标签：scalar → 否则 loopspec → arrayspec）。

## 验收

- 每项开 / 关两臂 G4 配对，与原任务（103 / 104 / 114 / 117 / 123 / 127）记录的方向一致；全表无可声明回退。
- `ClojureCompilerTests` 中 TypedEntries、LoopSpeculation、scalarisation、TieredEval 用例在 `CLEAT_COMPILER=cs` 下通过。

## 实施记录（2026-11-04）

**产物**（`src/Cleat.Compiler/Lower/`）
- `SpeculateLoops.cs`（新）：开关 `loopspec` `arrayspec` `scalar` `collapse`；`Fresh`（loops/fresh 的 名字%N，序号进程内唯一）、`SpecRename`（spec-rename）、
  形状检查 `SimpleBody`（spec-simple? / scalar-nodes-ok?）、`SpecBinders`、`SpecFinalJump`、`SpecNumericUse`；数值推测 `SpecCopy` / `SpecGuess` / `SpecLabel`；
  数组推测 `RtCallName` / `ArrayUses` / `ArrayGuesses` / `TagVar` / `ArraySpecLabel`；`CollapseEntryLabels`；标量替换 `ScalarTypeFields` / `NewOfParts` / `FieldReadOf` / `Tails` /
  `LoopJumps` / `PrimArgConv` / `RwSub` / `PushLetIntoTails` / `ScalarizeLetNew` / `ScalarizeLets` / `ScalarLabel`；`SpeculateLoops` pass（每个标签先内层：scalar → 否则 loopspec，再 arrayspec）。
- `SpeculateEntries.cs`（新）：开关 `typed`；`SpecPure` / `SpecPureCall`、`SpecTypedCopy`、`SpecTypedSelves`、`SpecBodyType`、`SpecEntryTry`（AOT 档：没有 cleat-138 的运行时 guard /
  ref-types，那部分随 163 / 170 已删除）、`SpecEntryFor`、`SpeculateEntries` pass；发射 `CgTypedSelf`（cg-typed-self!）、`CgTypedEntry`（cg-typed-entry!，try / catch TypedBailout）、
  `TypedEntryMethod`（clj-typed-entry-method）。
- `Tiering.cs`（新）：开关 `imagetier`；`MarkTierClasses`（mark-tier-classes!）、`TieredMethod`（*tiered-method*，线程静态，在 `CljFnClass` 里按类设置并恢复，
  动态范围与 Clojure 的 binding 相同）、`ArgTake`（arg-take!）。
- `*tiered-method*` 的各处行为：`LoadSlot` 经 ldloca / ldarga + `Locals.Take`；if（:value）、clj-static-call、KnownCall 的汇合局部变量；尾位置循环的 keep 集与 jump 的
  形参置空（含 Num 形参）；转发方法与 :static / :invoke 桥的实参 Take；`FnClasses.cs` 给分层类的方法加 `Tiered`（转发 / 桥方法除外，同 fwd-methods），类型化入口方法跟着子句方法登记。
- `Emit/IlEmitter.cs`：补上 Cleat.Emit 的 `TakeLoadsFor`（分层方法里只写一次读一次的 object 临时变量，读改成 ldloca + Locals.Take；条件同 Cleat.Emit：eval 单元里模块类型以外的方法，
  或带 Tiered 的方法）。174 的发射器只读了 `Tiered` 决定 AggressiveOptimization，没有这一步。
- `FnClasses.cs` 的 `RunReprPasses`：generate-unit 的顺序——MarkTierClasses（eval 或 imagetier）→ 推断 → collapse（变了就清表重推）→ SpeculateEntries → SpeculateLoops
  （按 loopFlags）→ 变了就清表重推，类型化入口的体按签名固定形参后推断（codegen.clj :3445-3505）。

**偏差**：TypedEntry / TypedSelf 的 quote 里放 Clojure 向量（`[签名 形参…]` / `[签名]`），与原版相同的数据形状；IR 节点集合不变（推测输出仍是 L3 的 `CljCall`）。

**验收**
- 正确性与 IL 形状：见 cleat-179 的实施记录（同一次比对）：`typed / loopspec / arrayspec / scalar / collapse / imagetier / tiered` 各自关掉时 21 个文件两边 stdout 逐字相同、
  IL 形状 0 行差异；缺省设置下 r7rs 56 项 0 行差异；包模式（含 imagetier 的 Tiered 方法标志与 take-loads）逐方法 IL 字节相同。
- Host 测试（`CLEAT_COMPILER=cs dotnet test tests/Cleat.Host.Tests -c Release --filter "ClojureCompilerTests|OptFlagTests|ClassGenerationTests|ValueBoundaryTests"`）：
  **180 个里 172 过、8 个失败**：
  - 6 个读 Lcil 转储（C# 编译器不产生 Lcil，按任务说明记为预期 / 过时，测试未改）：`AccessorInliningCrossCompileUnits`（eval-on.lcil）、`ArrayAccessIsResolvedStatically`（arr.dll.lcil）、
    `JoinRawUses`（jr.dll.lcil）、`LoopSpeculation`（spec.dll.lcil）、`NumUnboxedArithmetic`（numbench.dll.lcil）、`TypedEntries`（typed.dll.lcil）。
    它们断言的形状（类型化 aget / aset、原始 join、快慢循环、Num、invokeStaticTyped）在 C# 侧由上面的 IL 形状 / 字节比对覆盖。
  - `ClojureDriverEvalsLoadsAndCompiles`：断言 Clojure 驱动"简单形式解释执行"的计数（`DriverOps.SimpleCount` / `CompiledCount`，期望 `[6 0]`，C# 驱动输出 `[0 0]`）——驱动功能，不属 179 / 180。
  - `CoreFunctionsKeepLazySeqsCollectable`：512 MB 堆下 `lazy_head_core.clj` 第 9 行 `(apply + (big))` OutOfMemory。**改动前的 C# 后端（从 ~/workspace/cleat 取原文件另编一份）同样在第 9 行 OOM**，
    且关掉 tiered / infer / known / inline 都不变，所以不是本任务引入的，也没被本任务修好；Clojure 编译器缺省通过。顶层 Init 的 IL 形状不在探针范围内，原因未查，留给 181 前处理。
    **合并后补查（2026-11-04）**：最小复现（只有 `(defn big …)` 和 `(println (apply + (big)))` 两行）在 Clojure 编译器下也 OOM；`DOTNET_TieredCompilation=0` 时两种编译器都通过。
    所以不是 C# 编译器生成的代码抓住了头，而是镜像（仍由 Clojure 编译器编）里的核心函数停在 tier-0 时抓着：缺省模式下前几行与 Clojure 编译器自身的运行把这些函数跑热了，
    cs 模式不运行 Clojure 编译器，第 9 行时仍在 tier-0。181 用 C# 编译器编镜像时要定位是哪个核心函数（imagetier 的 arg-take / Locals.Take 覆盖），再让这项测试在 cs 下通过。

**性能（与 cleat-179 同一次跑分，方法与口径见 179 的实施记录：两臂 `CLEAT_COMPILER=cs`、3 轮、--shuffle、同轮配对中位；本机并发、低于 5 轮，|比值 - 1| < 10% 视为不可判定）**

| 开关 | 基准（原任务） | on/off 配对中位（同向 / 轮） | 读数 |
|---|---|---|---|
| loopspec | sum / sumfp（103） | x0.042 (3/3) / x0.089 (3/3) | 大幅收益 |
| typed | fib / tak / fibfp（104） | x0.221 (3/3) / x0.334 (3/3) / x0.186 (3/3) | 大幅收益 |
| tiered | nqueens / puzzle / browse（114） | x0.708 (3/3) / x0.624 (3/3) / x0.674 (3/3) | 收益，方向与 114 一致 |
| scalar | mbrotZ（123） | x0.605 (3/3) | 收益（123 记 0.63） |
| collapse | mbrotZ（123） | x0.962 (3/3) | 地板内 |
| arrayspec | array1 / fft（127） | x0.995 (2/3) / x0.972 (3/3) | 不可判定 |
| imagetier | —（117） | 未测 | r7rs 跑法是 eval 加载源码，镜像仍由 Clojure 编译器编出，C# 的 imagetier 在这里不起作用；正确性见包模式的 IL 逐字节比对 |

**全表 1 轮冒烟：`CLEAT_COMPILER=cs` vs 缺省（Clojure 编译器），同一构建**：56 项每项两臂背靠背各 1 次（臂顺序逐项交替），无错误 / 超时。
cs/clj 比值几何平均 **x0.972**；> 10% 变慢的 1 项（earley x1.112），> 10% 变快的 5 项（fib x0.708、parsing x0.769、ctak x0.809、fibc x0.868、sum1 x0.894），
其余在 x0.90 ~ x1.09。上面的 IL 比对显示两个编译器给这些程序生成的函数类 IL 形状相同（包模式逐字节相同），所以这些差异应当主要是单轮噪声，
另有可能的系统性来源（未验证）：C# 编译器不加载 Clojure 编译器包，进程的堆与 JIT 状态不同。原始数据 `results/triage149-m20-full.jsonl`。
**G4（56 项 × 5 轮同轮配对、全表无可声明回退）没有跑，属 cleat-181。**
