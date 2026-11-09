# cleat-185 core.async：timers-test 偶发挂起

- 里程碑：M21　依赖：无　状态：完成（2026-11-09）
- 来源：[cleat-183](cleat-183-by-ref-arguments.md) 的全量复跑

## 问题

`packages/core.async` 的上游测试偶发挂在 `clojure.core.async.timers-test/timeout-ordering-test`（四个线程各 `<!!` 一个 timeout 通道后 `Join`）：
某个 timeout 通道永远不关，测试线程卡在 `Thread.Join`，`PackageTests.CoreAsyncWithAnalyzerAndReader` 跑满 20 分钟超时。

- 频率：单独跑上游测试（`packages/core.async/test/run.clj`）约 3 / 16 次；全量 Host 里 2 次复跑中 1 次。
- 只在前面跑过 `async_test` 之后出现：单独跑 timers-test 12 / 12 通过。挂起时 `async_test` 的若干 future 还阻塞在 `<!!`（CountDownLatch.Await）。
- 已排除：timeout 守护线程因 `(TimeSpan. 负数)` 的 `WaitOne` 抛出而死——线程里未处理的异常会让进程退出，挂起时进程活着、CPU 约 5%。
- 与 cleat-183 无关的依据：by-ref 在运行时只涉及 tools.reader 的数字解析与 dispatch 宏的展开期，不碰调度与 timer 路径。

## 线索

- `impl/timers.clj`：`timeout` 在锁里 `Add` 并 `Set` AutoResetEvent，`timeout-worker` 在锁外读 `(.Min timeouts-set)`（SortedSet 非线程安全），
  `TimeoutQueueEntry` 只按 timestamp 比较（同 timestamp 视为相等，`Add` 会丢）。上游 clr.core.async 同样的代码。
- `impl/dispatch.cljc`：go 块与回调都走 `ThreadPool/QueueUserWorkItem`；async_test 留下的阻塞 future 是否让线程池饥饿、`<!!` 的投递回调排不上。

## 工作内容

1. 复现并在挂起时取全部线程的托管栈（`dotnet-dump`，不过滤），确定是 timer 没关通道、还是关了但 `<!!` 的回调没投递。
2. 按原因修（cleat 运行时或 core.async 的补丁，补丁按 `;;; CLEAT-185` 标注）。
3. 上游测试连跑 30 次无挂起；全量 Host 通过。

## 结果（2026-11-09）

### 原因

`impl/timers.clj` 的 `timeout-worker`：`process-entries`（锁内关掉已过期的项）之后，在锁外读 `(.Min timeouts-set)`，按
`(TimeSpan. (- 最早项的时刻 现在))` 等待。最早的项恰好在两步之间过期时等待是负的；`WaitHandle.WaitOne(TimeSpan)` 把它截成整毫秒：

- (-1, 0) ms → 0，无害；
- **(-2, -1] ms → -1 = `Timeout.Infinite`**：worker 一直睡到下一次 `timeout` 调用（它会 `Set`）。之后没有新的 timeout 时，所有 timeout 通道都不关——
  这就是挂起（timers-test 的四个线程各 `<!!` 一个 timeout 后 `Join`，没有后续调用）；
- **≤ -2 ms → `ArgumentOutOfRangeException`** 在该线程上未处理，进程退出（25 次循环里出现过 1 次，栈指向 timers.clj 的 WaitOne）。

直接验证：`(.WaitOne (AutoResetEvent. false) (TimeSpan. -5000))` 立即返回，`(TimeSpan. -15000)`（-1.5 ms）6 秒后仍在等。
async_test 之后才出现，是因为它造了大量短 timeout，命中这个窗口的次数多；先前排除"守护线程因异常而死"是对的（那种情形进程会退出），
挂起的是另一个分支（无限等待）。上游 clr.core.async 同样的代码。

### 修复

`timeout-worker` 在与 `timeout` 的 `Add` 同一把锁里读最早的项（SortedSet 非线程安全），等待时间取 `(max 0 …)`（`;;; CLEAT-185`）。

### 测试

- cleat 专有的 `test/clojure/core/async/cleat_timers_test.clj`：4 个线程各取 1500 个 0 ~ 3 ms 的 timeout（共用 45 秒期限）、空闲之后的 timeout 仍会关
  （10 秒期限）——回退时是失败而不是挂起。旧的 timers.clj：6 次里 3 次失败（各约 63 秒）；修复后通过。
- 压力对照（4 线程 × 3000 个随机 0 ~ 3 ms timeout，各 13 次）：旧版挂起 3 次，修复后 0 次（每次约 22 秒）。
- 上游测试连跑 30 次：30 / 30 通过（修复前约 3 / 16 次挂起）。core.async 现在 59 个测试、351 个断言。

### 门禁

Core 116 / Compiler 50 / 全量 Host 292（`PackageTests.CoreAsyncWithAnalyzerAndReader` 含新测试，源码与包程序集各一遍）；`image-set --twice --ilverify`、
conformance `--gate`、CTS / test_clojure 基线 OK。本节只改 packages/core.async，镜像不变。
