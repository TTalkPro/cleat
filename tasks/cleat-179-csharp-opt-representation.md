# cleat-179 优化·表示：类型推导与表示选择

- 里程碑：M20　依赖：cleat-177　状态：实现完成，G4 待 181（单项开 / 关的聚焦配对与全表 1 轮冒烟已跑，见实施记录；56×5 全表未跑）
- 设计依据：[30 §3.3、§4](../designs/30-csharp-compiler.md)；现行 `codegen.clj` 的 `infer-walk`（:646）、`infer-var-types-into!`（:779）、`cg-raw`（:1752）、Num（:1708）、装箱（:86-91）、宿主静态决议（:566 / :1838）

## 工作内容

1. `InferTypes`（L4 上的分析 pass）：格 `:fx :fl :l64 :num`、原始 / 装箱使用权重（`loopweight`）、穿过 if / begin 尾位置的原始用途（`joinraw`）；
   只对 `typed-clauses` / 生成了循环的形式运行（规则同现在）。
2. `ChooseRepresentation` 的完整档：按推导结果定 `Long / Double / Num / Bool / Char / Typed(T)`，插入显式 `Box` / `Unbox`；
   `num`（未定类型数用 `Num`）、`boxcache`（`LongBox.Get`）、`boxdouble`（`LongBox.Double`）、`charbox`（`CharBox.Get`）、`host`（编译期静态决议）。
3. `EmitIL` 对各表示的原始指令路径（算术、比较、局部、形参、返回）。

## 验收

- 每个开关开 / 关两臂 G4 配对；与原任务（100 / 101 / 102 / 108 / 109 / 110 / 142）记录的方向一致；全表无可声明回退。
- `ClojureCompilerTests` 中类型推导相关用例在 `CLEAT_COMPILER=cs` 下通过；`OptFlagTests` 覆盖新 pass 的开关。

## 实施记录（2026-11-04）

**产物**（`src/Cleat.Compiler/Lower/`，逐函数移植 codegen.clj，各成员的注释写明对应的 Clojure 函数）
- `InferTypes.cs`（新）：`ReprTables`（var-types / sound-types / fixed-types / var-facts / num-aliases / assigned-vars，加上 180 用的 spec-entries / tier-classes；
  线程静态，每个编译单元清空）、开关 `infer` `num` `joinraw` `loopweight`、带环境的 `ExprType`（expr-type）、`NumbersArithArgs`、忠实的 `L3Some`（l3-some?，含 own?）、
  `JumpsTo` / `RealLoop` / `BiggerThan`（count-nodes / big-body）/ `CollectAssigned`、`InferWalk`（infer-walk：MaybeTyped / Raw / joinraw / loopweight 权重 / deftype 原始字段）、
  `InferVarTypesInto`（infer-var-types-into!：单调不动点、decide!、Num 档的形参 / 别名 / 数值 let 三步）。
- `RawEmit.cs`（新）：cg-raw 全档（`CgRaw`：Num → TypedSelf → 含 Num 的算术取值 → Numbers long 原始运算 → PrimInvoke → 原始返回的宿主调用 → cg-raw-join! → 缺省）、
  `CgRawDefault`、`CgRawNum` / `NumArith` / `NumEmit` / `NumbersRawEmit`、`CgRawJoin`（if / begin / let）、类型化局部（`BindLocal` / `BindValue` / `BindingCode` / `BindLetVars`）、
  `BindNumParams`、`NumAliasLoc`、`VarRawType`、jump 的 keep 集清除 `ClearUnkeptArgs`。
- 改动：`CodeGen.cs`（`Raw` 增 `Fx` / `Num`；`LocKind` 增 `TLocal` / `NumArg`，`Loc` 按值相等；`MethodCtx.Big`；cg 的原始尾返回；let / label / jump 的原始存储与原始临时变量；
  clj-call 缺省分支先试 Num / Numbers 原始运算；`LoadVar` / `StoreVar` 的新位置；`PackArray` 的局部变量顺序改成与 pack-array! 一致）、
  `HostCalls.cs`（`ExprType` 移出；`HostCallSignature` / `StaticArgType` / `HostCallType` 带环境，缺省或 var-types 时用 sound-types，同 host-call-signature）、
  `Rt.cs`（Num、Numbers 的 long 重载、TypedBailout）、`FnClasses.cs`（各子句方法设 `Big` 与 `BindNumParams`；`GenerateUnit` 接 loopFlags，pass 顺序见 designs/30 §4）、
  `Types.cs`（deftype 方法与 async 子句设 `Big`）、`Driver/CsDriver.cs`（`CompileUnit` 算每个形式的"含循环"标志：LowerLoops 生成了 label 或 typed-clauses > 0，同 pipeline.clj front-ends）。

**与设计的偏差**：L4 不建节点，是"L3 + 按变量的表示表"（designs/30 §3.3 已改写）；`ChooseRepresentation` 不是独立 pass，是发射侧读表的部分（`RawEmit.cs`）。
推测 pass 的试算推断与原版一样写进同一组全局表（只涉及新名字），没有改成纯函数——为了逐字保持决策。

**验收**
- 正确性：21 个 `*_semantics.clj*` 在缺省与 `CLEAT_COMPILER=cs` 下 stdout 逐字相同；分别设 `CLEAT_NO_OPT=infer / num / joinraw / loopweight / host / boxcache / boxdouble / charbox`
  （以及 180 的各开关、`1`）两边仍逐字相同（`/tmp/m20/cmp.sh` 的矩阵，16 种设置 × 21 个文件全过）。`dotnet test tests/Cleat.Compiler.Tests -c Release` 26 / 26。
- **IL 形状逐方法比对**（本任务的主要证据）：探针加载程序后反射每个 Var 上的函数类，打印每个方法的签名、局部变量类型表、IL 长度、MethodImpl 标志，两个编译器 diff：
  21 个语义文件 0 行差异；r7rs 56 项的命名空间（eval 加载）0 行差异；上面 15 个单项开关各自对 21 个文件 0 行差异。
  包模式（`--compile-package` 编 20 个 r7rs 命名空间，3200 个方法，含嵌套闭包）**逐方法 IL 字节完全相同**（修了 `PackArray` 的局部变量顺序之后；之前只有 16 个方法因这个 174 遗留的
  槽位次序不同），`CLEAT_NO_OPT=imagetier` / `infer` 下同样逐字节相同。
- `CLEAT_NO_OPT=1` 时 IL 形状有差异，全部来自发射器：Cleat.Emit 按子串判断 eval 是否分层（`"1"` 不含 `tiered`，Clojure 路径仍分层并做 take-loads），
  C# 的 IlEmitter（cleat-174）用 `OptFlags.Off("tiered")`（`1` 全关）。输出仍逐字相同；`NO_OPT=tiered` 时两边形状一致。未改（属 174 / Cleat.Emit 的口径差，记账）。
- Host 测试见 cleat-180 的实施记录（同一次运行）。

**性能（2026-11-04，本机 192.168.3.13，与另一个代理的构建 / 测试并发；数字只作方向参考）**

方法：`bench/r7rs/triage149.py -r 3 --shuffle`（每轮打乱臂顺序），两臂都是 `CLEAT_COMPILER=cs`，关臂加 `CLEAT_NO_OPT=<开关>`；`R7_LOAD_CAP=90`、`R7_ATTEMPTS=3`
（等不到安静窗口的轮次照跑，配对比值用全部 3 轮；记录的采样 load 0.9 ~ 2.0，门槛 2.0）。主口径是同轮配对比值 on/off 的中位与同向轮数（151 口径），
**3 轮低于 151 要求的 5 轮，且本机地板 17 ~ 43%（151 §1）**：|比值 - 1| < 10% 的格子一律视为不可判定。原始数据 `bench/r7rs/results/triage149-m20-<开关>.jsonl`（副本里，未入库）。

| 开关 | 基准（原任务） | on/off 配对中位（同向 / 轮） | 读数 |
|---|---|---|---|
| infer | mbrot / fib / array1（100–102） | x0.064 (3/3) / x0.171 (3/3) / x0.567 (3/3) | 大幅收益，方向与原任务一致 |
| host | mbrot / string（101） | x0.036 (3/3) / x0.960 (3/3) | mbrot 收益；string 在地板内 |
| num | mbrot / array1（102） | x1.004 (1/3) / x0.975 (3/3) | 不可判定 |
| joinraw | quicksort / nqueens / primes（108） | x1.008 (0/3) / x0.971 (2/3) / x0.992 (3/3) | 不可判定 |
| boxcache | nqueens / takl / primes（109） | x0.996 (2/3) / x0.931 (2/3) / x0.953 (2/3) | 不可判定（偏快方向） |
| boxdouble | sumfp / mbrot（注释里的 sumfp） | x0.261 (3/3) / x1.000 (2/3) | sumfp 收益与原注释"慢 4 倍"一致 |
| charbox | browse（142） | x0.938 (3/3) | 方向一致，量级在地板内 |
| loopweight | destruc（139） | x1.003 (0/3) | 不可判定 |

全表 1 轮冒烟（cs vs Clojure 编译器）与 G4 的说明见 cleat-180 的实施记录。
