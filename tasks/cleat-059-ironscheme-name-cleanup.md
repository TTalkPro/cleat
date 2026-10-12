# cleat-059 清理 IronScheme 名字残留

- 里程碑：后续　依赖：cleat-054、cleat-058　状态：完成
- 设计依据：skeg 的提交 c7e1882（Drop remaining IronScheme naming）与 f4f528d；本任务推翻 [cleat-054](cleat-054-rename-scheme-libraries.md)
  "保留（非库名）"一条（用户 2026-10-12 决定）。

## 背景

cleat-054 只把库名 `(ironscheme …)` 改成了 `(cleat scheme …)`，有意保留了原语名、fasl / 接口文件的格式头与 nanopass 的
helpers 文件名约定。同源的 skeg 已把这些全部改成自己的名字。本任务按同样的范围清理 cleat 的元层，统一用 `cleat-scheme`
前缀（与库名 `(cleat scheme)`、程序集 `Cleat.Scheme`、镜像 `cleat.scheme.boot.dll`、环境变量 `CLEAT_SCHEME_BOOT` 一致）。

## 工作内容

| 现名 | 新名 | 位置 |
|---|---|---|
| `ironscheme-version` / `ironscheme-runtime`（C# 原语） | `cleat-scheme-version` / `cleat-scheme-runtime` | `Runtime/Misc.cs`、`primitives.ss`、两个构建脚本的导出表 |
| `ironscheme-build` / `ironscheme-test`（boot 过程） | `cleat-scheme-build` / `cleat-scheme-test` | `expander/main.sls`、两个构建脚本；`build` 改为加载 `boot/buildscript-nano.sps`（原来加载的文件 cleat-054 已改名，过程已失效） |
| fasl 格式头 `(ironscheme-fasl 1 …)` | `(cleat-scheme-fasl 1 …)` | `expander/main.sls` |
| 接口文件头 `(ironscheme-interface 1 …)` | `(cleat-scheme-interface 1 …)` | 构建脚本、`compiler.sls`、`codegen.sls` 的说明、`BootInterface.cs` |
| 整程序编译的清单 `ironscheme.app` | `cleat-scheme.app` | `expander/main.sls` |
| 环境变量 `IRONSCHEME_NO_034` / `_DUMP_PASSES` / `_DUMP_FAILED_CORE` / `_TRACE_ERRORS` | `CLEAT_SCHEME_…` | 编译器库、`codegen.sls`、`loops.sls`、`parse.sls`、`Errors.cs` |
| nanopass 覆盖库 `lib/nanopass/implementation-helpers.ironscheme.sls`、库扩展名 `.ironscheme.sls` | `implementation-helpers.cleat-scheme.sls`、`.cleat-scheme.sls` | `expander/main.sls`、构建脚本；覆盖库直接调用 `Cleat.Scheme.Builtins`，`TypeResolver` 的旧类型名映射删除（上游 nanopass 目录原样不动，它的 `.ironscheme.sls` 不再被选中） |
| 用户可见文本 `"IronScheme build completed."`、`"not an IronScheme compiled file"` | 去掉旧名 | `expander/main.sls` |
| 把现有组件叫成旧名的注释（`IronScheme.Core.HostPrimitives`、"IronScheme 扩展"等） | 现名 | boot 源码、reader、编译器库、`(clojure syntax)` |

**不改**：讲来历的注释（"从 IronScheme 移植"、IronScheme 设计文档与 iron-0xx 任务编号）、版权行、LICENSE / NOTICE、
designs/ 与 tasks/ 的历史记录、上游 nanopass 目录。`compile-library-dll` / `load-library-dll` / `library-name->dll-name`
也不改：cleat 里它们确实产出 `.dll` 程序集；skeg 改成 `.fasl` 是为 NuGet 打包换了文件格式，不是名字残留。

## 种子（两步）

旧种子引用旧原语名、按 `.ironscheme.sls` 找覆盖库，新种子引用新名：

1. C# 暂时同时提供新旧两个原语名；`seed --stage0`（stage0 + 旧种子展开新 boot 源码）生成新种子。
2. 删掉旧原语名，新种子构建镜像，镜像上再生成种子，与第一步的逐字节相同（不动点）。

## 验收标准

- 代码（`.cs`、`.sls`、`.sps`、`.ss`）里除来历注释与版权行外不再出现 `ironscheme` / `IRONSCHEME`；新种子里不再出现。
- 种子不动点；两段镜像不动点；全量测试通过。

## 结论（2026-10-12）

完成。按上表全部改为 `cleat-scheme` 前缀；代码（`.cs`、`.sls`、`.sps`、`.ss`）里不再出现旧名，只剩来历注释、版权行与上游 nanopass 目录。

- **顺带修正**：`cleat-scheme-build` 原来加载的 `ironscheme-buildscript-nano.sps` 在 cleat-054 已改名，过程早已失效，改为加载
  `boot/buildscript-nano.sps`。`codegen.sls` 读接口文件时比对格式头的两处也一并改名（只改写出端会让读取端认不出新文件）。
- **nanopass 覆盖库**：改名为 `lib/nanopass/implementation-helpers.cleat-scheme.sls`，直接调用 `Cleat.Scheme.Builtins`；
  `TypeResolver` 的 `IronScheme.Runtime.Builtins` 映射删除，`InteropTests` 改为断言现名可解析。
- **种子**：两步。第一步 C# 同时提供新旧原语名，`seed --stage0` 生成新种子（不含任何 `ironscheme`）；第二步删掉旧名，新种子构建
  两段镜像，镜像上再生成的种子与第一步逐字节相同。
- **产物**：`src/boot/cleat.scheme.clojure/` 的清单改为 `cleat-scheme.app`（旧的 `ironscheme.app` 已删）；随附库 `.clj.dll`
  用 `compile-libs` 重编译（`clojure.repl.deps` 照旧跳过）。旧格式头的接口文件不再被接受，需要重新编译。
- **测试**：`dotnet test Cleat.slnx` 597 全部通过（Core 129、Scheme 233、Host 234、Examples 1），含两段自举不动点。
