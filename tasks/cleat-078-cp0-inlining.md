# cleat-078 过程内联与 let 传播（移植 skeg iron-070）

- 里程碑：后续　依赖：cleat-077　状态：完成
- 来源：skeg 提交 be8735b、287b0d8、8935b41（实现）与 db71f77、d40adef（测量记录）；`~/workspace/skeg/Skeg/tasks/iron-070-cp0-proc-inlining.md`

## 背景

cleat-075 背景里定下的移植顺序的第四步。iron-070 照 Chez cp0 的思路给编译器加用户过程内联：

- 新 pass `cp0-inline` / `simplify-lets`（`compiler/inline.sls`，在 `cp0-lite` 之后）：let / letrec / 库绑定的小 lambda（不超过 24 个 L1 节点，
  非递归、未被赋值）在调用处换成带新名字的 let 副本，常量与变量传播、删掉不再引用的绑定，再折叠一次。
- codegen：`vector-ref` / `vector-set!` 在调用处内联（界检查 + `ldelem.ref` / `stelem.ref`，否则调原过程）；letrec 绑定的局部过程
  直接调用子句方法（不经委托）；`operands!` 的暂存变量按值的类型声明。
- front：`(eqv? x 'sym)` 等改 `eq?`、测试位置的短 `memv` 展开成比较链；`(list e …)` 改嵌套 `cons`；letrec 分组时常量绑定并入 lambda 组。
- 运行时：`eqv?` 的快路径；`equal?` 的环检测用 eq 表原语（`$eq-table-ref` / `$eq-table-set!`）。

## 做法

三方合并（基 0d56486，theirs d40adef），11 个文件里 8 个无冲突。冲突与改写：

- `codegen.sls`：编译单元记录 cleat 多一个 `type-names` 字段，skeg 新增 `local-known`，两个都保留（构造调用多传一张 eq 表）；
  `operands!` 的暂存变量 skeg 按 `code-result-type` 声明类型，cleat-053 加过 `reload-cast`（object 暂存变量取回后重做 castclass）——
  合在一起：推断出类型的按类型声明，推断不出（object）的取回后仍重做 castclass。skeg 早先从 `collect-known!` 抽出的 `known-for-export`
  在 cleat 里补上（`collect-known!` 也改用它）。
- `equal.sls`：skeg 早先把合并等价类的代码拆成了函数，cleat 没有；只取实质改动（`hashtable-ref` → `$eq-table-ref`）。
- `inline.sls` 按 cleat 的中间语言改写（见下）；`fresh-name` 从 `loops.sls` 导出（skeg 在 `async.sls`）。

### 内联只用于 Scheme 代码

Clojure 代码与 Scheme 代码走同一条编译流水线（`compiler.sls`）。skeg 的内联只认 skeg 的 L1；cleat 的 L1 另有 `try-catch`（多子句）、
`escape-frame`、`try-handle` 与 Clojure 节点（`clj-…`）。照搬会出错：

- 引用计数（`count-refs!`）对不认识的节点不往下走：只在 `(clj-invoke f x)` 里引用的变量会被当成没人用，`simplify-lets` 把它的
  let 绑定删掉。现在各遍历都走进全部复合节点的子表达式（含 deftype* / reify* 的方法体——reify 的捕获由方法体算出）。
- 复制含 Clojure 的 `fn*`（名字是 `clj-fn-name` 记录、对应一个生成的类）或 deftype* / reify* 的体会重复类名；原始签名（`^long` 等，
  cleat-042）的 fn 内联后少掉参数的转换。所以 Clojure 的 fn* 不作候选、它的体里不内联，含 fn* / deftype* / def 的体不复制。
- letrec 局部过程的直接调用（`local-known-preset!`）假设过程类里有 `前缀 + "C<i>"` 的静态子句方法，Clojure 的 fn* 生成的是
  `AFunction` 的子类（`clj-fn-class!` 不用 preset），也排除。

测量开关照 skeg：`CLEAT_NO_070=1` 关掉内联与 let 传播，`CLEAT_INLINE_LIMIT=n` 调节点预算，`CLEAT_NO_070` 含 `letrec` / `call` / `list`
分别关掉常量并入 letrec 组、局部直接调用、`list` 改 `cons`。

## 测试

- 快照：新增 `inline-fold`、`inline-budget`、`inline-recursive-assigned`、`vector-ops`、`eqv-to-eq`、`letrec-const-run`；
  `letrec-forward`、`typed-lambda-args`、`let-lambda-labels`、`local-escape`、`library` 随之变化。与 skeg d40adef 的快照逐个比对：
  全部逐字相同（cleat 的程序集多一个 `(runtime scheme)`），只有 `library.lcil` 差 40 行——移植前就有的差别（skeg 在每个宿主调用处
  包 try 转换 .NET 异常，cleat 只在处理器一级转换，cleat-058）。`front-seed` 39/39；快照 88 个全过。
- base-library 224、r6rs-edges 151 全过；Chez mats `5_2.ms` 照样 `fail 0`。skeg 在 iron-070 之后把 `5_2.ms` 的 5 个循环表子句
  （filter / partition / remq）登记为 hang：它们靠 2 GB 堆上限在 10 秒内 OOM 报错而"通过"，机器负载高时来不及。cleat 同样登记（手工；
  以后用 `mkknown.py` 重新生成时要补回，因为日志里它们不是 FAIL）。
- 种子重生成，不动点。Scheme 镜像 2278912 → 2387456 字节（+4.8%，skeg +5.03%）。
- 微基准（`micro-ops.sps`，对 cleat-077 的构建交替四轮取最小值）：`assoc` 35 → 19、`apply` 40 → 24、`eqv?`（fixnum）12 → 7、`abs` 18 → 10、
  `string=?` 15 → 9、`guard` 不抛异常 78 → 56、`vector-ref` / `vector-set!` 9 / 12 → 6 / 8、`map` 53 → 42、`string-append` 52 → 39。
  `vector-fill!` 在这个脚本里 167 → 275，但单独计时两边都是约 280 ns（`$vector-fill!` 本身也一样）：`Array.Fill` 写对象数组走 GC 写屏障，
  代价随数组所在的代变，内联改变了前面各项的分配，测到这一项时的堆状态不同——不是 `vector-fill!` 变慢。
- Clojure 基准（`bench -r 3`）两边一致：Clojure 代码不参与内联。
- 全量：Scheme 255/255、Core 129/129、Host 247/248。失败的 `NuGetColdLoadTests.ExampleProjectBuildsServesAndDevelopsInteractively`
  查明是环境问题：dotnet 的本地工具解析缓存（`~/.dotnet/toolResolverCache`）按 包 + 版本 记安装路径、不看 `NUGET_PACKAGES`，指向我在
  cleat-074 手工验证时装在临时目录里的 `Cleat.Cli 1.0.4`（cleat-074 的构建，那个目录还在），`dotnet tool restore` 就不装 fixture 打的包，
  示例跑在旧镜像上，fixture 现编的 nrepl 包报 `StaleModuleException`。手工按 fixture 的步骤复现、删掉缓存后编译通过；fixture 改为每个
  一个 `DOTNET_CLI_HOME`（缓存随之隔离）并设 `DOTNET_NOLOGO`，`NuGetColdLoadTests` 4/4。
- 顺带发现：Clojure 镜像的 `.inl`（可内联子句的 fasl）两次构建不逐字相同（dll 与 `.ifc` 相同）；cleat-077 的构建上同样如此，不是本任务引入的，没有处理。
