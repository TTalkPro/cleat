# cleat-060 预编译库的扩展名统一为 .fasl

- 里程碑：后续　依赖：cleat-059　状态：完成
- 设计依据：skeg 的提交 481f319（Unify fast-load extension to .fasl）。

## 背景

元层有两种编译产物，都是 .NET 程序集：`compile-file` / `compile-program` 写的 `.fasl`（入口返回 `(cleat-scheme-fasl 1 …)`），
与 `(compile)` / `compile-dll` 按库名写在库路径上的 `.dll`（入口返回 13 元素的库描述列表，加载库时按名字探测）。
skeg 把后者的扩展名也改成 `.fasl`，过程随之改名。本任务照 skeg 对齐，范围与它相同。

## 工作内容

| 现名 | 新名 |
|---|---|
| `library-name->dll-name`（`<库名>.dll`） | `library-name->fasl-name`（`<库名>.fasl`）；`compiled-library-exists?` 按 `.fasl` 探测 |
| `compile-library-dll` | `compile-library-fasl`；新增 `compile-library-fasl/asm 文件 e 确定性?`：为真时程序集名取文件名主干（库路径上的预编译库，跨库引用与 ILVerify 按文件名解析），否则加进程内唯一后缀；扩展名为 `.dll` 时仍取文件名主干 |
| `compile-dll`（`compile` 的内部过程与 `load-r6rs-top-level` 的方式名） | `compile-fasl` |
| C# 原语 `load-library-dll` | `load-library-fasl` |

与 skeg 相同，内部名字 `load-library-from-dll`、C# 方法 `ModuleLoader.LoadLibraryDll` 不改。

## 种子（两步）

1. C# 同时提供 `load-library-dll` 与 `load-library-fasl`；`seed --stage0` 生成新种子。
2. 删掉旧原语名，新种子构建镜像，镜像上再生成的种子与第一步逐字节相同。

## 验收标准

- `tests/scheme/compiler/compile-library.sps` 用新名字通过；种子与两段镜像不动点；全量测试通过。

## 结论（2026-10-12）

完成，范围与 skeg 481f319 相同。库路径上按库名命名的预编译库从 `<库名>.dll` 改为 `<库名>.fasl`（仍是程序集，加载时按 `.fasl` 探测），
`compile-library-fasl` / `compile-library-fasl/asm` / `library-name->fasl-name` / `load-library-fasl` 取代对应的 `*-dll`。
`compile-fasl` 以确定性程序集名调用 `compile-library-fasl/asm`（与原来 `.dll` 扩展名时的命名相同）。

- **种子**：两步；第一步 C# 同时提供新旧原语名、`seed --stage0` 生成新种子（旧名 0 处）；第二步删掉旧名，新种子构建镜像，镜像上再生成的
  种子与第一步逐字节相同。
- **测试**：`compile-library.sps` 改用 `mini.lib.fasl` 与 `compile-library-fasl/asm`。`dotnet test Cleat.slnx`：Core 129、Scheme 233、
  Examples 1 通过；Host 234 中 233 通过，`PublishedDirectoryRunsStandalone` 因 `/tmp`（12G 内存盘）被其他项目的临时文件写满而失败，
  `TMPDIR` 指到主磁盘单独重跑通过——与本改动无关。
- 旧的 `<库名>.dll` 预编译库不再被探测，需要重新编译（仓库里没有这类产物）。
