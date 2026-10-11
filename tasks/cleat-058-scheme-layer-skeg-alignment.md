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
