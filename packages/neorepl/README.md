# neorepl（cleat 移植）

[neorepl](https://github.com/nrepl/neorepl) 0.1.0（上游提交 `41d8b3a`）移植到 cleat：基于 nREPL 的终端 REPL 与命令行工具。
交互式 REPL 带行编辑器（多行输入、高亮、补全、光标后的参数提示、C-x C-d 看文档、历史按项目保存、Ctrl-C 中断求值），
CIDER 风格的命令（`,doc` `,source` `,apropos` `,macroexpand` `,pst` `,where` `,edit` `,reload` `,help`），以及
`eval` `load` `doc` `source` `apropos` `complete` `macroexpand` `where` `reload` `pst` `interrupt` `sessions` `ports` `check`
`server` 等子命令。依赖本仓库的 nREPL 包（`packages/nrepl`）做客户端与进程内服务；许可与上游相同（EPL 1.0，见 `LICENSE`）。

它只说 nREPL，所以除了 cleat 的服务，也能连 JVM Clojure、babashka 等任何 nREPL 服务。

## 用法

```shell
# bin/neorepl 用 PATH 上的 cleat（或 $CLEAT 指定的）运行
packages/neorepl/bin/neorepl                 # REPL：连当前目录或上级目录 .nrepl-port 里的服务，没有就为项目起一个
packages/neorepl/bin/neorepl eval '(+ 1 2)'
packages/neorepl/bin/neorepl doc map
packages/neorepl/bin/neorepl --in-process    # 服务就在 neorepl 自己的进程里
packages/neorepl/bin/neorepl server --port 7888

# 不用脚本时
cleat -I packages/nrepl/src -I packages/neorepl/src -m neorepl.main [命令] [选项]
```

`neorepl --help` 与 `neorepl 命令 --help` 有完整说明。

**jack-in**：没有服务可连时，REPL 为项目起一个服务，结束时停掉；`neorepl server` 起一个常驻的。项目是带 `cleat.edn` 的目录
（当前目录或最近的上级目录），服务是 cleat 运行 `nrepl.cmdline`，加载路径上是 neorepl 自己用的 nREPL 包与**项目的 basis**
（tools.build 的 `create-basis`，cleat-069）：`:paths`、`:deps` 里的 NuGet 包（按 `nuget.config` restore，BaGetter 等私有源可用）与
`:local/root` 项目、`:neorepl/aliases` 所选别名的路径。tools.build 不在加载路径上时只用 `:paths`（有 `:deps` 时报错）。

```clojure
;; cleat.edn，键都可省
{:paths ["src"]                                  ; 缺省 ["src"]
 :deps {Some.Lib {:nuget/version "1.0.0"}}        ; 服务的加载路径上也有
 :aliases {:dev {:extra-paths ["test"]}}
 :neorepl/aliases [:dev]                         ; jack-in 用的别名
 :middleware [my.app/wrap]}                      ; 交给 nrepl.cmdline 的 --middleware
```

**安装**：`bin/neorepl` 从源码运行（带上 nrepl、tools.build、tools.namespace 的源码）。也可以经 NuGet：包 `Cleat.Neorepl`
（依赖 `Cleat.Nrepl`、`Cleat.ToolsBuild`），在项目的 `cleat.edn` 里写别名
`:neorepl {:extra-deps {Cleat.Neorepl {:nuget/version "0.1.4"}} :main-opts ["-m" "neorepl.main"]}`，用 `cleat -M:neorepl` 启动
（完整的例子见 `examples/WebServer`）。

**启动时间**：从源码加载 neorepl 与 nREPL 每次约 7 秒；从包程序集（`Cleat.Neorepl` / `Cleat.Nrepl`，cleat-067）约 1.1 秒。

## 测试

```shell
cleat -I packages/nrepl/src -I packages/neorepl/src -I packages/neorepl/test packages/neorepl/test/run.clj [命名空间正则]
```

上游的测试套件逐个移植（86 个测试 704 条断言，约 1.5 分钟；源码与 AOT 两种加载都通过）；`NEOREPL_TEST_VERBOSE=1` 打印每个
测试的名字与耗时。`dotnet test` 经 `tests/Cleat.Host.Tests/PackageTests.cs` 跑它。

## 与上游的差异

- **行编辑器**：上游用 JLine；这里是 Clojure 写在 System.Console 上的 `neorepl.editor`（用户选定），对外的函数不变：
  多行输入（形式不完整时 Enter 换行，续行提示 `#_=>`）、按词法高亮、TAB 补全（唯一候选直接补全，否则补公共前缀或列出候选）、
  光标在末尾时的参数提示（eldoc）、历史文件（Up / Down、Ctrl-R 反向搜索）、读行时到来的输出印在提示上方、C-x C-d 文档、
  Emacs 式编辑键（Ctrl-A/E/B/F/K/U/W/Y/L、Alt-B/F/D/Backspace）。键盘读自 `Console.ReadKey`；读行时 Ctrl-C 是按键（清空本行），
  其余时候是 `Console.CancelKeyPress`（中断求值）。测试用的终端读 TextReader 里的按键序列（含转义序列与括号粘贴），写到 TextWriter。
  未做 JLine 的：括号粘贴模式只在测试终端上识别（真实控制台上粘贴按逐键处理）、vi 键位、补全菜单的光标选择。
- **jack-in**：只认 cleat 项目（用户选定）；上游的 Leiningen、Clojure CLI、babashka、shadow-cljs 及 cider-nrepl / piggieback
  的注入都去掉了，`--tool` 只有 cleat，没有 `--aliases`。服务在 `sh -c 'trap "" INT; exec …'` 下运行，终端的 Ctrl-C 不会传给它；
  停止时对服务及其子进程（`pgrep -P` 找）发 SIGTERM，5 秒后 SIGKILL（.NET 没有 ProcessHandle.descendants）。
- **进程与信号**：关闭钩子是 `ProcessExit`；.NET 的控制台程序收到 SIGTERM 时不触发它，所以登记清理时一并让 SIGTERM 经
  `Environment/Exit` 退出（`nrepl.socket/exit-on-sigterm!`）。被中断的命令线程用零睡眠清掉待处理的中断（CLR 没有 Thread.interrupted）。
- **文件**：路径用字符串（java.io.File 的位置）；会话缓存的锁是以 `FileShare/None` 打开的锁文件（另一进程持有时重试，JVM 是
  FileChannel.lock），替换缓存用 `File/Move`；历史文件名是项目路径 SHA-256 的十六进制，与上游相同。
- **服务端给出的东西**：`:ex` 是 CLR 类型名；`file:/路径` 形式的 URL 自己拆（System.Uri 不收没有 authority 的写法）；
  `pst` 把 cleat 宿主自己的栈帧（`Cleat.*`、`System.*`）也算作工具帧。
- **标准输入**：`neorepl eval -` 与 `check -` 用 `.ReadToEnd` 读（cleat 的 `slurp` 不收一般的 TextReader）。

测试的改写都在测试文件里以 `cleat:` 注释说明，主要是：jack-in / server / kill 三组改用 cleat 项目与 `kill -TERM`；
运行时错误的位置（cleat 编译的代码在 CLR 栈帧里没有源文件位置，报不出 `broken.clj:2`）；CLR 异常只在抛出时才有栈；
.NET 正则没有 `\Q…\E`。
