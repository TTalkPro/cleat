# cleat 的包：版本表与依赖图（1.2.0，cleat-187）

运行时与工具（`CoreInfo.Version`，`tools/build.cs pack` 打包）：

| 包 | 版本 | 内容 |
|---|---|---|
| `Cleat.Runtime` | 1.2.0 | Cleat.Core、Cleat.Hosting、Cleat.Compiler 程序集 + Clojure 镜像与随附库；C# / F# 项目引用它 |
| `Cleat.Cli` | 1.2.0 | `cleat` 命令（dotnet tool） |

`packages/` 下的包（各自是 cleat 项目，`cleat -T:build install | push`；版本在各自的 `build.clj`）：

| 包 | 版本 | 来源 | cleat 的改动 | 依赖（NuGet） | 首次发布 |
|---|---|---|---|---|---|
| `Cleat.ToolsNamespace` | 1.5.5 | clr.tools.namespace 1.5.x | 无 | — | 1.0（cleat-066），1.2.0 重编为修订 5 |
| `Cleat.ToolsBuild` | 0.10.21 | cleat 自己的 tools.build（API 照 clojure/tools.build） | cleat 原创 | ToolsNamespace 1.5.5 | 1.0（cleat-066），1.2.0 重编 |
| `Cleat.Nrepl` | 1.8.4 | nREPL 1.8.0 的移植 | 移植改写 | — | 1.0（cleat-062），1.2.0 重编为修订 4 |
| `Cleat.Neorepl` | 0.1.5 | neorepl 0.1.0 的移植 | 移植改写 | Nrepl 1.8.4、ToolsBuild 0.10.21 | 1.0（cleat-063），1.2.0 重编为修订 5 |
| `Cleat.Nanopass` | 1.0.0 | nanopass 框架的 Clojure 移植（MIT） | cleat 原创 | — | 1.2.0（cleat-182 从编译器拆出） |
| `Cleat.SpecAlpha` | 0.6.250 | clr.spec.alpha（含 core.specs.alpha） | 1 个文件 | —（生成器按需用 TestCheck） | 1.2.0（cleat-166） |
| `Cleat.TestCheck` | 1.1.4 | clr.test.check 1.1.4 | 1 个文件（shrink-long） | — | 1.2.0（cleat-166） |
| `Cleat.ToolsReader` | 1.5.2 | clr.tools.reader 1.5.2 | 无（cleat-183 起与上游相同） | — | 1.2.0（cleat-167） |
| `Cleat.ToolsAnalyzer` | 1.4.0 | clr.tools.analyzer(.clr) | 3 个文件（proxy-class* 等，cleat-184） | ToolsReader 1.5.2 | 1.2.0（cleat-167） |
| `Cleat.CoreAsync` | 1.9.865 | clr.core.async | 3 个文件（timers，cleat-185；by-ref） | ToolsAnalyzer 1.4.0、ToolsReader 1.5.2 | 1.2.0（cleat-167） |
| `Cleat.DataGenerators` | 1.1.0 | clr.data.generators 1.1.0 | 无 | — | 1.2.0（cleat-191） |
| `Cleat.TestGenerative` | 1.1.2 | clr.test.generative 1.1.2 | 无 | DataGenerators 1.1.0、ToolsNamespace 1.5.5 | 1.2.0（cleat-191） |

每个包都还依赖 `Cleat.Runtime [1.2.0]`（精确版本，tools.build 的 `pack` 自动加）。

## 版本规则

- 包的版本 = 上游版本；同一上游版本**为新的运行时重编**时末位加一（"修订"）：包程序集绑定编它时的镜像，别的运行时上加载会抛 StaleModuleException，
  所以每次运行时发版都要重编所有包，而 NuGet 不允许重推同一版本。1.1.0 时发布过的四个包（tools.namespace、tools.build、nrepl、neorepl）因此在 1.2.0 各加一；
  M19 ~ M23 新增的包没有发布过，以上游版本首发。
- 包对 `Cleat.Runtime` 写精确版本 `[x.y.z]`（cleat-187）；包之间写下限（运行时的精确版本已经保证一致）。
- 发版顺序（依赖在前）：ToolsNamespace → ToolsBuild → Nrepl → Neorepl；ToolsReader → ToolsAnalyzer → CoreAsync；DataGenerators → TestGenerative（要 ToolsNamespace）；
  Nanopass、SpecAlpha、TestCheck 独立。

## 依赖图

```
Cleat.Runtime [1.2.0]  ← 所有包
Cleat.ToolsNamespace ← Cleat.ToolsBuild ← Cleat.Neorepl → Cleat.Nrepl
        ↑
        └──────────── Cleat.TestGenerative → Cleat.DataGenerators
Cleat.ToolsReader ← Cleat.ToolsAnalyzer ← Cleat.CoreAsync
        ↑                                     │
        └─────────────────────────────────────┘
Cleat.SpecAlpha ··> Cleat.TestCheck（可选：生成器按需 require）
Cleat.Nanopass（独立）
```
（`A ← B` = B 依赖 A；`A → B` = A 依赖 B。）
