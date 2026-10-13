# tools.build（cleat 移植）

[tools.build](https://github.com/clojure/tools.build)（上游提交 `aef6def`）移植到 cleat：用 Clojure 写构建脚本，编包程序集、
构建 C# / F# 项目、打 NuGet 包、装进本地源、推到 nuget.org 或 BaGetter 等自建仓库。设计见 [designs/18](../../designs/18-tools-build.md)。
依赖 [tools.namespace](../tools.namespace)（部分移植）。

## 用法

项目根目录的 `cleat.edn`：

```clojure
{:paths ["src"]
 :deps {Cleat.Nrepl {:nuget/version "1.8.1"}    ; NuGet 包，源与凭据按 nuget.config（BaGetter 等）
        my/util {:local/root "../util"}}         ; 本地的另一个 cleat 项目
 :aliases {:test {:extra-paths ["test"]}}}
```

`build.clj`（与上游的写法相同，任务名按 cleat 换了几个）：

```clojure
(ns build
  (:require [clojure.tools.build.api :as b]))

(def lib 'My.Lib)
(def version "1.0.0")

(defn pack [_]
  (b/delete {:path "target"})
  (let [basis (b/create-basis)]
    (b/compile-clj {:basis basis :class-dir "target/classes" :lib lib})   ; target/classes/My.Lib.dll
    (b/pack {:basis basis :lib lib :version version :class-dir "target/classes"
             :nupkg-file (format "target/%s.%s.nupkg" lib version) :license "EPL-1.0"})))

(defn install [_]
  (pack nil)
  (b/install {:nupkg-file (format "target/%s.%s.nupkg" lib version)}))
```

运行（相当于 `clj -T:build pack`；`-T` 与 `-M` 是 cleat 宿主自带的，cleat-071）：

```sh
cleat -T:build pack
cleat -T:build install :version '"1.0.1"'      # 键值对按 EDN 读，并入参数 map
```

cleat 宿主作为 dotnet tool 发布（包 `Cleat.Cli`，命令 `cleat`，随附 tools.build 与 tools.namespace 的包程序集）：
`dotnet tool install -g Cleat.Cli`，或在项目里 `dotnet new tool-manifest && dotnet tool install Cleat.Cli` 后用 `dotnet cleat …`。
在 cleat 仓库里直接用构建出的宿主时，`-T` / `-M` 用仓库里 `packages/tools.build/src` 与 `packages/tools.namespace/src` 的源码。

`packages/nrepl/build.clj`、`packages/neorepl/build.clj` 是完整的例子（neorepl 经 NuGet 依赖 Cleat.Nrepl）。

运行项目本身，照 Clojure CLI 的执行模式（cleat-069、071、072；完整的表见 designs/18 §5）：

```sh
cleat -M:neorepl                         # clj -M：按 cleat.edn 的别名建 basis，跑别名的 :main-opts
cleat -M -m my.app arg                   # 不用别名也行
cleat -X:deploy :env :prod               # clj -X：调函数（别名的 :exec-fn / :exec-args / :ns-default），参数是 map
cleat -T:mytool fn :k v                  # clj -T：工具别名的 :deps / :paths，不带项目的依赖
cleat -Spath -M:dev                      # 打印加载路径；-P 只下载依赖；-Sdeps '{…}' 临时并入
```

宿主按别名建 basis，把它的加载路径加进**本进程**，再按别名的 `:cleat-opts` + `:main-opts`（如 `["-m" "neorepl.main"]`）+ 命令行其余参数
运行，与 `clj -M` 一样不另起进程。一个完整的项目（BaGetter、neorepl、`dotnet build` 混合 C#、gen-class）见 `examples/WebServer`。

tools.build 自己也是包：`Cleat.ToolsBuild`（依赖 `Cleat.ToolsNamespace`），各自的 `build.clj` 构建与发布。

## 与上游的对应

| 上游 | cleat |
|---|---|
| `delete` `copy-file` `write-file` `copy-dir` `zip` `unzip` `process` `git-process` `git-count-revs` `resolve-path` `set-project-root!` `with-project-root` | 同名同参数；路径是字符串（`resolve-path` 返回字符串，不是 `java.io.File`） |
| `create-basis` | 读 `cleat.edn`：`:nuget/version` 依赖由 `dotnet restore` 解析（在项目目录下的 `.cleat/basis/` 生成临时项目，`nuget.config` 照常生效；本地源作附加源），`:local/root` 是本地 cleat 项目；`:paths` 可以写别名；`:argmap` 是别名合并的参数（如 `:cleat-opts`）。`Cleat.Runtime` 与只被它依赖的包不进加载路径 |
| `java-command` | `cleat-command`（`:cleat-cmd` `:cp` `:basis` `:cleat-opts` `:main` `:main-args`），加载路径每项一个 `-I` |
| `compile-clj` | 编出**包程序集** `<class-dir>/<程序集名>.dll`（designs/17）：`:assembly`，缺省 `:package-id` 或 `:lib`（`my.org/lib` → `my.org.lib`）；`:ns-compile` / `:sort` / `:bindings` / `:filter-nses` 照旧；`:cleat-cmd` / `:cleat-opts` 代替 `:java-*` |
| `javac` | `dotnet-build`：`dotnet build` 一个 `.csproj` / `.fsproj` / `.sln`，`:output-dir`、`:configuration`、`:properties`；输出目录放进加载路径后 Clojure 按类型名直接用 |
| `write-pom` `pom-path` `jar` | `pack`：直接写 nupkg（`lib/net10.0/` 是 `:class-dir` 的内容），依赖 `Cleat.Runtime`（缺省为运行着的版本起）与 `:deps` 里的 NuGet 包；`:local/root` 依赖报错 |
| `install` | nupkg 放进本地源（缺省 `~/.cleat/local-feed`，或 `CLEAT_LOCAL_FEED`、`:local-feed`），并删掉 NuGet 全局包目录里同 id 同版本的缓存 |
| — | `push`：`dotnet nuget push`，`:source`（nuget.config 里的名字或 URL；BaGetter 是 `http://主机/v3/index.json`）、`:api-key`（缺省 `NUGET_API_KEY`） |
| `uber` | 可部署**目录** `:uber-dir`：宿主与运行时在根目录，`:class-dir` 与依赖包在 `lib/`（上游的排除与冲突处理器照旧），`:main` 时写 `run` / `run.cmd` |

没有 clojure.spec：参数检查是谓词。cleat 不报告反射，上游的 `test-capture-reflection` 不移植；jar 相关的测试按目录改写。

## 测试

```sh
cleat -I packages/tools.build/src -I packages/tools.namespace/src -I packages/tools.build/test packages/tools.build/test/run.clj
```

测试项目在 `test-data/`（上游的改为 `cleat.edn`，Java 源码换成 C# 项目），每个测试复制到临时目录里跑；NuGet 的测试用存根运行时包
与临时的全局包目录，不连网、不碰用户的 NuGet 缓存。`dotnet test` 经 `tests/Cleat.Host.Tests/PackageTests.cs` 运行；
用真的 `Cleat.Runtime` 构建 nrepl / neorepl 并 restore 的验收在 `NuGetColdLoadTests`。许可证同上游（EPL 1.0）。
