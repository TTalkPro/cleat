# cleat-104 无提示已知函数的类型化入口

- 里程碑：M10　依赖：cleat-103　状态：完成（2026-10-15）
- 设计依据：[20 §5](../designs/20-skeg-optimizations.md)；skeg iron-075 / 076

## 工作内容
1. 签名推测：单元内直接链接的顶层 `defn`（单固定子句、≤ 4 形参、体不大、无副作用 / 分配 / 组外调用），形参与返回值各推 `{long, double, obj}`，组内乐观不动点。
2. 生成：`invokeStaticPrim` 形的 `$t`；通用入口实参匹配时转调；类型已知的调用点直调（复用 `PrimInvoke` 路径）。
3. long 溢出抛异常，不做哨兵 / 重算 / 计数；不做过程级 Num 签名。
4. 开关 `CLEAT_NO_OPT` 含 `typed`；语义用例；IL 快照。

## 验收标准
- `bench/fib`（无提示）接近 `prim_fib`；基线、门禁、全量不变；种子不动点。

## 结果（2026-10-15）

### 实现（codegen.clj：speculate-entries，Cleat.Core.Runtime.TypedBailout，开关 `CLEAT_NO_OPT=typed`）
- 候选：`(clj-def v (fn …))`，单固定子句、1 ~ 4 形参、没有闭包字段、没有 `^long` / `^double` 原始签名；体是纯的——只有 Numbers 的运算与
  `Util.equiv / equals`、if / let / begin / label / jump、经 Var 的自调用，不超过 200 个节点，且至少有一次自调用（非递归函数的通用入口只多一次检查与 try，
  第一版把 `inc` / `dec` 也特化了，去掉）。形参是否"数值"按它（或 arity 循环里它的副本）是不是 Numbers 运算的操作数。
- 推测：依次试 long → long、long → double、double → double；类型化的一份体（绑定换新名，自调用换成 `(clj-call "TypedSelf" '[签名] var 实参…)`，
  expr-type 是签名的返回类型）在形参固定类型下试算推断，体的返回类型（含 label，`spec-body-type`）是猜的类型、自调用的实参也是，才成立。
- 生成：`invokeStaticTyped(self, T…) → R`（类型化体，形参原始局部，尾位置原始返回）；通用入口 `invokeStatic` 的体包成 `(clj-call "TypedEntry" …)`：
  实参 `isinst` 相符时在 try 里转调并装箱，捕获 TypedBailout 时落回通用体；否则直接走通用体。
- 自调用（`TypedSelf`）：取 Var 的值；是本函数的类（`isinst`）就 `call invokeStaticTyped`，否则经 `IFn.invoke` 调用、结果按签名取回
  （`TypedBailout.Long / Double`：不是推测的类型时抛 TypedBailout，通用入口用通用体重算——体是纯的，重算安全）。Var 的重定义照常生效。
- long 溢出照常 `Numbers.add(long, long)` 抛异常（与通用体相同），没有 skeg 的哨兵、`$g` 重算与计数；不做过程级 Num 签名。

### 测量（linux01，Release）
| | 关（`CLEAT_NO_OPT=typed`） | 开 | `^long` 提示（pfib） |
|---|---|---|---|
| fib(32) | 82 ms | 13 ms | 23 ~ 25 ms |
| fibfp(30.0) | 41 | 5 | — |
| bench fib（fib 25，3 轮 min） | 4 | 0（< 1 ms） | prim_fib（fib 27）2 |

- 验收"fib 接近 prim_fib"：超过——有提示的自调用经 Var 取值、castclass 到 `primifs.LL` 再接口 callvirt，类型化入口是 isinst + 静态 call。
- boot 镜像不变（核心库里没有满足条件的递归纯数值函数；种子的 boot 部分与 cleat-103 逐字节相同），编译器包种子重新生成。
- 语义：`tests/Cleat.Host.Tests/data/typed_semantics.clj`（long / double / Ratio / BigInt / BigDecimal / Int32 / 字符串 / nil 实参，long 进 double 出，溢出，
  `=` / `zero?` / recur，unchecked-math，重定义：新函数返回 long（不退回）、double（TypedBailout 后通用体重算）、关键字（报错与通用体相同），
  经 map / apply / partial 调用）开 / 关输出逐字相同；`ClojureCompilerTests.TypedEntries` 另查 fib 的 `invokeStaticTyped(Int64) → Int64` 与体里的
  `Numbers.add(long, long)`，`^long` 提示的与有副作用（println）的不特化。
- 验证（linux01）：种子不动点；Core 117 / Host 258 通过（1 个按环境跳过：ilverify）；语料门禁通过（522 行）；两套基线不变（cts 6866/31/6、test_clojure 3688/18/0）；
  bench 其余项在噪声内。
- 两次"不是不动点"都是同步造成的假象：rsync 把本地 `src/boot` 下未入库的构建产物（.ifc / .inl / .lcil，带着旧的修改时间）带到 linux01，构建不再重新生成，
  stage1 的编译器读到与 boot.dll 不符的接口文件、丢掉了已知函数的直接调用（cleat.compiler.dll 2.2 MB）。改为只同步 `git ls-files -co --exclude-standard`。

### 没做的
- tak：体里的 `(not …)` 是对 `clojure.core/not` 的普通调用，不算纯，不特化（28 ms，与关时相同）。把 not 这类已知的纯核心函数加进白名单要先确定
  "纯"的来源（Var 可被重定义），留给 cleat-105 一起看。
- gcd 一类只有 recur、没有自调用的函数：不特化（arity 循环的变量来自 Num 形参，cleat-103 也不推测 Num 变量）。
- 别的函数里对类型化函数的调用：仍经 Var 与通用入口（一次 isinst + try），没有直调 invokeStaticTyped。
