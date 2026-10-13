# cleat-076 Scheme 元层的 M10 性能项（移植 skeg iron-061 ~ 068）

- 里程碑：后续　依赖：cleat-075　状态：完成
- 来源：skeg 提交 1039327、4ccd134、0ddd5b4、eb38e49、e346a94、c8efc13、05e9388、b83e38a（`~/workspace/skeg/Skeg/tasks/iron-061` ~ `iron-068`）

## 背景

skeg 的 M10 按 Chez 的写法消掉了 r7rs-benchmarks 里的一批热点，cleat 的 Scheme 元层（同源于 IronScheme）还没有这些。
iron-069 / 070 / 071 / 073 都建在它们之上，所以先移植 M10（cleat-075 的背景里定下的范围与顺序）。

| skeg | 内容 |
|---|---|
| iron-061 | `quotient` / `remainder` / `modulo` 按类型分派（Chez s/5_3.ss）；编译器内联 fixnum 的 `div` / `rem` |
| iron-062 | `exact-integer-sqrt` / bignum 平方根改用 Zimmermann 的 Karatsuba 平方根（`SqrtRem`） |
| iron-063 | `guard` / `try` 正常返回不经 `outerk` 逃逸（同 Chez 的 `$guard`） |
| iron-064 | `zero?` `positive?` `negative?` `abs` `floor` … `even?` `odd?` `max` `min` `inexact` `exact` `expt` 的 fixnum / flonum 快路径；`$fixnum?` / `$flonum?` 编译为 `isinst`，一元谓词内联；`sqrt` 对非完全平方的分数返回 flonum |
| iron-065 | 处理器栈线程静态（cleat 已经是）；参数对象共用一个 `AsyncLocal<object[]>` |
| iron-066 | 通用算符的内联模板加 flonum 路径；操作数都是 flonum 的 `+ - * /` 推断为 flonum |
| iron-067 | `equal?` 入口按类型分派、`assoc` / `member` 的 eqv 键、`list-tail` 的 fixnum 循环、两参数 `append` 一遍完成、`values` 固定子句、`apply` 不建中间表、`string-append`、`$vector-fill!`；`with-clr-exception-handler` 在普通代码里内联 |
| iron-068 | 局部逃逸续延（`k` 只在空栈位置被直接调用）编译为跳转 |

## 做法

三方合并：基为 skeg 7a77817（M10 之前），theirs 为 615e740（M10 结束），改名后 `git merge-file` 进 cleat 的文件（cleat-075 的做法；
改名规则补上 `(skeg)` / `(skeg x …)` → `(cleat scheme …)`）。17 个文件里 12 个无冲突。

与 skeg 结构不同、要改写的地方：

- **中间语言**：skeg 的 `escape-frame` / `try-catch x body0 body1` 来自 async 的降级（D10），cleat 没有 async，`try-catch` 是 `$try/catch`
  的多子句形式。cleat 新增两个节点，只有非 async 的语义：`(escape-frame k body)`（iron-068 的局部逃逸）与 `(try-handle x body0 body1)`
  （iron-067 内联的 `with-clr-exception-handler`；改名以免与 cleat 的 `try-catch` 冲突）。由 `lower-loops`（cleat 的 `loops.sls`，skeg 在
  `async.sls` 里）产生；`front`（自由变量、赋值转换：被捕获又赋值的异常变量进处理体时装箱）、`closures`、codegen 的各遍历登记。
- **codegen**：`escape-frame` 只有 `cg-local-escape!`（(k v) 是存值后 `br`，跨 try 区域用 `leave`）；`try-handle` 是 skeg 普通方法里的
  `try-catch` 去掉 async 恢复点。async 帧相关的代码（`cg-escape-body!` 的恢复点、`EscapeContinuation` 版的 `escape-frame`）不移植。
- **Parameters.cs**：cleat 的处理器栈本来就是线程静态的（cleat-058），只移植参数对象的存储（去掉 `AsyncFrame` 的判断）；skeg 的 `Tasks.cs`
  （`task-run` / `task-then` 的处理器表）cleat 没有。
- 冲突的其余几处是注释与 `values` 的定义（保留 cleat 的 dynamic-wind 说明）。

## 测试

- `tests/scheme/base/base-library.sps`：skeg 的整个文件（A ~ K 组来自 IronScheme iron-054 / 059，L ~ O 组是 M10 的，期望值取自 Chez）。
  移植前在 cleat 上 224 项只差 2 项（`(abs -0.0)`、`sqrt` 对非完全平方返回精确分数——正是 iron-064 修的），移植后全过。
- 快照：新增 `generic-fl-loop`、`local-escape`，与 skeg 的逐字相同（cleat 的程序集多一个 `(runtime scheme)` 属性）；`inline-arith` 更新，与 skeg 的相同。
- `NumbersTests.ExactIntegerSqrtIsCorrect`（iron-062）。
- 种子重生成，不动点（重建镜像后再生成，逐字相同）。
- 微基准 `tests/scheme/perf/micro-ops.sps`、`micro-exceptions.sps`（skeg 的，手动运行，不进 dotnet test）。

## 结论（2026-10-14）

完成。

- **微基准**（`tests/scheme/perf/micro-ops.sps`、`micro-exceptions.sps`，ns/次，含约 6 ns 循环开销；与 cleat-075 的构建（另建工作树）
  交替运行四轮取最小值）：

  | 操作 | 改前 | 改后 |
  |---|---:|---:|
  | `guard` 不抛异常 | 3872 | 70 |
  | `try` 不抛异常 | 3845 | 68 |
  | `call-with-return` 局部逃逸 | 1496 | 5 |
  | `remainder` / `quotient` / `modulo` | 200 / 179 / 177 | 17 / 12 / 39 |
  | `even?` / `zero?` | 116 / 15 | 6 / 6 |
  | `sqrt`（flonum） / `exact`（flonum） | 105 / 157 | 17 / 16 |
  | `expt`（fixnum） | 218 | 63 |
  | 通用 `+` / `*` 作用于 flonum | 30 / 31 | 9 / 10 |
  | `equal?`（fixnum） / `assoc` | 23 / 65 | 6 / 31 |
  | `values` 两个值 / `append` / `string-append` | 46 / 39 / 72 | 20 / 26 / 49 |
  | `parameterize` | 521 | 296 |
  | `exact-integer-sqrt`（2000 位） | 28.2 ms | 35 µs |

  几项小操作稳定地慢 1 ~ 10 ns：`eqv?`（fixnum）7 → 10、`number->string` 35 → 45、`string->utf8` 68 → 77、`case-lambda` 剩余参数
  35 → 39。它们的代码没有变（`number->string` 对 fixnum 走的分支只多了内联的 `fixnum?`）；镜像由 2175488 增到 2223104 字节，
  循环里的通用 `<` / `+` 多了 flonum 路径。按 skeg iron-070 的经验（fib 的 5% 查明是代码对齐）记录在案，不再追。
- **Clojure 基准**（`tools/build.cs bench -r 3`，两个构建各跑一次）：assoc_map、fib、keyword_invoke、lazy_chain、prim_fib、protocol、
  reduce_vec、sum_loop 的最小值在噪声内一致——Clojure 代码不经过这些 Scheme 过程。
- **测试**：Scheme 测试 237/237（base-library 224 项、r6rs-edges 40 项、快照、`ExactIntegerSqrtIsCorrect`）；全量见下。

### 顺带：neorepl 会话缓存的并发读（cleat-063 的缺陷）

全量测试里 `PackageTests.Neorepl` 的 `ensure!-at-the-same-time-test` 失败一次：10 个线程同时为不同名字建会话，缓存里少了第一个。
复现脚本（60 轮并发 `ensure!`）表明 cleat-075 的构建上同样会出现（约 1/30 轮），与本任务无关：cleat 沿用 ClojureCLR 的
`clojure.clr.io`，`slurp` 以 `FileShare/None` 打开文件，锁外读缓存（`cached`）的线程与锁里的 `read-cache` 同时打开时后者抛
`IOException`，被当成空缓存，写回时冲掉别人刚存的会话（JVM 上没有这种锁，原版没问题）。`sessions.clj` 的读取改用
`File/ReadAllText`（共享读）；`discovery.clj` 读 `.nrepl-port` 同样改（并行的 CLI 调用读同一个端口文件）。修后 60 轮 0 次。
已发布的 `Cleat.Neorepl 0.1.2` 仍是旧代码，需要时另发新版本。
