# src/scheme — Scheme 元层的源码

Scheme 元层（designs/15）的 Scheme 一侧：boot 库与展开器、nanopass 编译器、nanopass 框架、文本种子与构建脚本。
C# 一侧在 `src/Cleat.Scheme`（运行时）、`src/Cleat.Stage0`（Lcore 解释器）、`src/Cleat.Emit`（发射器）。

## 来源

从 IronScheme 仓库（https://github.com/IronScheme/IronScheme ，本地 /home/david/workspace/IronScheme）的 `src/scheme/`
纳入（cleat-046，2026-10-10），与 `src/Cleat.Scheme` 取自同一提交 `26386793ee27f478a2e69ccdc17bc10a3a134944` 及其工作树
（iron-034 的未提交改动）。许可：BSD 3-Clause（`src/Cleat.Scheme/LICENSE-BSD.txt`）；`boot/expander/` 与两个构建脚本
源自 psyntax（MIT 式声明，Abdulaziz Ghuloum 与 Kent Dybvig）；`nanopass/` 是 nanopass-framework-scheme 的快照
（MIT，见其 `Copyright`）。仓库根目录 `NOTICE` 有条目。

## 布局

| 目录 | 来源 | 说明 |
|---|---|---|
| `boot/` | IronScheme `src/scheme/boot/` | 编进 boot 镜像的库（`(boot …)`、`expander/` 展开器）、原语表 `primitives.ss`（由 `Cleat.PrimGen` 生成）、文本种子 `boot-seed.core`、构建脚本 `ironscheme-buildscript-nano.sps`（Phase A / B）与 `seed-buildscript.sps`（种子再生成） |
| `lib/ironscheme/compiler/`、`lib/ironscheme/compiler.sls` | `src/scheme/lib/ironscheme/compiler/` | nanopass 编译器与驱动；`async.sls` 不纳入，其中的 `lower-loops` 拆到 `loops.sls` |
| `lib/ironscheme/{clr,contracts,contracts-helper,integrable,typed}.sls`、`lib/ironscheme/{clr,syntax,typed}/` | `src/scheme/lib/ironscheme/` | boot 源码、编译器与 nanopass 适配库导入的、不在镜像里的库（从源码加载） |
| `lib/nanopass/implementation-helpers.ironscheme.sls` | `src/scheme/lib/nanopass/` | IronScheme 对 nanopass 的覆盖适配库（在搜索路径里先于 `nanopass/` 找到） |
| `nanopass/` | 子模块 `src/scheme/lib/nanopass-framework-scheme/` | nanopass 框架原样快照（不含 `.git`） |
| `clojure/` | cleat 新增（cleat-048 起） | 按需加载、不进 Scheme 镜像的 Clojure 侧库，如 `(clojure syntax-kinds)`（把 Clojure 集合登记为语法扩展）、`(clojure namespaces)`（命名空间模型，cleat-049）、`(clojure syntax)`（特殊形式与 defmacro 垫片，cleat-050）；库名 `(clojure x)` 由搜索目录 `.` 找到 |

库名暂时仍是 `(ironscheme …)` / `(boot …)` / `(nanopass …)`（cleat-054 改名）。psyntax 的文件定位器把 `(ironscheme compiler parse)`
映射到 `<搜索目录>/ironscheme/compiler/parse.sls`，所以编译器放在 `lib/ironscheme/compiler/` 而不是设计 15 写的 `src/scheme/compiler/`。

## 运行

在本目录下（库搜索路径 `-I lib -I nanopass`）：

```
dotnet ../Cleat.Stage0/bin/Release/net10.0/stage0.dll -I lib -I nanopass boot/boot-seed.core boot/ironscheme-buildscript-nano.sps ../boot   # Phase A → src/boot/cleat.scheme.boot.dll
dotnet ../Cleat.Stage0/bin/Release/net10.0/stage0.dll -I lib -I nanopass --boot ../boot/cleat.scheme.boot.dll <脚本.sps>            # 在镜像上运行脚本
```

`dotnet build` 经 `src/Cleat.Host/Cleat.Host.csproj` 的 `GenerateSchemeBootImage` 目标自动做 Phase A；`tools/build.cs` 有
`scheme-stage1` / `seed` / `ilverify`；自举不动点与编译器快照测试在 `tests/Cleat.Scheme.Tests`（`Bootstrap`、`SchemeScript`）。
