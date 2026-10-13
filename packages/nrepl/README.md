# nREPL（cleat 移植）

[nREPL](https://github.com/nrepl/nrepl) 1.8.0（上游提交 `d00d171`）移植到 cleat：服务端（会话、可中断求值、补全、查找、
load-file、打印与错误钩子、标准输出转发）、客户端（`nrepl.core`）、bencode / EDN / tty 三种传输、TCP 与 Unix 域套接字、
TLS，以及命令行 `nrepl.cmdline`。纯 Clojure，不依赖 cleat 之外的东西；许可与上游相同（EPL 1.0，见 `epl.html`）。

## 用法

```shell
# 启动服务（写 .nrepl-port，打印 nrepl:// 地址）
cleat -I packages/nrepl/src -m nrepl.cmdline
cleat -I packages/nrepl/src -m nrepl.cmdline --port 7888
cleat -I packages/nrepl/src -m nrepl.cmdline --socket /tmp/nrepl.sock
cleat -I packages/nrepl/src -m nrepl.cmdline --tls-keys-file server.keys

# 启动并用自带的客户端连上；或连到已有的服务
cleat -I packages/nrepl/src -m nrepl.cmdline --interactive
cleat -I packages/nrepl/src -m nrepl.cmdline --connect --port 7888
```

在程序里：

```clojure
(require '[nrepl.server :as server] '[nrepl.core :as nrepl])
(def srv (server/start-server :port 7888))
(with-open [t (nrepl/connect :port 7888)]
  (-> (nrepl/client t 1000)
      (nrepl/message {:op "eval" :code "(+ 1 2)"})
      nrepl/response-values))   ;=> [3]
(server/stop-server srv)
```

CIDER、Calva、neorepl（`packages/neorepl`）等 nREPL 客户端都能连。

## 测试

```shell
cleat -I packages/nrepl/src -I packages/nrepl/test packages/nrepl/test/run.clj [命名空间正则]
```

上游的测试套件逐个移植（`test/`，249 个测试 2833 条断言，约 2 分钟）；`NREPL_TEST_VERBOSE=1` 打印每个测试的名字与耗时。
`dotnet test` 经 `tests/Cleat.Host.Tests/PackageTests.cs` 跑它。上游用到的 matcher-combinators、test.check、locksmith、
commons-net 在 cleat 上没有，分别换成 `nrepl.test-matchers`（只实现用到的匹配器）、固定种子的随机值、`nrepl.test-certs`
（.NET 的 `CertificateRequest`）与 `TcpClient`。

## 与 JVM 版的差异

移植以 CLR 的方式实现 JVM 的机制，行为差异如下（源码里以 `cleat:` 注释标出）。

- **线程**：JVM 版用缓存线程池；这里每个任务一个后台线程，栈 512 MB（只占地址空间）——求值要跑 cleat 的展开器与编译器，
  递归很深，宿主的主线程也是这个大小。线程里逸出的异常记日志后线程结束（CLR 上逸出的异常会终止整个进程）。
- **中断**：.NET 没有 `Thread.stop`（`Thread.Abort` 自 .NET 5 起不存在）。`interrupt` 对求值线程调 `Thread.Interrupt`
  （停在 Sleep / Wait / Join / 阻塞读上的立即抛 `ThreadInterruptedException`），并记下这个线程已被中断：之后它的 `*out*` /
  `*err*` 写入即抛出，所以循环打印的代码会停；它抛出的错误不再报告，相当于 JVM 上被 `ThreadDeath` 杀掉的求值。会话换新线程，
  照常响应 `interrupted`。纯计算的循环停不下来，留在后台继续跑（5 秒后记一条警告）。上游测试里"中断睡眠时报 eval-error"
  "轮询 `isInterrupted`""强行停线程"三项按此改写。
- **传输与套接字**：传输与服务是 `IDisposable`（`with-open` 调 `Dispose`）；断线是 `System.IO.IOException`（JVM 是
  `SocketException`）。TCP 与 Unix 域套接字都用 `System.Net.Sockets.Socket`（BCL 在所有支持的系统上都有 Unix 域套接字，
  不需要 JDK 17 或 junixsocket）；`nrepl.socket/as-nrepl-uri` 返回字符串。bencode 传输的输入经 `BufferedStream`；EDN 与
  tty 传输直接读网络流——`BufferedStream.Read` 拷出缓冲里的一部分后会继续阻塞读网络，`StreamReader` 叠在上面会卡在消息中间。
- **消息内容**：`:ex` / `:root-ex` 是 CLR 类型名（`System.ArgumentException`，JVM 是 `class java.lang.…`）；`describe`
  的 `:versions` 里 `:clr` 取代 `:java`；读条件的平台特性是 `:cljr`。
- **打印**：回复用的 writer 与 JVM 一样先缓冲（8K，刷新时整块交给按行 / 按 `buffer-size` 切块的回调），所以输出的切分与上游相同；
  打印配额用尽抛的是 `nrepl.util.streams/quota-exceeded` 做的 ex-info（JVM 是 `QuotaExceeded` Error）。
- **求值**：`LineNumberingTextReader` 没有列号的设置方法，带 `:column` 的代码前面补 column−1 个空格；`*file*` /
  `*source-path*` 取代 `Compiler/SOURCE_PATH` / `SOURCE`；load-file 不重置 JVM 编译器的内部变量（cleat 没有）。
- **补全**：类是已加载程序集的公有类型，成员是 CLR 的方法、字段与属性（属性按字段补全），命名空间来自加载路径下的源文件。
- **TLS**：.NET 的 `SslStream` 取代 JSSE，密钥材料格式与检查相同（只认 TLS 1.3、必须有客户端证书、只按 CA 校验对端、拒绝与服务端相同的证书）。
  差异：不支持 EdDSA 私钥；客户端在连接时就完成握手（JVM 在第一次读写时），所以密钥不对时 `connect` 就抛出；
  `X509Certificate2.CreateFromPem` 只收 `ReadOnlySpan`，cleat 的互操作传不了，证书与私钥的配对经 `CreateFromPemFile`，
  PEM 短暂写进只有当前用户可读的临时目录，用完即删。
- **命令行**：Ctrl+C 是 `Console.CancelKeyPress`；`System/exit` 换成 `Environment/Exit`；`.nrepl-port` 在 `ProcessExit` 时删除——
  .NET 的控制台程序收到 SIGTERM 时不触发 `ProcessExit`（JVM 会跑关闭钩子），所以登记了要在退出时删的文件后，SIGTERM 改为经
  `Environment/Exit` 退出（`nrepl.socket/exit-on-sigterm!`）；没有 `nrepl.main` 入口类，用 `-m nrepl.cmdline`。
- **版本**：固定为 1.8.0（JVM 版读 jar 里的 POM 属性），环境变量 `nrepl.version` 可以覆盖。

未移植：`nrepl.spec`（cleat 没有 clojure.spec；会话里只在 `clojure.spec.alpha` 已加载时绑定 `*explain-out*`）、
`nrepl.util.jvmti` 与 `libnrepl` 原生库、`nrepl.util.classloader`、`nrepl.socket.jdk17` / `junixsocket`、Java 辅助类
（`SessionThread`、`QueuePollingReader`、`CallbackBufferedOutputStream` 等由 `nrepl.util.queue`、`nrepl.util.streams`、
`nrepl.util.threading` 用 Clojure 实现）。

## 移植中发现的 cleat 问题

- 已修（cleat-064）：宏展开出的 def 不记源位置、以 `-` 开头的协议方法分派不到实现、带超时的 `deref` 吞掉 future 的错误、
  `LispReader+ReaderException` 的消息是 .NET 的默认文本、`spit` 不截断文件。
- 未修（cleat-065）：常量集合字面量上的非常量元数据不求值；`nrepl.middleware-test` 因此用 `with-meta` 写中间件。
- ClojureCLR 的 `clojure.clr.io` 不能对一般的 `TextReader` 做 `slurp`；相应的测试改为 `.ReadToEnd` 加 `.Dispose`。
