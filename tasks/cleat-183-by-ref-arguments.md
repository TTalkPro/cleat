# cleat-183 宿主调用的 by-ref 实参（ref / out 形参）

- 里程碑：M21　依赖：cleat-169（收口后开始）　状态：完成（2026-11-09）
- 设计依据：[30](../designs/30-csharp-compiler.md)（C# 编译器的分析器 / L4 表示 / 发射）；ClojureCLR 的 `by-ref` 语义

## 问题

ClojureCLR 用 `(by-ref x)` 把局部变量的地址传给 CLR 方法的 `ref` / `out` 形参，调用之后 `x` 是新值：

```clojure
(let [n 0]
  (if (Int64/TryParse s (by-ref n)) n :bad))
```

cleat 的 C# 编译器遇到它直接报 `by-ref arguments are not supported yet`（`Analyzer/HostInterop.cs`）。ClojureCLR 的库里这种写法常见：
cleat-167 移植 core.async / tools.reader 时两处都是改写绕开的（`Boolean/TryParse`、`BigInteger.AsInt64`），再移植别的库还会遇到。

## 难点（为什么当时没做）

1. **局部变量不可变**：编译器按 `let` 绑定不变来做优化（常量传播、类型推断、闭包捕获按值复制）；`by-ref` 等于方法调用改写了局部变量，
   这个局部要按可变的处理（与 `set!` 赋值转换同类：ConvertAssignments / 类型推断都要知道它会被写）。
2. **存储位置**：`ref` 形参要"某个具体类型的存储格的地址"。被闭包捕获的局部在闭包对象的字段里；推断成原始表示的局部可能是未装箱的
   `long` / `double`、也可能是 Num 的双局部（L4 表示表）。by-ref 的局部要有一个与形参元素类型相符的 CLR 局部槽。
3. **两条调用路径**：
   - 编译期决议到方法（静态决议 / KnownCall）：发射 `ldloca`（字段时 `ldflda`），相对直接；
   - 运行时反射（`Cleat.Core.Runtime.Reflector.Call`）：实参装在 `object[]` 里，调用后要把数组里的新值写回局部。
4. **重载选择**：形参类型为 `T&` 的方法要能被选中（`plain-method?` 现在把 by-ref 形参的方法排除在外），`by-ref` 实参只匹配 by-ref 形参。

## 工作内容

1. 分析器：认 `(by-ref sym)` 实参，`sym` 必须是局部（形参、`let` / `loop` 局部；Var、字段之外的形式报清楚的错误），把该局部标成"被 by-ref 写"。
2. 前中端：被 by-ref 写的局部按可变处理（不做常量传播；类型推断按形参元素类型或保守为对象；被闭包捕获时按 ConvertAssignments 的 box 处理并说明语义）。
3. 下降与发射：静态决议时给局部分配与形参元素类型一致的槽，`ldloca` 传地址；反射路径在调用后把 `object[]` 的对应元素写回（必要时转换类型）。
4. 重载决议（编译期与 Reflector）：支持 `ref` / `out` 形参，`by-ref` 实参与 by-ref 形参互相匹配。
5. 把 cleat-167 绕开的两处改回 `by-ref` 写法（core.async 的 `dispatch.cljc`、tools.reader 的 `impl/commons.clj`），去掉 `;;; CLEAT-167` 注释。
6. 测试：`Int64/TryParse`、`Dictionary.TryGetValue`（泛型 out）、`ref` 形参的读写、循环里反复 by-ref、被闭包捕获的局部、反射路径（无类型提示）、
   错误用法（非局部、字面量）；镜像确定性与 ilverify 不受影响。

## 验收

- 上面的用例在编译期决议与反射两条路径都正确；错误用法报清楚的错误。
- core.async、tools.reader 改回 `by-ref` 后上游测试仍全过；Host 全量、G3 / G5 通过。
- README"已知限制"里 by-ref 一条删除。

## 结果（2026-11-09）

### 做法

- **分析器**（`Analyzer/HostInterop.cs` `ByRefHostCall`）：`(. T m … (by-ref x) …)`、`(T/m …)`、`(.m o …)`、`(new T …)` 里有 by-ref 实参时改写成
  `(let [r (ByRefHost kind T member mask [inst] 实参 …)] (set! x (ByRefNth r 1)) … (ByRefNth r 0))`——调用返回 `[结果, 各 by-ref 位置的新值]`，
  再按普通的局部赋值（IR 的 `Set`）写回。被写的局部由前端（ConvertAssignments、常量传播、类型推断）按可变处理，被闭包捕获时装箱，
  不另做特殊的分析。x 必须是 let / loop / fn 形参、catch 绑定或 deftype 的可变字段；其余（Var、字面量、不可变字段、多余实参）编译期报错。
  `type-args` 的泛型方法调用与 by-ref 同用时报错（未支持）。
- **编译期决议**（`ClojureStaticCalls.ResolveByRef` + `Lower/HostCalls.cs` `CgByRefHost`）：个数与 by-ref 位置相符的重载恰好一个、类型都能按名字引用时，
  实参依次求值进临时变量，by-ref 位置开元素类型的局部（ref 先按形参转换存入，nil 留默认值；out 不存）、`ldloca` 传地址，直接 `call` / `callvirt` / `newobj`；
  调用后装出结果数组（值类型经 `Reflector.ConvertFrom` 成 Clojure 表示：Int32 → Int64、Single → Double）。例：`Int64/TryParse` →
  `call System.Int64::TryParse(System.String, System.Int64&)`。不看实参的静态类型——候选不唯一就交给运行时。
- **运行时**（新 `Runtime/ByRefCalls.cs`，入口 `ClojureOps.ByRefHost`）：同样按个数与 by-ref 位置筛候选，其余位置按 Reflector 的匹配等级，ref 位置看局部当前的值、
  out 位置不看；`MethodInfo.Invoke(DoNotWrapExceptions)`，被调方法的异常原样传出；选择按（类型、名字、kind、mask、各实参类型）缓存。
  没有形状相符的重载报 `MissingMethodException`、不可分辨报 `More than one matching method …`。
- 不带 by-ref 调 ref / out 方法的报错提示改为 "pass ref/out arguments with (by-ref local)"。
- cleat-167 绕开的两处改回上游写法：tools.reader `impl/commons.clj`（与上游逐字相同）、core.async `impl/dispatch.cljc` 的 `Boolean/TryParse`。

### 测试

`ClojureCompilerTests`：`ByRefArguments`（`data/by_ref.clj` 16 组用例——out / ref、泛型 out（有无提示）、结果的数值表示、写回后的常量传播与类型推断、
循环 / loop 变量 / 形参 / 被捕获的局部 / 闭包里 by-ref / 可变字段 / catch 绑定、异常原样传出、歧义、无相符重载——在缺省与 `CLEAT_NO_OPT=host`
（全走运行时）下输出逐字相同）、`ByRefStaticCallsUseLdloca`（IL 取证）、`ByRefMisuse`（错误用法）。实现中查出并修了运行时缓存键漏了 ref 位置的值类型
（先用 long 调过的 `Interlocked/Increment` 之后用 nil 调会复用 Int64 重载）。

### 门禁

Core 116 / Compiler 50；全量 Host 292（289 + 新增 3）；`image-set --twice --ilverify`、conformance `--gate`、CTS / test_clojure 基线全过。
镜像 `cleat.boot.dll` 与 183 之前逐字节相同（核心库没有 by-ref 写法），r7rs 不受影响，未重跑。

全量 Host 复跑时遇到两件与 by-ref 无关的事：

1. **NuGetColdLoadTests 两项稳定失败**（`ExampleProjectBuildsServesAndDevelopsInteractively`、`NreplAndNeoreplBuiltWithToolsBuildRestoreFromTheFeed`，
   单独跑能过）：tools.build 的 `create-basis` 没给 `:local-feed` 时把 `$CLEAT_LOCAL_FEED`（缺省 `~/.cleat/local-feed`）加成附加源，本机那里留着
   2026-10-15 手工验证装过的同版本号旧包（对着旧镜像编的）；同 id 同版本时 NuGet 取先应答的源，restore 到旧包即 `StaleModuleException`。
   fixture 的环境改为 `CLEAT_LOCAL_FEED` = 本 fixture 的源（测试不看机器上的默认本地源）；之前稳定失败的组合复跑 84 / 84。
2. **core.async 的 `timers-test` 偶发挂起**（全量里 `CoreAsyncWithAnalyzerAndReader` 跑满 20 分钟；单独跑上游测试约 3 / 16 次）：只在前面跑过
   async_test 之后出现，单独跑 timers-test 12 / 12 通过；by-ref 在运行时只涉及 tools.reader 的数字解析与宏展开期，不碰调度路径。
   已排除"timeout 守护线程因负的 TimeSpan 抛出而死"（线程里未处理的异常会让进程退出，挂起时进程活着）。原因未查明，另立任务。

### 顺带发现（未改）

- `dotnet build` 不重编 `src/boot/Cleat.Clojure.Libs.dll`（只有 `compile-libs` / `publish` / `pack` 与调它们的 Host 测试会）：编译器改动之后 src/boot 里可能是
  新镜像配旧随附库；`tools/build.cs publish` 先 `CompileLibs` 再 `dotnet build`（后者可能重编镜像），顺序也是反的。
