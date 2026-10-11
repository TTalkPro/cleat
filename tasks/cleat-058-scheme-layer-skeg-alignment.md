# cleat-058 Scheme 元层按 skeg 对齐：guard / call-with-return / try·catch·finally·throw / #nil / EmptyList

- 里程碑：后续　依赖：cleat-047、cleat-054　状态：进行中
- 设计依据：skeg 的 designs/13-exceptions.md（§1 ~ §7）与其提交 e02260a、6cd25cc、a67bece、54f5181、7e32a99、63fdb30、4fe132e、
  4ee4ebe、fbdd543、ad82eaf、d6e49ec、aab48c4；本仓库 [15 §3](../designs/15-scheme-core-vendoring.md) 的异常模型（本任务推翻其中
  "删除处理器栈 / call/cc" 两条）。

## 背景

cleat-047 为简化元层删掉了 call/cc 与处理器栈，`guard` 改为 `$try/catch` 的宏——代价是 `guard` 不再有 R6RS 语义
（`raise-continuable` 穿过 guard、子句在 raise 处求值），也**不接 CLR 异常**（15 §3 写了要包成条件，实现没做；
`try-catch.sps` 还断言了它会漏过）。CLR 异常靠 `clr-guard`、`with-clr-exception-handler` 与各处手写的 `host-guard` 宏分别处理。
同源的 skeg（同为 IronScheme 改名）已把这些整理成一套，本任务按它对齐。

## 工作内容（用户 2026-10-11 的五点）

1. **恢复 guard，统一 Scheme 的 guard 与 clr-guard**
   - 恢复 C# 处理器栈（`ExceptionHandlers`，AsyncLocal；`$current-handlers` / `$set-current-handlers!`）、`with-exception-handler`、
     `raise` / `raise-continuable` 的 R6RS 语义（skeg 未改这部分，照搬）。
   - `guard` 按 skeg 13 §4：`call-with-return` + `with-exception-handler`；子句在 raise 处测试，命中的体经 outerk 逃逸到 guard 帧执行；
     不命中 `(raise-continuable con)`。
   - .NET 异常 → 条件（skeg 13 §5）：条件类型 `&clr-exception`（父 `&error`，字段 `exception`）、`make-clr-condition` /
     `clr-condition?` / `clr-condition-exception`、`clr-exception-of?`；运行时钩子 `clr-exception-violation`；
     转换点：Scheme 的宿主调用边界（codegen 的 `host-call` / `host-field-*` / `host-new*`——**不含 Clojure 的 `clj-call`**，
     Clojure 层自己按 CLR 类型捕获，cleat-042 的快速路径不受影响）与 `with-exception-handler` 的处理器级 CLR catch（`$raise-clr-exception`）。
   - `clr-guard` 改为 `try` 上的宏；各处手写 `host-guard` 改用 `guard` / `try`。
2. **call/cc → call-with-return**：逃逸、一次性（`EscapeContinuation` + `ContinuationException`，catch 用同一性过滤，finally 作废），
   恢复 codegen 的 `escape-frame` 与 stage0 的支持；不保留 `call/cc` 别名。所有 CLR catch（含 Clojure 的 `try`）要放过 `ContinuationException`。
3. **#nil**：读写 `#nil`（CLR null），`nil?`（内联 `ldnull ceq`）；Scheme 中 `#nil` 为假、`'()` 为真（`RT.IsTrue` 改为 `o != null && (o is not bool b || b)`）。
4. **`'()` = `EmptyList.Instance`**（`Cleat.Scheme.EmptyList`，`IReadOnlyList<object>`）：表尾、`null?`、构造、fasl（新标签、版本号）、
   常量编码 `(empty)` / `(nil)`、发射器、stage0、嵌入 API；Clojure 层：Clojure nil = `#nil` = CLR null，原来用 `'()` 表示 nil 的地方改 `#nil`，
   `nil->f` 改用 `nil?`；Clojure 的 `if` 不再需要 `(if (null? t) #f t)` 包装（#nil 本身为假）。
5. **删除 dynamic-wind**，改用 `try` / `catch` / `finally` / `throw` 语法（psyntax 核心宏，skeg 13 §3 / §3a）；
   `parameterize`、`with-gensym-scope`、`load`、`trace`、`io/simple`、`with-ns-binding` 等改用 `$try/finally` / `try … (finally …)`。

## 分阶段（每阶段一次提交：测试全绿、种子不动点）

- **阶段 1**：处理器栈 + `with-exception-handler` + `call-with-return` + 新 `guard` + `&clr-exception` 与两个转换点 + `clr-guard` 统一。
- **阶段 2**：`try` / `catch` / `finally` / `throw` 宏，删除 `dynamic-wind`。
- **阶段 3**：`#nil` 与 `EmptyList`（影响面最大：运行时、fasl、发射器、codegen、stage0、Clojure 层、嵌入）；
  展开器文件用到新原语 `nil?`，按 skeg 13 §7 / IronScheme 05 §5 的两步法重生成种子。

## 验收标准

- Scheme 测试：skeg `host-smoke.sps` 中 guard / try / throw / #nil 各组用例的对应版本；R6RS 异常测试按 skeg 的期望。
- 两个不动点（Scheme 镜像、Clojure 镜像）与种子不动点（镜像生成 = stage0 生成）成立；符合性基线不退化；cleat-042 的基准不退化。

## 进度

### 阶段 1（2026-10-12）：处理器栈、call-with-return、guard、&clr-exception

- C#：cleat-047 删掉的 `EscapeContinuation` / `ContinuationException` / `ExceptionHandlers`（线程静态）/ 钩子 `raise-continuable`、
  `with-exception-handler` 按 c0e9d62 的逆补丁恢复；`call/cc` 改名 `call-with-return`（普通原语，不是编译器特殊形式——skeg 设为特殊形式
  只为 async 体内联，本仓库元层没有 async）；新增 `Errors.RaiseClrException`、钩子 `clr-exception-violation`（共 16 个）、
  不安全原语 `$raise-clr-exception`。stage0 的 `$try/catch` 放过 `ContinuationException`。
- Scheme：`exceptions.sls` 按 skeg 恢复 `with-exception-handler`（装处理器时外包 CLR catch：回卷上来的 .NET 异常包成 `&clr-exception` 条件
  在本处理器下 raise）、R6RS 的 `raise` / `raise-continuable`；`conditions.sls` 加 `&clr-exception`（父 `&error`）；`guard` 按 skeg 13 §4
  （call-with-return + with-exception-handler，不命中时 raise-continuable）；`main.sls` 的三处（eval-top-level、with-guard、repl-describe）
  改为处理器式。
- 编译器：`try-catch` 有按 CLR 类型的子句时先加一个 `(catch ContinuationException (rethrow))`——Clojure 的 `(catch Exception e …)`
  不会吞掉逃逸续延。
- **与 skeg 的差别**：.NET 异常只在 `with-exception-handler` 一级转换，不在每个宿主调用（clr-call 等）处包 try 区域（skeg 13 §5.1 的
  第 1 个转换点）。对 guard / try 的结果相同（宿主异常不可继续）；省掉每次宿主调用的 try 区域（Scheme 编译器自身大量宿主调用，影响 Clojure 编译速度）。
- **语义变化**：`$try/catch '#f` 不是处理器——外层有处理器（guard、eval-top-level）时 raise 先交给处理器，`$try/catch '#f` 只接住走到缺省
  处理器的 raise。Clojure 驱动不装 Scheme 处理器，Clojure 代码里的 Scheme raise 仍到缺省处理器变成 SchemeException，Clojure 的 catch 照常看到；
  因此 Clojure 的 `try` 不需要另装处理器。测试：四个 Clojure 脚本的外框把 `&clr-exception` 报成 `(exception 消息)`（意图不变）；
  `try-catch.sps` 的外框在用例期间清空处理器栈，"外层 guard 内 raise" 一例按 R6RS 改期望。
- 种子：按两步法重生成两次（先让种子的库表认识新名字，再让 main.sls 用它们），两次都是不动点（镜像生成 = stage0 生成）。583 测试全绿。

### 阶段 2（2026-10-12）：try / catch / finally / throw，删除 dynamic-wind

- 展开器核心宏 `try`、`throw`（skeg 13 §3 / §3a 原样）：`try` 装处理器，catch 按 `clr-is` 匹配 `&clr-exception` 条件里的原异常，
  经逃逸续延回到 try 帧分派；不匹配 `raise-continuable` 给外层；`finally` 是 `$try/finally`。从 `(cleat scheme)` 与 `(rnrs)` 导出，
  两张构建脚本表都登记为系统宏。
- 删除 `dynamic-wind`（定义与 `base.sls` / 两张表的导出）。原用处：`parameterize`（psyntax 与 compat 两份，compat 用 datum->syntax
  取 `$try/finally`）、`with-gensym-scope`、`load/unload`、`trace`、`io/simple`（局部 try 宏删除）、Clojure 层的 `with-ns-binding`，
  都改为 `$try/finally`。
- `clr-guard` 改为 `try` 上的宏（catch System.Exception）；`(cleat scheme clr)` 加 `clr-exception-of?`。
- 测试：新增 `tests/scheme/compiler/guard-try.sps`（移植 skeg host-smoke 的 guard / try / throw / call-with-return 部分，32 例）；
  `compile-small.sps`、`try-catch.sps` 里用 dynamic-wind 的用例改为 `try … (finally …)`（guard 的子句体在 finally 之后执行）。
- 种子：一步重生成（展开器文件只用到种子已认识的 `$try/finally`），不动点；种子里不再有 dynamic-wind。590 测试全绿。
