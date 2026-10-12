# cleat-043 gen-class、proxy 全量与异步

- 里程碑：后续　依赖：cleat-033　状态：完成
- 设计依据：[03 §4](../designs/03-language-core.md)

## 工作内容
1. `proxy` 全量（基类方法重写、`proxy-super`）：发射器生成子类，方法表转发（与 stage0 的 `deftype` 做法相同）。
2. `gen-class`（ClojureCLR `genclass.clj` 的语义）与 `gen-interface`。
3. 异步：评估 ClojureCLR net11 的 `await*` 与 IronScheme 10 的 `AsyncFrame` 状态机，决定 cleat 的表面（`async-fn` / `await`）。

## 验收标准
- 各项有设计附录与测试；语料与基线不退化。

## 设计（2026-10-12）

方向（用户要求）：proxy 与 gen-class 按 CLR 平台本身的规则生成类型，而不是照搬 JVM 的形状。共同的做法：

- **真正的子类，编译期生成**。展开器的特殊形式（`proxy-class*`、`gen-class*`）在展开期用反射算出类型布局
  （`HostSyntax.ProxyLayout` / `GenClassLayout`，`src/Cleat.Scheme/Clojure/HostSyntaxClasses.cs`），编译器（codegen 的
  `proxy-class!` / `genclass-class!`）据此生成 Lcil 类型——编译器本身不反射，与 deftype* 相同。AOT 时类型进 `.clj.dll`，
  REPL / 源码加载时进该次求值的动态程序集；值为生成的 Type（`ClojureOps.DefineType` 登记，classForName 取最新的）。
- **按原签名覆写**。基类的 public / protected、virtual 且非 sealed 的实例方法（按签名取最派生的，被 new 隐藏的不算；
  属性、事件的访问器 get_X / set_X / add_X 也是方法）与抽象方法按原签名覆写，protected 覆写为 protected（Lcil 新增
  `(attrs protected)` → `MethodAttributes.Family`）；新接口的方法按原签名实现，与已有方法同名同参时显式实现
  （`接口.方法`，private + MethodImpl）。基类已实现的接口经它（可能已被覆写的）虚方法分派，不重新实现。
  `Object.Finalize` 不覆写（否则每个实例都进终结队列）。参数 / 返回含 by-ref、指针、ref struct 的方法：非抽象的不覆写，
  抽象的生成抛 NotImplementedException 的桩（CLR 要求实现全部抽象成员）；泛型方法定义无法覆写，报错。
- **值的转换**沿用 deftype*：值类型实参按 Clojure 的规则装箱（`BoxArg`，int → long），fn 的结果按声明的返回类型转换
  （`ConvertReturn` + unbox.any / castclass，void 丢弃）。超过 19 个实参经 `InvokeIFn`（applyTo）。
- **构造函数**：基类的 public / protected 构造函数按原签名转发（公开）。

### proxy

- `(proxy [基类? 接口…] [实参…] (名字 [参数…] 体…) …)` → `(construct-proxy (proxy-class* 基类 接口…) 实参…)` 再 `init-proxy`
  方法名 → fn 的映射（与 JVM 相同：一个名字的 fn 服务该名字的全部重载，按实参个数分派；首参为 this）。
  类型只取决于基类与接口，类名照 JVM 的 proxy-name（`命名空间.proxy$基类$接口短名…$哈希`），同一编译单元里只生成一次；
  运行时的 `get-proxy-class` 求值 `(proxy-class* …)` 并按基类缓存。
- 生成的方法：字段 `__clojureFnMap`（IProxy 的映射）按方法名取 fn（`ClojureOps.ProxyFn`），有则以 (this 实参 …) 调用，
  没有（或值为 nil，update-proxy 回到缺省）时调基类的实现（非虚 `call`）或抛 NotImplementedException（抽象 / 接口方法）。
  `init-proxy` / `update-proxy` / `proxy-mappings` 语义同 JVM；基类构造函数里调到的虚方法走缺省行为（映射还没设）。
- **`proxy-super`**：每个可覆写的基类方法另有公开的非虚入口 `proxy$super$名字`（同签名，直接 call 基类实现），
  `(proxy-super m a…)` 展开为 `(. this proxy$super$m a…)`。JVM 的做法是临时从映射里去掉该方法再调用、事后恢复——
  同一实例被并发调用时不安全，递归调用自己的 proxy-super 时也会错；这里没有这个问题。
- 原来的子集（基类为 Object 且无构造实参时改写为 reify*）去掉：全部走生成的子类，update-proxy 等对所有 proxy 可用。

### gen-class

- 宏展开为 `(gen-class* {选项})`（JVM 只在 `*compile-files*` 时生成、ClojureCLR 把每个类写进以类名命名的程序集）；
  cleat 不论是否 AOT 都在当前编译单元里生成：`(ns x (:gen-class))` 在 REPL / 源码加载时也得到可用的类型。
  ClojureCLR 代码里常见的 `(assembly-load "my.ns.MyClass")` 经 `assembly-load` 的桥接给出生成该类型的程序集
  （`Loader.FindDefinedType`；返回动态程序集不能经 AssemblyLoadContext.Resolving，所以放在 assembly-load 里）。
- 选项全部支持：`:name :extends :implements :init :post-init :constructors :methods（含 ^{:static true}）:main :factory :state
  :exposes :exposes-methods :prefix :impl-ns :load-impl-ns`。约定照 ClojureCLR：方法经静态 Var 字段（`名字__var`）找实现，
  同名多签名时先找 `名字-参数类型短名…`（`Type.Name`，无参为 `-void`）的 Var；未绑定时调基类实现或抛
  NotImplementedException；`:main` 生成 CLR 的入口签名 `static void Main(string[])`（-main 返回 Task 时等它完成）。
  `:methods` 的实例方法是 public virtual（可再被覆写）；`:state` 为 public readonly 字段。
- 类型初始化器 intern 各 Var（`Var.internPrivate`），`:load-impl-ns`（缺省真）时实现命名空间不在 `*loaded-libs*` 里就 require
  （不能看命名空间在不在——intern 已经把它建出来了；ns 形式在文件开头就登记 `*loaded-libs*`，加载中用到该类型不会重入）。
- 构造函数：缺省为基类的可访问构造函数；`:constructors {[参数类型…] [基类参数类型…]}` 时按它，`:init` 的结果
  `[[基类实参…] state]` 在调用基类构造函数之前算出（IL 允许在基类构造函数调用前不触碰 this 地计算），`:state` 在其后写入，
  `:post-init` 最后以 (this 实参…) 调用。ILVerify 通过。
- gen-interface / definterface 维持 cleat-053 的实现（`deftype*` + `:cleat/interface` → Lcil interface 类型）。

### 异步

评估：IronScheme 10 的 AsyncFrame（编译器把 async-lambda 改写成状态机，cleat-046 没有纳入）与 ClojureCLR 的 `await*`
（.NET 11 的**运行时 async**：方法带 `MethodImplAttributes.Async`（0x2000）标志、声明返回 Task<T>、体返回 T，体里调
`AsyncHelpers.Await`，挂起与恢复由运行时完成）。选后者——这是 CLR 平台自己的机制（C# 的 async 方法将来也编译成它），
编译器不必做状态机变换。实测 .NET 10 上它已经可用，但要环境变量 `DOTNET_RuntimeAsync=1`（runtimeconfig 设置无效）；
不支持时带该标志的方法使整个类型加载失败（TypeLoadException）。

表面照 ClojureCLR（代码可移植）：`(defn ^:async f …)`、`(^:async fn* …)`、特殊形式 `await*`，库 `clojure.clr.async.task.alpha`
（`async` `await` `result` `->task` `run` `delay-task` `completed-task` `task?` `wait-all` `wait-any` `wait-all-results`
`wait-any-result`）。实现（`AsyncSupport`）：

- ^:async fn 的每个子句体包成无参的体 thunk（`expand-fn*` 的 thunk? 模式），子句返回 thunk 的 Task。
- **运行时 async 可用时**（`AsyncSupport.RuntimeAsync`：探测能否定义带 async 标志的方法；`CLEAT_RUNTIME_ASYNC=0` 强制退化）：
  thunk 的体编进 `invokeAsync`（返回 `Task<object>`、`(attrs async)`），`invoke` 转发；`await*` 生成
  `ToTask`（Task / Task<T> / ValueTask / ValueTask<T> → Task）→ `AsyncHelpers.Await(Task)` → `ResultOf`（Task<T> 的值、
  失败时抛出原来的异常）。方法里不发 tail.、不做局部变量清除。
- **否则退化**：thunk 是普通 fn，同步运行，值包成已完成的 Task、异常包成已失败的 Task（与 async 方法一致，不同步抛出）；
  `await*` 同步等待。结果相同，只是等待时占着线程。
- `await*` 只能出现在 async 体里、且不在 catch / finally 里（编译期报错，文本同 ClojureCLR）：展开器在上下文的局部变量表里
  放标记（async 体 / catch·finally 区域），fn* 与 deftype 方法体去掉标记——嵌套的 fn 不是 async 的。recur 不能跨过体 thunk。
- 用运行时 async 编译的 AOT 程序集要在支持它的运行时上加载。

实测（.NET 10，`DOTNET_RuntimeAsync=1`）：100 个各 await 20 ms 的 ^:async 调用并发完成约 26 ms（5 个线程池线程），退化实现
顺序等待约 2 s；await 之后在另一个线程上恢复。

### 配套改动

- **局部变量的类按初值推断**（JVM 的 LocalBinding 取 init 的 javaClass）：没有提示的 let 局部，初值为 `(new T …)`、`(T. …)`
  或带 `^T` 提示时取 T（只取引用类型；loop 局部不推断）。于是 `(let [e (ExampleClass.)] (.foo e …))` 能静态决议。
- **静态决议的重载选择**（非 clojure.lang 类型，按 C# 的做法）：多个重载相容时取形参与全部已知实参类型逐一相同的那一个——
  只在静态类型未知的位置上各重载的形参都相同时（运行时实参类型不会改变选择）。实参类型新增：`(int x)` 等返回
  int / bool / char / String 的静态宿主调用、折叠成的 Int32 常量。gen-class 测试的 `(.foo example o (int 1))` 因此选
  `foo(Object, Int32)`（与 JVM 一致；cleat 的 `(int 1)` 装箱为 long，运行时反射只会选 Object 的重载）。
- **`await` / `await-for`**（clojure.core）：发给 agent 的动作改为 `clojure.lang.AwaitAction`——值不变、watch 照常收到通知，
  但在该动作整个完成（watch 通知、错误登记）之后才放行等待者。JVM 在动作函数里就 countDown，等待者醒来时 agent 线程可能
  还在为这个动作通知 watch，刚加的 watch 会收到这次 old = new 的通知（cts 的 add-watch "watch agent" 因上面的推断改变了时序
  而间歇失败，HEAD 0/10、改动后 5/10；这里从根上去掉竞争）。`send` 会把动作包一层（binding-conveyor-fn），所以动作在 agent
  线程上运行时登记自己，Agent 在动作结束时放行。
- cts.clj：`clojure.test-clojure.genclass` 先加载辅助命名空间 genclass.examples（ClojureCLR 先 AOT 编译它）；
  `clojure.test-clojure.clr.async.task` 不再跳过。

## 结果

- test_clojure：genclass 由加载失败变为 5 个测试 62 断言全过；clr.async.task 13 个测试 21 断言全过（两种实现都过）；
  java-interop 错误 4 → 0（通过 238 → 244）；pprint 错误 124 → 0（通过 349 → 471，余 3 个失败：读入器列号、promise 的
  reify 类名、double 的 `E+18` 格式，都不是 proxy 的问题）；printer 错误 12 → 0；repl 错误 4 → 0（余 2 个失败：从源码加载
  的 defn 的 `:line` 为 0，source-fn 读到 ns 形式）。基线已更新。
- 语料：8 条因 pprint 的 proxy 记为 `:not-yet` 的分歧变为通过，删除；`get-proxy-class throws`（jolt 的桩值，参考实现返回
  Object 的代理类）记为 `:corpus/jolt-value`。
- 测试：`tests/Cleat.Host.Tests/ClassGenerationTests.cs`（proxy、gen-class 源码与 AOT、类型先于命名空间加载、
  async 的两种实现与编译期错误）；ILVerify 通过（含 AOT 的 proxy / gen-class 类型）。
- 基准：提交时机器负载约 20（其他项目的测试与构建），两次测量互相差到 30%，无法与第 5 项后的数字比较——待空闲时复测
  （局部变量的类推断与重载选择改变了 clojure.core 的部分调用点）。
- 未做：gen-class 的 CLR 特性（attribute）与 by-ref 参数（报错）；proxy / gen-class 覆写返回 Task 的方法时不自动成为 async
  方法（实现里用 `(t/async …)` 返回 Task 即可）；顶层的简单形式在 REPL 里求值时不经静态决议（与以前相同）。
