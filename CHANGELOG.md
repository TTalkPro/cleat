# Changelog

条目按里程碑记（任务索引 [TASK.md](TASK.md)）；M19 及以前的完整收口记录由 cleat-169 补写。

## 未发布

### M20 编译器改为 C#（cleat-171 ~ 182，[designs/30](designs/30-csharp-compiler.md)）

- 编译器用 C# 重写（`src/Cleat.Compiler`，程序集 `Cleat.Compiler`）：保留 nanopass 式的小 pass（L0 ~ L3 IR 节点由 Source Generator 生成），
  L4 表示表、L5 线性 CIL 之后用 ILGenerator 直接发射；镜像 / 包程序集经 PersistedAssemblyBuilder 持久化，MVID 按内容哈希。
- 优化全部移植（直接链接与内联、KnownCall、类型推断与原始表示、循环特化、入口特化、分层等）；56 项 × 5 轮同轮配对与 Clojure 编译器对照无可声明回退。
- `dotnet build` 由编译器从源码两步编出镜像（`--no-boot` 编 stage1，再在其上编），构建两次逐字节相同（G5）；没有种子、不需要自举。
- **删除**（cleat-182）：Clojure 写的编译器 `src/clojure/cleat/compiler/`、`CLEAT_COMPILER` / `CLEAT_COMPILER_NS` / `CLEAT_CS_ANALYZER` 开关、
  编译器包 `cleat.compiler.dll`、文本种子 `src/boot/seed/` 与 `tools/build.cs` 的 `stage1` / `selfhost` / `clojure-seed` / `compile-compiler`、
  发射器 `src/Cleat.Emit`（`cleat-emit` 命令行与它的 S 表达式 CIL 格式）与 `CLEAT_DUMP_LCIL` / `CLEAT_DUMP_EVAL_LCIL`。
- `cleat.nanopass` 移到 `packages/nanopass`，是普通的用户库（包程序集 `Cleat.Nanopass`）。
- `tools/build.cs cs-image` 改名 `image-set`（旧名仍可用）；CI 的种子守卫换成确定性守卫（`Category=Determinism`）。
- 宿主新增 `--no-boot`（不加载任何镜像）。
- `CLEAT_NO_OPT` 的记号 `inline` 删除（早已没有读取处）；调用点内联用 `CLEAT_NO_INLINE` / `-g` 关。
