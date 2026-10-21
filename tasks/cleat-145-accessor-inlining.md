# cleat-145 小访问器的跨编译单元内联

- 里程碑：M16　依赖：cleat-144　状态：完成（2026-10-21，代码全落地但**默认关闭**：开关 `accinline` 是 opt-in，
  要 `CLEAT_OPT_ACCINLINE=1` 才开 —— 111 的同轮配对 A/B 显示开侧只是中性、browse 仍 5/5 同向慢 5.8%，
  没有一项达到可声明的收益，按"没有收益就不进默认路径"的纪律关成默认，见结论 9 与 [notes M16 §8](../notes/r7rs-performance.md)；
  第 1 条的"扩展到镜像"半段明确不做，见结论 6）
- 设计依据：[28 §2.3](../designs/28-hot-path-optimizations.md)

## 问题

归因实验 3：JVM 关内联（`-XX:-Inline`）后 browse / scheme / peval / array1 掉到 cleat 的 ±20% 量级
——pair 密集项 3 ~ 5x 差距的主体是 C2 把小访问器与包装调用内联掉。cleat 侧：defn 包装比接口直调
贵约 2 ns（3.4 vs 1.3），JVM 为 0；`register-eval-inlines!` 只在 eval 单元注册内联接口文件
（driver.clj 对 `:boot` / `:package` 关闭），r7rs 普通 ns 的 `mcar` / `mcdr` / `set-mcar!`
一类访问器都不会被内联。`not` 的 `:inline` 曾让 tak 3.7 → 1.9 s，这层收益在 r7rs 尺度已被验证。

## 工作内容

1. 内联接口文件扩展到镜像 / 包构建：解决"内联决定跨自举阶段必须稳定"的反馈问题
   （driver.clj 现注释的原因），必要时按固定点迭代或把内联决定与阶段解耦。
2. 内联对象：deftype 字段读 / 写访问器、小 defn 包装（体量阈值 + 无副作用判定），
   命中后发字段直读 / 直存，绕过接口调用。
3. 与 `restrict-loop-inlines?`（cleat-120）协调：循环体内非 core 用户函数的内联限制不回退
   （array1 的 tier-0 活性教训），访问器类内联需单独评估是否豁免。
4. `CLEAT_NO_OPT` 开关（如 `accinline`）保留旧行为。

## 验收

- 种子不动点：内联决定变化后 stage2 == stage3，BuildFromTheSeedReproducesTheSeed 通过。
- 语义用例：访问器内联开 / 关输出逐字相同（含中途重定义 Var、异常顺序、副作用次数）。
- 同窗口 A/B：browse / peval / scheme / destruc 等 pair 密集项可测收益；array1 不回退
  （对照 cleat-120 的结论）；门禁与两套基线不变。

## 结论（2026-10-21）

1. **做的两件事**：(a) eval 单元与包构建各扫一遍顶层 L0，把 deftype 里"方法体就是一个字段引用"与
   "存一个字段并返回同一个传入值"的接口方法记成 `[接口 方法 实参个数] → {:class :field :kind}`
   （`driver.clj` 的 `eval-accessors` 原子表 + `register-eval-accessors!` / `accessors-of` / `merge-accessors`，
   同一个 key 出现两个不同实现就**删掉该项**，调用点保留接口调用）；(b) 给包装 defn 生成 `.inl` 子句时
   （`inline-clauses` 多收一个 `accessors`）把整个体就是那一个 `CallInstance` 的子句换成
   `(if (IsInstanceOf 'T o) (GetInstanceFieldDirect 'T 'f o) 原接口调用)`，写侧是 `(SetInstanceField 'T 'f o v)`
   （**读侧最初复用的是通用的 `GetInstanceField`，第一轮 A/B 因此整体回退，见结论 4；现在是专用节点**）
   （`codegen.clj` 新增 `cg-set-instance-field!`：按源顺序压接收者、castclass、压值、stfld 后 ldloc 返回原值，
   `^long` / `^double` 字段复用 `PrimArgLong` / `PrimArgDouble` 入口转换，决议不出字段时整体退回
   `ClojureOps.SetFieldOf` 反射）。
2. **识别上的四个形状坑**（都写进 `driver.clj` 注释了）：deftype 方法体的 named-let 外壳在**零实参**方法里
   `call` 只有两项（旧代码写死 `= 3` 让读访问器全部落空）；setter 的体是
   `(begin (call (lambda false (clause (tmp) false (begin (set! FIELD tmp) tmp))) value) value)`
   且必须校验"返回的是传进来的那个形参"；包装子句里 `CallInstance` 的接收者是**内层 letrec 形参**而不是子句形参，
   所以 `peel-named-let` 只在实参是纯转发符号时剥层建改名表，且 `resolve` 要沿改名表**多跳**走到本子句形参才算
   "同一个表达式、求值一次"（停在中间层符号是自由变量、停在非符号表达式会被守卫与直读各求值一次，两种都不特化）；
   `cl` 是 `np/unparse` 出的普通 list，不能 `assoc`（改成 `vec` + `assoc` + `apply list`）。
   另两个坑：写访问器一度把字段**下标** over 成字段名，`nth` 用字符串下标直接 InvalidCastException；
   docstring 里的 ASCII `"` 会破坏 reader（改用「」）。
3. **守卫失败一律走 else 分支的原接口调用**：nil 接收者、`reify` 的另一个实现、同接口的另一个实现、
   同名重定义 deftype 后按名决议到另一个类 —— 异常文本与求值次数都不变（`accinline_semantics.clj` 的 17 项 +
   新用例的退化路径探针逐条核过开 / 关逐字相同）。**volatile 字段的写不做特化**（写侧没接 `stfld` 的 `volatile.`
   前缀），volatile 字段的**读**特化（直读带 `(volatile.)` 前缀，用例有取证）；prim 签名包装的子句不特化。
   特化把守卫多包一层可能超内联上限：
   先算特化子句、超上限就**退回未特化子句**，绝不因特化失败把 cleat-144 已有的内联整体丢掉。
4. **读侧第一次整体回退，根因是复用了通用发射器**（本轮真正的教训）：特化子句最初发的是
   `(GetInstanceField 'T 'f o)`，第一轮 A/B（`m145-ab`）10 项里 browse x1.538（0/5 同向）、destruc x1.215（0/5）、
   lattice x1.034 / wc x1.021 全偏慢，中位几何平均 x1.068 —— 比"没收益"更糟。查 IL 找到根因：通用的
   `GetInstanceField` 为 object 接收者要补 `ldstr + Instance`（即 NotNil 检查）、结果是 `System.Object` 还要过
   `RetObject`，而守卫成立时接收者必然非 nil，这两笔纯属白付，比原来的接口调用还贵。改成专用节点
   `GetInstanceFieldDirect`（`driver.clj` 的 `specialize-accessor-clause` 发新节点，`codegen.clj` 新增
   `cg-get-instance-field-direct!`：`castclass` 接收者 + `ldfld`，**不发 NotNil、不发 RetObject**，
   volatile 字段照旧带 `volatile.` 前缀；决议不出字段时整体退回通用 `GetInstanceField`），配套三处：
   `arith-only-calls` 认它（否则被当成有副作用的调用）、`host-call-layout` 认它的字段类型
   （`^long` 一类字段的原始 / 装箱用途判定要拿得到类型）、派发 cond 在 `SetInstanceField` 之前加一条。
   `cg-static-host-call!` 因此多收第 4 个参数 `guarded?`（只有在守卫内才省 NotNil / RetObject），其余调用点
   一律传 `false`，默认行为逐字不变。有意不动的一处：cleat-123 的标量替换改写只认 `field-read-of` →
   `GetInstanceField`，新节点**不进那条路径**（扩大改写面的风险与收益不对称），所以守卫直读不会再被消一层。
   测试随之加读侧取证：`isinst` 与后面的 `ldfld` / `stfld` 之间不得出现 `Instance` / `RetObject`
   （实测 28 段、0 段违规）。修复后 browse 回到 x1.058、destruc 回到地板内（见 9）。
5. **包路径**：`*compile-files*` 为真时依赖包入口不执行，固化的 `.inl` 因此没登记过；新增
   `PackageIndex/PrepareNamespaceInterface(ns)` 只按命名空间载入依赖程序集并登记它嵌入的 `.ifc` / `.inl`（不跑入口），
   `analyzer.clj` 的 `inline-target` 在查表前问一次。**只在包构建期问**（判据：驱动线程局部状态里绑着 `:package`）——
   eval / 加载期按需载入依赖程序集会让本单元直接链接依赖包的函数、第一次用到就跑那个包入口开头的镜像核对，
   主机进程的镜像与打出去的 Cleat.Cli 包里的镜像不是同一份时抛 StaleModuleException，
   把一条与内联无关的加载整个打断（111 上 `examples/WebServer` 的 neorepl 热替换与 tools.build 跑 neorepl 两条用例实测）。
   顺带记下：这两条 Bootstrap 用例本身是**筛法敏感**的 —— 同一个 main 构建，跑整夹具 4 条只输 Sqlite 那条，
   换 `--filter` 只挑这两条时 main 也失败，所以判定必须用同一种筛法做对照。
6. **明确不做**：镜像构建仍然 `inline-calls? = false`，内联决定不进种子（cleat-144 的口径，
   `BuildFromTheSeedReproducesTheSeed` 要逐字节一致）——所以工作内容第 1 条的"扩展到镜像"这半段留着；
   r7rs 的 eval 与包两条路径不受影响。直接 `.getA` 的调用点（没有 defn 包装、不走 `.inl` 的那一类）本任务不动，
   交 cleat-153 的调用点类型反馈。与 `restrict-loop-inlines?`（cleat-120）的协调：访问器特化只发生在
   **被内联的包装函数子句内部**，不新增循环体内的用户函数内联，因此不需要额外的豁免评估；array1 实测未回退（见 9）。
7. **种子**：改了 `src/clojure` 必须重生成 —— 为这个开关一共走过五次不动点（145 初版 218 s、加包构建门控后
   215 s、读侧修复与 `guarded?` 重构各一次、口径改默认关再一次），最后一次在 111 上 `clojure-seed` 用 **205 s** 到不动点，boot /
   compiler 六个产物 stage2 == stage3 逐字节 identical（`cleat.boot.dll` 2074624、`cleat.boot.lcil`
   18421969、`cleat.boot.ifc` 134061、`cleat.boot.inl` 742861、`cleat.compiler.dll` 3279872、
   `cleat.compiler.dll.lcil` 24554978），再"从种子发射回来"也 identical。进库的是 `cleat.compiler` 的
   `ifc` / `inl` / `lcil` 三个种子（10907 / 42720 / 1244051 B gz，boot 那三个不变）；
   md5 本机与 111 逐一核对：`ifc` 454e70d9ef6065152764e33177e30937、`inl` 9f613de7225dd380afc2e29541e006f4、
   `lcil` 7bab92a4e51f3e3d7ae74d00ad387d4d。变的只是编译器包自身 `.inl` 子句的形状与 lcil 里的新派发 ——
   内联决定不进种子（口径改动只改一个环境变量的判定），这正是种子能跨自举阶段稳定的原因。
8. **验证**（全部在 192.168.3.111 的最终构建上 —— 默认关的口径改动只改 `analyzer.clj` 的一个判定，之后重生成种子、
   重新构建，全套重跑一遍）：Release 构建 0 error（1 warning，既有的）；
   `AccessorInliningCrossCompileUnits` + `BuildFromTheSeedReproducesTheSeed` **Passed 2 / 2（23 s）**；
   Cleat.Core.Tests **123 / 123**；Cleat.Host.Tests 全套 285 条 **283 通过 / 1 失败 / 1 跳过（24 m 42 s）**，
   唯一失败是 `ConsoleExampleQueriesCsvWithSqlite`（远端 NuGet 离线源里没有 `Microsoft.Data.Sqlite`，与 145 无关，
   main 对照同样只输这一条）。**上一轮那两条 Bootstrap 失败这次不复现**：`ExampleProjectBuildsServesAndDevelopsInteractively`
   与 `NreplAndNeoreplBuiltWithToolsBuildRestoreFromTheFeed` 的根因确认是 10-18 留下的 `/tmp/cleat-nuget-f5d2cbc6`
   旧缓存 —— 种子换代后 boot image id 变了，缓存里的包程序集是按**上一代镜像**编的，跑到入口开头的镜像核对就抛
   StaleModuleException；把那个目录移走后就干净了（判"我改坏了没有"之前先清这类缓存，见 28 §6）。
   `conformance --run --gate` → `gate: 521 classified divergences, 521 failing rows`、**`gate: OK`**；
   `cts cts` → `247 namespaces / tests 247 / 断言 6868 通过 / 29 失败 / 6 错误`、**`baseline: OK`**；
   `cts test-clojure` → `54 namespaces（31 skipped、12 not loaded / timed out）/ tests 384 / 3877 通过 / 25 失败 / 4 错误`、
   **`baseline: OK`**（144 写回的那份基线逐字相同）。
   开侧 IL 抽查（`/tmp/acc-on.lcil`，202815 B / 69 单元）快路径就是四条：
   `(isinst "accinline.gen.Foo, eval$c4") (ldnull) (cgt.un) (brfalse else)` → `(castclass …) (ldfld "a")`，
   else 分支才是 `ldstr "getA" + Instance + callvirt IFoo.getA`；`volatile` 字段带 `(volatile.)` 前缀，
   `^long` 字段头发 `(ldfld …) (call LongBox "Get")`；守卫→字段指令的配对段共 28 段，0 段里出现 `Instance` / `RetObject`。
   A/B 没有为默认关重跑：默认 = 关这条由用例的**默认臂取证**（默认设置的 eval IL 里一段守卫直读都没有）
   与语义用例逐字相同锁住，开臂读数就是 9 里的 `m145-ab2`。
9. **A/B**（111，两臂同轮交替 × 5 轮 × 10 项，`R7_MAXLOAD=1.2` / `R7_LOAD_CAP=1200`，0 条 loaded；
   原始数据 `bench/r7rs/results/m145-ab.jsonl`（读侧修复前）与 `m145-ab2.jsonl`（修复后））。
   汇总要显式带 `R7_PAIR_MAXLOAD=1.5`：111 只有 4 vCPU，`R7_MAXLOAD` 的缺省门槛是核数 x 25% = 1.0，
   而跑分时采样 load 常在 1.0 ~ 1.4，用缺省门槛会把绝大多数配对筛掉、每项只剩一两个格子（本轮踩过，
   `m16sum.py` 已修：`R7_PAIR_MAXLOAD` 可另指配对门槛，筛空时退回全部配对并在格子上打 `*`）。
   on/off 同轮配对比值中位（<1 = 开优化更快，括号 = 同向 <1 的轮数 / 总配对）：

   | 基准 | 修复前 m145-ab | 修复后 m145-ab2 |
   |---|---|---|
   | browse | **x1.538 (0/5)** | **x1.058 (0/5)** |
   | destruc | **x1.215 (0/5)** | x0.970 (3/5) |
   | lattice | x1.034 (0/5) | x0.986 (3/5) |
   | wc | x1.021 (1/5) | x1.020 (1/5) |
   | maze | x1.011 (2/5) | x0.974 (4/5) |
   | string | x1.013 (1/5) | x1.000 (2/5) |
   | array1 | x1.009 (2/5) | x0.992* (3/4，1 对被 load 筛掉) |
   | peval | x1.002 (2/5) | x1.002 (1/5) |
   | scheme | x0.984 (4/5) | x1.021 (1/5) |
   | conform | x0.962 (3/5) | x0.996 (3/5) |
   | **各项中位的几何平均** | **x1.068** | **x1.002** |

   照实写的结论：读侧修复把 browse 从 +54% 压到 +5.8%、把 destruc 拉回 3% 地板内，整体到**中性**；
   但**没有任何一项跑出可声明的收益**，browse 仍是 5/5 同向 +5.8%（> 地板）= 一条可声明的残余回退；
   array1 未回退（cleat-120 那条关注点过关）。收益为零的机制解释：这批基准里的热访问器多是**直接 `.getA`
   调用点**（没有 defn 包装、不走 `.inl`），本任务的特化只在包装函数被内联时生效，碰不到那一批；
   生效的点只多出一笔 `isinst` + `castclass` 的分支成本。要吃到裸调用点是 cleat-153 的调用点类型反馈。
   → 口径据此定为**默认关（opt-in）**：`CLEAT_OPT_ACCINLINE=1` 才开、`CLEAT_NO_OPT=accinline` 仍可单独关
   （两个都设时关优先）；`bench/r7rs/m16full.py` 的 `DEFAULT_NOPT` 去掉 `accinline`（默认关的开关不进 150
   的关侧名单），归因它时反过来用 `CLEAT_OPT_ACCINLINE=1 ARMS=cleat-on,cleat-off NOPT=accinline`。
   browse 残余的 +5.8% 只记账不再改发射（守卫不命中时多一次判断、命中时也多一次分支），
   153 之后若要重开这项，先按 [notes M16 §8](../notes/r7rs-performance.md) 的配方复测。
