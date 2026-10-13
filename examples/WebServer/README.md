# WebServer：一个完整的 cleat 项目

一个 ASP.NET 小服务：HTTP 路由在 C#，处理逻辑用 Clojure 写。它演示 cleat 项目的几件事：

1. **依赖从自建 NuGet 仓库 BaGetter 来**：cleat 运行时、nREPL、neorepl、tools.build 都是 NuGet 包；
2. **用 neorepl 交互地开发**：对着项目起 REPL（jack-in），也可以连进**正在运行的服务**里改代码；
3. **`dotnet build` 混合 C# 与 Clojure**：一条命令构建 C# 库、Clojure 代码和 C# 主机；
4. **gen-class 给 C# 用**：Clojure 生成真实的 .NET 类型 `Web.Handlers`，实现 C# 接口，C# 端直接 `new`，不调用任何初始化 API；
   `Web.Cli` 是带 `Main` 的 gen-class，命令行与 C# 都能跑它；
5. **Clojure CLI 的执行模式**：`cleat -M` / `-X` / `-T`。

```
WebServer/
  .config/dotnet-tools.json  本地 dotnet tool：Cleat.Cli（命令 cleat），dotnet tool restore 从 BaGetter 装
  nuget.config            包从哪来：BaGetter（Cleat.*）与 nuget.org
  cleat.edn               cleat 项目：加载路径、NuGet 依赖、别名
  build.clj               构建脚本（tools.build）：prep / compile / test / pack / push / publish
  csharp/WebCore/         C# 库：接口 IHandlers、记录 Greeting、工具类 Slug
  src/web/handlers.clj    Clojure 处理函数，gen-class Web.Handlers 实现 WebCore.IHandlers
  src/web/dev.clj         在服务进程里起 nREPL（Cleat.Nrepl）
  src/web/cli.clj         带 -main 的 gen-class Web.Cli（命令行；C# 也能调它的 Main）
  test/web/               clojure.test
  host/                   ASP.NET 主机（C#），dotnet build 时顺带编译 Clojure
```

## 准备

- .NET 10 SDK。cleat 本身是 dotnet tool `Cleat.Cli`（命令 `cleat`，带运行时与 tools.build），本项目的 `.config/dotnet-tools.json`
  钉了版本：在本目录 `dotnet tool restore` 一次，之后用 `dotnet cleat …`（也可以 `dotnet tool install -g Cleat.Cli` 全局装，用 `cleat …`）。
  下文的命令都在本目录执行。
- 一个 BaGetter，地址写在 `nuget.config`（缺省 `http://localhost:5000/v3/index.json`）：

  ```sh
  docker run -d --name bagetter -p 5000:8080 -e ApiKey=<你的 key> -v bagetter-data:/data bagetter/bagetter
  ```

- 把 cleat 的包推上去（在 cleat 仓库里；key 用环境变量 `NUGET_API_KEY` 或 `:api-key`）：

  ```sh
  dotnet run tools/build.cs -- pack -o artifacts/nuget                  # Cleat.Runtime 与 Cleat.Cli
  for n in Cleat.Runtime Cleat.Cli; do
    dotnet nuget push artifacts/nuget/$n.1.0.3.nupkg --source bagetter \
        --configfile examples/WebServer/nuget.config --api-key "$NUGET_API_KEY"
  done
  cleat=$PWD/src/Cleat.Host/bin/Release/net10.0/cleat                   # 仓库里构建出的 cleat
  for p in tools.namespace tools.build nrepl neorepl; do                  # 按依赖顺序
    (cd packages/$p && $cleat -T:build install && $cleat -T:build push :source '"bagetter"' \
        :configfile '"../../examples/WebServer/nuget.config"')
  done
  ```

  `install` 先放进本地源 `~/.cleat/local-feed`，后面的包构建时从那里 restore 前面的包；`push` 推到 BaGetter。
  NuGet 只肯按**名字**推到 http:// 的源，而且那个源在 nuget.config 里要标 `allowInsecureConnections="true"`（本目录的 nuget.config 就是这样）。

## 1. 从 BaGetter 取依赖

`cleat.edn` 的 `:deps` 写 NuGet 包，和 C# 项目的 `PackageReference` 一样：

```clojure
:deps {Cleat.Nrepl {:nuget/version "1.8.1"}}
```

tools.build 的 `create-basis` 把它们交给 `dotnet restore`：源、凭据、包源映射都按 `nuget.config`，所以私有的 BaGetter 和 nuget.org
可以一起用（本例 `Cleat.*` 只从 BaGetter 取）。解析结果在 `.cleat/basis/`，依赖不变就不重新 restore。C# 主机的
`PackageReference Cleat.Runtime` / `Cleat.Nrepl` 走的是同一个 `nuget.config`。

## 2. 用 neorepl 开发

neorepl 本身也是 BaGetter 上的包，`cleat.edn` 的别名 `:neorepl` 把它加进依赖，`cleat -M:neorepl` 就像 `clj -M:neorepl`：

```sh
dotnet tool restore                 # 一次：装 Cleat.Cli
dotnet cleat -T:build prep          # 先把 C# 库编进 target/dotnet（cleat.edn 的 :paths 里有它）
dotnet cleat -M:neorepl             # REPL；没有运行着的 nREPL 服务时，为本项目起一个（jack-in）
```

jack-in 起的服务的加载路径就是项目的 basis（`:neorepl/aliases [:dev]` 加上 `test/`）：项目源码、C# 库、NuGet 依赖都在上面。

```clojure
user=> (require 'web.handlers)
user=> (web.handlers/greeting-text "repl")
"hello, repl!"
user=> (WebCore.Slug/Make "C# from Clojure")         ; C# 类型直接用
"c-from-clojure"
user=> (.Greet (Web.Handlers.) "gen-class")          ; gen-class 的类型在 REPL 里也有
#object[Greeting ...]
user=> (require 'web.handlers-test) (clojure.test/run-tests 'web.handlers-test)
```

其他用法：`dotnet cleat -M:neorepl eval '(+ 1 2)'`、`dotnet cleat -M:neorepl server`（只起服务，给编辑器连）、`dotnet cleat -M:test`（跑测试）。

### 连进正在运行的服务

主机在设置了 `WEB_NREPL_PORT` 时在自己的进程里起 nREPL（`src/web/dev.clj`）。处理函数经 Var 间接调用，在 REPL 里重定义立即生效：

```sh
WEB_NREPL_PORT=7888 dotnet run --project host -- --urls http://127.0.0.1:8080
curl 'http://127.0.0.1:8080/hello?name=cleat'          # {"name":"cleat","message":"hello, cleat!"}

dotnet cleat -M:neorepl -p 7888
user=> (in-ns 'web.handlers)
web.handlers=> (defn greeting-text [n] (str "hi there, " n "!"))

curl 'http://127.0.0.1:8080/hello?name=cleat'          # {"name":"cleat","message":"hi there, cleat!"}
```

## 3. `dotnet build` 混合 C# 与 Clojure

```sh
dotnet build host
```

按顺序做三件事：

1. 编 `csharp/WebCore`（主机的 `ProjectReference`）；
2. **编 Clojure**：`host/WebServer.csproj` 里的 MSBuild 目标 `CompileClojure` 运行 `dotnet cleat -T:build compile :dotnet-dir <WebCore 的输出目录>`，
   tools.build 的 `compile-clj` 把 `src/` 编成一个包程序集 `target/classes/Web.Handlers.dll`（对着 WebCore.dll 编译）。
   增量：源码、`cleat.edn`、`build.clj`、WebCore.dll 都没变时跳过；
3. 编 C# 主机：`Web.Handlers.dll` 是普通的程序集引用。`Cleat.Runtime` 包把 cleat 的镜像与驱动库复制到输出目录。

**为什么 C# 分成 `csharp/WebCore` 与 `host` 两个项目**：顺序是 C# → Clojure → C#。Clojure 代码要对着 C# 的接口与类型编译
（`Web.Handlers` 实现 `WebCore.IHandlers`、调 `WebCore.Slug`），C# 主机又要对着 Clojure 编出的类型编译（`new Web.Handlers()`）。
放在一个 C# 项目里，`IHandlers` 要在 Clojure 之前编、`Program.cs` 要在之后编，一次 C# 编译做不到；所以契约（接口、记录、工具类）
单独一个库，主机单独一个项目——和 Java 里"接口一个模块、Clojure 实现、调用方另一个模块"一样。

调 cleat 的命令是 MSBuild 属性 `CleatCommand`：缺省 `dotnet cleat`（本地工具），全局装的用 `-p:CleatCommand=cleat`；
示例放在 cleat 仓库里时自动用仓库里构建出的 cleat（`Directory.Build.props`）。
发布：`dotnet cleat -T:build publish`（`dotnet publish host`），或直接 `dotnet publish host`。

## 4. gen-class：给 C# 用的 .NET 类型

`src/web/handlers.clj`：

```clojure
(ns web.handlers
  (:import (WebCore Greeting Slug))
  (:gen-class
   :name Web.Handlers
   :implements [WebCore.IHandlers]
   :methods [^:static [StartNrepl [int] Object] ^:static [StopNrepl [] Object]]))

(defn -Greet [_this name] (Greeting. name (greeting-text name)))   ; 返回 C# 记录
(defn -Sum [_this numbers] (total numbers))                          ; long[] 进来
(defn -Slugify [_this title] (Slug/Make title))                      ; 调 C# 工具类
```

`host/Program.cs`：

```csharp
builder.Services.AddSingleton<IHandlers>(_ => new Web.Handlers());   // Clojure 实现的 C# 接口
app.MapGet("/hello", (string? name, IHandlers h) => h.Greet(name ?? ""));
```

没有 `CleatRuntime.Init()`：`Web.Handlers` 第一次被用到时，它的类型初始化器启动 cleat 运行时（惰性启动，designs/17），
再从包程序集加载 `web.handlers` 命名空间。并发的第一次请求只启动一次。

| 接口 | 例子 |
|---|---|
| `GET /hello?name=x` | `{"name":"x","message":"hello, x!"}` |
| `POST /sum`（JSON 数组） | `[1,2,3,4]` → `{"sum":10}` |
| `GET /slug?title=…` | `Hello, Cleat World` → `{"slug":"hello-cleat-world"}` |
| `GET /stats` | `{"requests":N}`（Clojure 的 atom） |

## 5. 带 `-main` 的 gen-class，以及 Clojure CLI 的几种执行模式

`src/web/cli.clj` 是一个命令行：`(:gen-class :name Web.Cli :main true)` 生成带静态 `Main(string[])` 的 .NET 类型，它调 `-main`。
同一段代码可以这样跑：

```sh
dotnet cleat -M:cli sum 1 2 3                       # clj -M：别名 :cli 的 :main-opts 是 ["-m" "web.cli"]
dotnet cleat -M -m web.cli greet Bob                # 不用别名，直接 -m
dotnet run --project host -- cli slug "Hello C#"    # C# 主机调 Web.Cli.Main(args)，同样不需要初始化
```

`cleat.edn` 里还有一个给 `-X` 用的别名，函数接收一个 map：

```clojure
:greet {:exec-fn web.cli/greet :exec-args {:name "world"}}
```

```sh
dotnet cleat -X:greet                              # hello, world!
dotnet cleat -X:greet :name '"you"'                # 键值对并在 :exec-args 之上
dotnet cleat -X web.cli/greet :name '"there"'       # 不用别名，函数写全名
dotnet cleat -T:build test                          # clj -T：build.clj 的 build/test（不带项目依赖的工具函数）
dotnet cleat -Spath -M:dev                          # 打印加载路径；-P 只下载依赖
```

| 模式 | 跑什么 | 加载路径 |
|---|---|---|
| `-M[:别名]` | `-main`（别名的 `:main-opts`，或命令行的 `-m ns`） | 项目的 basis（加别名） |
| `-X[:别名]` | 一个函数，参数是 map（`:exec-fn` / `:exec-args` / `:ns-default`） | 项目的 basis（加别名） |
| `-T:别名` | 工具函数（`:ns-default`；没有这个别名时名字就是命名空间，`-T:build` 即 build.clj） | 只有工具别名的 `:deps` / `:paths`，缺省项目根目录 |

## 把 Clojure 部分发布成包

```sh
dotnet cleat -T:build pack     # target/Web.Handlers.1.0.0.nupkg：Web.Handlers.dll 与 WebCore.dll，依赖 Cleat.Runtime、Cleat.Nrepl
dotnet cleat -T:build push     # 推到 nuget.config 的 bagetter（key：$NUGET_API_KEY 或 :api-key）
```

别的 .NET 项目 `dotnet add package Web.Handlers` 后同样直接 `new Web.Handlers()`。
