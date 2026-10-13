# cleat-062 nREPL 包（packages/nrepl）

- 里程碑：后续　依赖：cleat-053、cleat-064　状态：完成
- 设计依据：上游 [nREPL](https://github.com/nrepl/nrepl) 1.8.0（`~/workspace/nrepl`，提交 `d00d171`）；ClojureCLR 的
  `clojure.core.server`（CLR 上的套接字服务写法）。

## 背景

用户 2026-10-13 要求把 nREPL 与 neorepl 移植进本仓库，作为两个独立的包。决定（用户选定）：放在 `packages/nrepl`、
`packages/neorepl`；求值中断在 CLR 上"中断后放弃"（不往编译器里加轮询点）；neorepl 的行编辑器用 Clojure 在 System.Console
上重写；neorepl 的 jack-in 只认 cleat 项目。本任务是 nREPL，neorepl 见 [cleat-063](cleat-063-neorepl-package.md)。

## 工作内容

1. **包的布局**：`packages/nrepl/{src,test,load-file-test}`、`README.md`、`epl.html`（上游许可）。纯 Clojure，用
   `cleat -I packages/nrepl/src` 加载；命令行 `cleat -I packages/nrepl/src -m nrepl.cmdline`。
2. **源码移植**（28 个命名空间）：JVM 互操作逐处换成 CLR——线程池 → 每任务一个大栈后台线程（`nrepl.util.threading`）、
   `LinkedBlockingQueue` / `SynchronousQueue` → `nrepl.util.queue`（持久队列 + Monitor）、Java 辅助类
   （`QueuePollingReader`、`CallbackBufferedOutputStream`、`QuotaBoundWriter`、`TeeOutputStream`、`SessionThread`）→
   `nrepl.util.streams` 的 TextReader / TextWriter 代理、`java.net` / `java.nio` 套接字 → `System.Net.Sockets.Socket`
   （TCP 与 Unix 域套接字同一类型）、JSSE → `SslStream`、类路径扫描 → 已加载程序集与加载路径、`sun.misc.Signal` →
   `Console.CancelKeyPress`。差异逐条见包的 README。
3. **中断语义**（用户选定"中断后放弃"）：`Thread.Interrupt` + 中断登记（`nrepl.util.threading/interrupted?`）：被中断线程的
   `*out*` / `*err*` 写入即抛出（循环打印会停），它的错误不再报告（对应 JVM 被 ThreadDeath 杀掉的求值）；纯计算循环留在后台。
4. **测试**：上游测试逐个移植（`nrepl.spec`、classloader、jvmti 三处 JVM 专用的除外），缺的库换成包内替身
   （`nrepl.test-matchers`、`nrepl.test-certs`）；`test/run.clj` 是运行器；`tests/Cleat.Host.Tests/PackageTests.cs` 接进 `dotnet test`。

## 验收标准

- 上游测试（移植后）全部通过；CIDER 风格的 bencode 客户端能连、求值、补全、查找、中断；全量测试通过。

## 结论（2026-10-13）

完成。249 个测试 2833 条断言全部通过（约 2 分钟），bencode 与 EDN 两种传输各跑一遍 core 测试；tty、TLS、Unix 域套接字、
命令行（含起子进程的 ack / 端口 / 套接字测试）都覆盖。

- **移植中修的 cleat 缺陷**另立 [cleat-064](cleat-064-port-found-runtime-fixes.md) 先行提交：宏展开出的 def 的源位置
  （nREPL `lookup` 的 `:line` 依赖它）、以 `-` 开头的协议方法、带超时的 `deref`、`ReaderException` 的消息、`spit` 不截断。
  未修的记为 [cleat-065](cleat-065-literal-metadata-evaluation.md)（常量字面量上的非常量元数据）。
- **三个 CLR 上才有的坑**：一是 `BufferedStream.Read` 拷出缓冲里的一部分后还会阻塞读网络，EDN / tty 传输的 `StreamReader`
  叠在上面会卡在消息中间——文本传输改读不带缓冲的网络流（`nrepl.socket/input-stream`）；二是线程里逸出的异常会终止整个
  进程——nREPL 起的线程都有顶层捕获；三是控制台程序收到 SIGTERM 时不触发 `ProcessExit`——登记了退出时删除的文件
  （`.nrepl-port`、Unix 域套接字）后，SIGTERM 改为经 `Environment/Exit` 退出。
- **测试的改写**（都在测试文件里以 `cleat:` 注释说明）：`:ex` 的类型名、`:cljr` 读条件、中断三项（见 README）、
  客户端在连接时握手（TLS 坏密钥的三项）、`slurp *in*`（`clojure.clr.io` 不收一般的 TextReader）、
  中间件描述用 `with-meta`（cleat-065）、编译出的函数在 CLR 栈帧里没有源文件（改查 Var 的 `:file` / `:line`）。
