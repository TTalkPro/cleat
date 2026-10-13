# cleat-063 neorepl 包（packages/neorepl）

- 里程碑：后续　依赖：cleat-062、cleat-064　状态：完成
- 设计依据：上游 [neorepl](https://github.com/nrepl/neorepl) 0.1.0（`~/workspace/neorepl`，提交 `41d8b3a`）与它的 PLAN.md。

## 背景

与 [cleat-062](cleat-062-nrepl-package.md) 同一个请求（2026-10-13）：把 neorepl 移植进来作为独立的包。用户选定：行编辑器用
Clojure 在 System.Console 上重写（不用 JLine，也不复用 Cleat.Host 的 C# LineEditor）；jack-in 只认 cleat 项目。

## 工作内容

1. **包的布局**：`packages/neorepl/{src,test,bin}`、`README.md`、`LICENSE`（上游 EPL 1.0）；`bin/neorepl`（sh）与
   `bin/neorepl.cmd` 用 PATH 上的 cleat（或 `$CLEAT`）带着两个包的加载路径运行 `neorepl.main`。
2. **源码移植**（16 个命名空间）：纯逻辑的（lexer、cli、render、lookups、json）几乎原样；客户端（`neorepl.nrepl`）、
   求值（`neorepl.evaluation`，队列换成 `nrepl.util.queue`）、发现、会话缓存（锁文件换成 `FileShare/None`）、关闭钩子、
   XDG 目录、main、repl 按 CLR 改写。
3. **行编辑器** `neorepl.editor`：重写，对外的 API 与 JLine 版相同（`open` `close` `read-code` `read-line` `print-text`
   `print-code` `fresh-line` `on-interrupt` `suspended` `width`）。终端是一个协议：控制台（`Console.ReadKey`、ANSI 输出、
   读行时 Ctrl-C 是按键，其余时候是 `CancelKeyPress`）与测试用的流终端（按键序列解码自 TextReader，含转义序列与括号粘贴；
   不读行时的 Ctrl-C 交给信号处理函数，像终端把它变成 SIGINT）。
4. **jack-in**：只认 cleat 项目（`cleat.edn`，可写 `:paths`、`:middleware`）；服务是 cleat 运行 `nrepl.cmdline`；
   停止时连同子进程（`pgrep -P`）发 SIGTERM，5 秒后 SIGKILL。
5. **测试**：上游测试逐个移植；jack-in / server / kill 几组按 cleat 项目重写；`tests/Cleat.Host.Tests/PackageTests.cs` 接进 `dotnet test`。

## 验收标准

- 移植后的测试全部通过；真实终端上 REPL 可用（编辑、补全、eldoc、历史、Ctrl-C）；CLI 子命令对 cleat 的 nREPL 服务可用；全量测试通过。

## 结论（2026-10-13）

完成。86 个测试 704 条断言全部通过（约 1.5 分钟），从源码加载与预编译（AOT）两种情形都通过。

- **启动时间**：CLI 每次从源码加载 neorepl 与 nREPL 约 7 秒；按依赖顺序、每个命名空间一个进程地预编译成 `.clj.dll` 后约 1.4 秒。
  同一进程里连着 `compile` 多个命名空间时，后编的会引用前面那个求值时生成的内存类型（`eval$80` 程序集，运行时找不到），
  所以必须分进程——用户决定把这个"按包构建"的做法放进移植中的 tools.build 包（[cleat-066](cleat-066-tools-build-package.md)），
  不加到 `tools/build.cs`。
- **为预编译修的 cleat 缺陷**：AOT 模块的常量表在本命名空间 require 别的命名空间之前就初始化，常量里有别的命名空间的 deftype
  （如 `nrepl.socket` 引用 `nrepl.tls.TlsConnection`）时找不到类型。fasl 读类型找不到时，按类型名前缀在加载路径上找对应命名空间的
  `.clj.dll`，只载入程序集（不跑入口）再找（`Loader.LoadAssemblyDefining`、`Fasl.ResolveClojureType`）；之后 require 载入同一个程序集
  并跑入口（`LoadModule` 复用已载入的同路径程序集）。JVM 上类按名从类路径加载，同理。
- **移植中修的其余 cleat 缺陷**并入 [cleat-064](cleat-064-port-found-runtime-fixes.md)：`spit` 不截断、顶层 `do` 子形式的源位置
  （`,source` / `,where` 对宏定义的 Var 依赖它）。
- **CLR 上的处理**（README 有完整清单）：SIGTERM 不触发 `ProcessExit`，登记清理时让 SIGTERM 经 `Environment/Exit` 退出；
  `System.Uri` 不收 `file:/路径`；CLR 异常只在抛出时才有栈；`TextWriter/Synchronized` 的包装不转发 `ToString`（测试里读内层的 StringWriter）。
- **另记**：讨论中确定的后续任务——gen-class 类型惰性启动运行时（[cleat-067](cleat-067-lazy-runtime-startup.md)，设计见
  [designs/17](../designs/17-lazy-runtime-startup.md)），排在 tools.build 之后。
