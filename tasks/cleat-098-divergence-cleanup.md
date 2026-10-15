# cleat-098 偏离表清理

- 里程碑：M9　依赖：cleat-095　状态：完成

## 背景
`tests/conformance/cleat-divergences.edn` 有 529 条：309 条 `:host/jvm-api`、98 条 `:corpus/jolt-value` 等是宿主差异或语料本身的问题，不动；
`:error-model` 6、`:numeric` 2、`:host/boxing` 2、`:strictness` 2、`:printf` 2、`:not-yet` 1（`seque`）、`:upstream/clojureclr` 7 需要逐条判断。

## 工作内容
1. 逐条过上述小类：能修的修（编译器或运行时），不该修的改写 `:reason` 说明为什么是永久偏离。
2. nREPL 的 middleware 测试改回上游的 `^{…}` 写法（cleat-065 之后可行）。

## 验收标准
- `conformance --run --gate` 通过；偏离表里没有 `:not-yet`（或每条 `:not-yet` 指向一个任务）。

## 结论（2026-10-15）

完成。偏离表从 529 条到 526 条（cleat-097 删掉两条 shuffle 的 `:strictness`，本任务删掉 `:not-yet` 的 seque），剩下的小类逐条判断如下，都是
永久偏离（`:reason` 已写清），不改：

| 类别 | 条 | 判断 |
|---|---|---|
| `:error-model` | 6 | 编译错误是一个带位置的 ExpanderException，不是 CompilerException 包一层 cause；ns 的 exclusion / private var 的错误嵌套不同——cleat 的错误模型，designs/02 |
| `:upstream/clojureclr` | 7 | core.clj 跟 ClojureCLR master（Clojure 1.13）：关键字键的 array-map 不在 9 项时升级、select-keys 用 get、destructure 的 :or 求值顺序、`*clojure-version*` 1.13 |
| `:numeric` | 2 | `%e` 打印次正规数、rationalize 用 double 的精确二进制值：CLR 原生数值（designs/02 的决定） |
| `:host/boxing` | 2 | 装箱的 long 不共享实例、符号名字串不 intern：CLR 没有 JVM 的小整数缓存与 String.intern |
| `:printf` | 2 | clojure.lang.Printf（ClojureCLR 移植的 Formatter）接受 `%#s`、`%<s` 的异常类型不同 |
| `:host/hash` 3、`:host/strings` 1、`:host/message` 2、`:host/defaults` 1 | 7 | hasheq / Char.IsWhiteSpace / 错误信息措辞 / `*compile-path*` 缺省 "."：宿主差异 |

**seque**：ClojureCLR 的 core 没有它；按 JVM 版实现（`LinkedBlockingQueue` → `BlockingCollection`，offer → `TryAdd … 0`，put → `Add`，
take → `Take`；agent + `release-pending-sends`），`(seque [1 2])`、带缓冲的无穷序列、生产者抛错（与 JVM 一样：分块的源在放进任何元素前
就抛，得到 `()`）行为一致。

**nREPL**：`packages/nrepl/test/nrepl/middleware_test.clj` 改回上游的 `^{::middleware/descriptor …} {…}` 写法（cleat-065 之后可以了），
`mw` 辅助函数与说明注释删去；PackageTests 的 nREPL 套件通过。

core.clj 改了：种子重生成，不动点与全量测试见提交信息。

