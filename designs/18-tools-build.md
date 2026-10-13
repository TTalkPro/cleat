# 18 tools.build 包：cleat 项目的构建、NuGet 依赖与发布

状态：**实现**（2026-10-13，任务 [cleat-066](../tasks/cleat-066-tools-build-package.md)；建立在 [designs/17](17-lazy-runtime-startup.md) 的包程序集与 `Cleat.Runtime` 之上）。

## 1. 目标

照 Clojure 官方的构建方案 [tools.build](https://github.com/clojure/tools.build)（上游 `aef6def`）给 cleat 一个**用 Clojure 写构建脚本**的库，
作为包 `packages/tools.build` 提供，不往 `tools/build.cs`（仓库自己的构建）里加命令。用户决定（2026-10-13）：

- 依赖与发布用 **NuGet**，兼容 **BaGetter** 等自建仓库（源与凭据按 `nuget.config`）；
- 能用 `dotnet build` 直接构建 C# / F# 项目，产物给 Clojure 代码用；
- 能 `pack` 出 nupkg、推到 nuget.org / BaGetter；包里是 designs/17 的包程序集，C# / F# 项目引用后冷加载。

## 2. 项目文件 `cleat.edn`

与 neorepl 的 jack-in 已经读的文件同一个（`:paths`、`:middleware` 照旧），加上依赖与别名，形状照 `deps.edn`：

```clojure
{:paths ["src"]
 :deps {Cleat.Nrepl {:nuget/version "1.8.1"}      ; NuGet 包（id 是符号），版本或区间（NuGet 语法）
        my/util {:local/root "../util"}}           ; 本地的另一个 cleat 项目：它的 :paths 与 :deps
 :aliases {:test {:extra-paths ["test"]
                  :extra-deps {Some.Lib {:nuget/version "2.0.0"}}}}}
```

## 3. basis（`create-basis`）

返回与上游同形的 map：合并后的 `cleat.edn` 加 `:libs`、`:classpath`、`:classpath-roots`（加载路径，按顺序），另有 `:argmap`（所选别名合并的参数）。

- **NuGet 依赖的解析**交给 NuGet 自己：在项目目录下的 `.cleat/basis/` 生成一个只有 `PackageReference` 的临时项目，运行 `dotnet restore`
  ——临时项目在项目目录树里，`nuget.config`（源、凭据、BaGetter 地址）按 NuGet 的规则从项目目录往上找到；本地源（§6）存在时作为附加源。
  读 `obj/project.assets.json`：每个包的运行时程序集（`runtime` 条目）所在目录进 `:classpath-roots`，传递依赖与 `:dependents` 照 assets 记。
  依赖没变时（哈希记在 `.cleat/basis/`）不重跑 restore。
- `Cleat.Runtime` 与只被它依赖的包不进加载路径：运行构建与程序的 cleat 自己就是运行时。
- `:local/root`：读那个目录的 `cleat.edn`，它的 `:paths`（按那个目录解析）进加载路径，它的 `:deps` 递归并入。
- 运行时怎么用加载路径：宿主的 `-I`。目录里的包程序集由加载器登记（designs/17 §2.3），C# 库的 dll 由加载器的程序集解析按目录找到。

## 4. 任务一览（`clojure.tools.build.api`）

| 上游 | cleat | 说明 |
|---|---|---|
| `delete` `copy-file` `write-file` `copy-dir` `zip` `unzip` `resolve-path` `set-project-root!` `with-project-root` | 原样 | System.IO / System.IO.Compression；glob 自己实现（`*` `**` `?` `{a,b}` `[…]`） |
| `process` `git-process` `git-count-revs` | 原样 | System.Diagnostics.Process |
| `create-basis` | 改写 | §3 |
| `java-command` | **`cleat-command`** | `:cleat-cmd`（缺省：运行着的这个 cleat）、`:cp`、`:basis`、`:cleat-opts`、`:main`、`:main-args` → `{:command-args […]}` |
| `compile-clj` | 改写 | 子进程 `cleat --compile-package`（designs/17）：`:class-dir` 下写 `<程序集名>.dll`；命名空间与顺序照上游（`:ns-compile`，或 tools.namespace 的 `:topo` / `:bfs`）；`:bindings` 照上游 |
| `javac` | **`dotnet-build`** | `dotnet build` 一个 `.csproj` / `.fsproj` / `.sln`，`:output-dir` 进 `:class-dir` 或交给调用方放进加载路径 |
| `write-pom` `pom-path` `jar` | **`pack`** | 直接写 nupkg（OPC zip：`<id>.nuspec`、`[Content_Types].xml`、`_rels/.rels`、`lib/net10.0/`）。依赖：`Cleat.Runtime`（缺省为运行着的版本，`[版本, )`）与 basis 的 NuGet 依赖；`:local/root` 依赖不能进 nupkg，报错 |
| `install` | 改写 | nupkg 放进本地源（§6），并删掉 NuGet 全局包目录里同 id 同版本的缓存（否则重装同一版本 restore 仍取旧包） |
| — | **`push`** | `dotnet nuget push`：`:source`（nuget.config 里的名字或 URL，BaGetter 是 `…/v3/index.json`）、`:api-key`（缺省环境变量 `NUGET_API_KEY`） |
| `uber` | 改写 | 可部署**目录** `:uber-dir`：宿主与运行时（程序集、两个镜像、驱动库、随附库）在根目录，`:class-dir` 与依赖包的程序集在 `lib/`（上游的冲突处理器照旧作用于同路径文件），`:main` 时写启动脚本 `run` / `run.cmd`（`dotnet cleat.dll -I lib -m main`） |

参数检查照上游的 `assert-required` / `assert-specs`，但 cleat 没有 clojure.spec：`assert-specs` 用谓词（路径是字符串、路径集合、lib 是符号、basis 是 map）。

## 5. 运行构建脚本与项目

上游靠 Clojure CLI。cleat 把它的执行模式做进宿主（cleat-071、072），语义照 CLI：

| 命令 | 作用 |
|---|---|
| `cleat -M[:别名…] [-m ns] 参数…` | 跑 `-main`：按 `cleat.edn` 与别名建 basis，加载路径加进本进程，接着跑别名的 `:cleat-opts` / `:main-opts` 与其余参数；别名没有 `:main-opts` 又没有别的参数时进 REPL |
| `cleat -X[:别名…] [ns/fn] :键 值…` | 调函数，参数是一个 map：项目的 basis；函数取命令行的（不限定时用别名的 `:ns-default`）或别名的 `:exec-fn`；参数是别名的 `:exec-args` 并上键值对（键可以是路径 `[:a :b]`） |
| `cleat -T:别名 fn :键 值…`（也写 `-T别名`） | 工具函数，**不带项目的 deps 与 paths**：别名的 `:deps`、`:paths`（缺省 `["."]`）、`:ns-default`、`:exec-args`；`cleat.edn` 没有这个别名时名字就是命名空间，加载路径是项目根目录——`-T:build pack` 调 build.clj 的 `build/pack`（tools.build 随附） |
| `-Sdeps EDN` / `-Spath` / `-P` | 并在 `cleat.edn` 之上的临时配置 / 打印加载路径 / 只解析与下载依赖 |

没有的：`-A`（CLI 已弃用，报错提示改用 `-M` / `-X`）、`-Ttools install` 与已安装的工具（cleat 的工具走 dotnet tool）、`-J`（没有 JVM）、`-Srepro`
（没有用户级的 deps 文件）。

这些模式都要 tools.build：宿主作为 dotnet tool 发布（`Cleat.Cli`，命令 `cleat`），工具包里随附 `Cleat.ToolsBuild` / `Cleat.ToolsNamespace`
的包程序集（镜像旁的 `tools/`，比镜像旧时不用）；开发布局（仓库里构建出的 cleat）下用仓库里的源码，不用随附的那份（它是 pack 的构建产物，
会落后于源码）。项目里用 `.config/dotnet-tools.json` 钉住版本，`dotnet tool restore` 之后 `dotnet cleat …`——源与 BaGetter 照 `nuget.config`。
（cleat-069 时这两个入口是 `cleat-build` / `cleat-run` 两段脚本，按用户意见换成了工具。）

## 6. 本地源

相当于 `~/.m2`：缺省 `~/.cleat/local-feed`（环境变量 `CLEAT_LOCAL_FEED` 或参数 `:local-feed` 覆盖），平铺的 nupkg 目录（NuGet 的本地目录源）。
`install` 写入它，`create-basis` 的 restore 把它作为附加源——不改用户的 `nuget.config`。

## 7. tools.namespace

`compile-clj` 的 `:topo` / `:bfs` 用上游 tools.namespace（`28d3f57`）的 `find` / `parse` / `dependency`，移植为独立的包 `packages/tools.namespace`：
`dependency`、`track` 原样；`parse` 用 `clojure.core/read`（读者条件）代替 tools.reader；`file` 换 System.IO；`find` 只保留目录（类路径与 jar 的搜索在 CLR 上没有对应物）。

## 8. 不做

- `write-pom` 的 Maven 元数据、`:mvn/*` 依赖、git 依赖（`:git/url`）：NuGet 不认它们；git 依赖以后可以按 `:local/root` 的样子做。
- 别名式启动 `cleat -X:alias` / `-M:alias`（宿主的命令行）：可以建在 `create-basis` 与 `clojure.tools.build.main` 之上，另行决定。

## 9. 实现记录（cleat-066）

- **普通程序集按类型名加载**：`dotnet-build` 的产物、NuGet 包里的 C# 库放进 `-I` 目录后，Clojure 代码要能直接写类型名（JVM 上类路径里的类按名加载）。
  加载器加了 `Loader.FindTypeOnLoadPath`：类型名在别处都找不到时，按元数据给加载路径目录里的 `*.dll`（不递归）建公有类型表（目录变了重建），
  命中再载入程序集（`RT.classForName` 的最后一站）。
- **`compile-clj` 的子进程**：照上游写编译脚本——require `:bindings` 里 Var 的命名空间，在 `with-bindings` 里调编译驱动
  `Cleat.Scheme.Clojure.Driver/CompilePackage`；`:filter-nses` 把包的根换成对应的子目录与文件（驱动的根检查因此也接受文件）。
- **`process`**：`:inherit` 而 `*out*` / `*err*` 不是控制台时（REPL、测试捕获输出）转写到 `*out*` / `*err*`；`:write` / `:append` 建父目录。
- **反射**：cleat 不报告反射（`*warn-on-reflection*` 无输出），上游的 `test-capture-reflection` 不移植。
- **版本序**：`pack` 缺省依赖 `Cleat.Runtime >= 运行着的版本`（现在是 `1.0.0`）；SemVer 里带预发布标签的版本低于同号正式版、标签之间按字典序比较，测试打的运行时包要不低于它（就用 `1.0.0`）。
- **neorepl 的 jack-in** 仍只读用户项目 `cleat.edn` 的 `:paths` / `:middleware`，没有按 basis 解析 `:deps`（可以建在 `create-basis` 上，另行决定）。
  ——cleat-069 已改：见 §10。

## 10. 示例项目带来的补充（cleat-069）

- **`cleat -M:别名`**（cleat-071 起是宿主的选项，§5；cleat-069 时是脚本 `cleat-run` 与 `clojure.tools.build.main` 的 `-M` 分支）：相当于 `clj -M:alias`，按别名建 basis，
  运行 `cleat -I … <:cleat-opts> <:main-opts> 参数…`，终端交给子进程。与 §5 的 `cleat -T:build 函数`（`clj -T`）并列。
- **neorepl 的 jack-in 用 basis**：tools.build 在加载路径上时，服务的加载路径是项目的 basis（`:neorepl/aliases` 选别名）；
  `Cleat.Neorepl` 因此依赖 `Cleat.ToolsBuild`。
- **tools.build、tools.namespace 也是 NuGet 包**：`Cleat.ToolsBuild`、`Cleat.ToolsNamespace`（版本从上游的 0.10.14、1.5.1 起，之后按修正与运行时更新递增：cleat-070 时为 0.10.16、1.5.2）。包程序集对着某个 Clojure 镜像编译，镜像一变（`Cleat.Runtime` 新版本）就要重编并发新版本。
- **`push` 到 http:// 源**：NuGet 只按 nuget.config 里标了 `allowInsecureConnections` 的**源名字**推送（BaGetter 常见）；`push` 加 `:configfile`，
  在项目根目录运行（源名字按项目的 nuget.config 查）。
- **basis 读回退包目录**：`project.assets.json` 的 `packageFolders` 里第一个是全局包目录，其后是回退目录；从回退目录解析到的包只在那里，
  路径按实际存在的那个目录拼。
- **运行时扫描应用依赖**（cleat-067 的 `PackageIndex`）不能按 `Cleat.` 前缀跳过程序集：包程序集也可以叫 `Cleat.*`（`Cleat.Nrepl`），
  只跳过运行时自己的那几个。
