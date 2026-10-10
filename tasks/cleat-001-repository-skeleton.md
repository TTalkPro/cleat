# cleat-001 仓库骨架与构建约定

- 里程碑：M0　依赖：无　状态：未开始
- 设计依据：[00 §2、§5](../designs/00-overview.md)、[01 §6](../designs/01-reference-analysis.md)

## 目标
建立 00 §5 的目录结构、解决方案与构建约定，确定许可分区（00 D11）。

## 工作内容
1. `Cleat.slnx`、`Directory.Build.props`（net10.0、Release 默认、`Nullable`、`TreatWarningsAsErrors` 对新代码开启、对 `clojure.lang/` 目录关闭）、`.editorconfig`（沿用 IronScheme 09 的 C# 规范）。
2. 空项目：`src/Cleat.Core`、`Cleat.Emit`、`Cleat.Stage0`、`Cleat.Host`、`Cleat.PrimGen`；`tests/Cleat.Core.Tests`（xunit）。
3. 目录：`src/clojure/`、`src/cleat/`、`src/boot/`、`native/`、`tests/conformance/`、`spikes/`、`tools/`。
4. `tools/build.cs`（.NET 10 文件式 C# 程序）的命令骨架：`stage1`、`seed`、`ilverify`、`conformance`、`native`。
5. 许可：`src/Cleat.Core/clojure.lang/LICENSE-EPL.txt`、`src/clojure/LICENSE-EPL.txt`、`NOTICE` 说明来源；README 补一段来源与许可说明。**在开始之前向用户确认 D11 的许可分区方案**。
6. `.gitignore` 补 `src/boot/*.dll`、`*.ifc`、`native/build/`。

## 验收标准
- 仓库根目录 `dotnet build` 成功（空项目）；`dotnet run tools/build.cs -- --help` 列出命令。
- 用户已确认许可分区。

## 结论（2026-10-10）

完成。
- `Cleat.slnx`、`Directory.Build.props`（net10.0、`Nullable`/`ImplicitUsings` 关、`EnforceCodeStyleInBuild`）、`Directory.Solution.props`（默认 Release）、`.editorconfig`（沿用 IronScheme 的 C# 规范，Lisp 文件 2 空格）。
- 项目：`src/Cleat.Core`（类库）、`Cleat.Emit`（`cleat-emit`）、`Cleat.Stage0`（`stage0`）、`Cleat.Host`（`cleat`）、`Cleat.PrimGen`（`primgen`）；`tests/Cleat.Core.Tests`（NUnit 4，与 IronScheme 相同版本，离线缓存可用）。`dotnet build` / `dotnet test` 通过（1 个冒烟测试）。
- `tools/build.cs`：命令骨架 `stage1` / `seed` / `ilverify` / `conformance` / `compile-libs`，未实现的命令报对应任务号；文件头加 `#:property ImplicitUsings=enable`（根目录的 props 对它也生效）。
- 许可：`NOTICE` 列出来源与许可；`src/Cleat.Core/clojure.lang/LICENSE-EPL.txt`、`src/clojure/LICENSE-EPL.txt`（由 ClojureCLR 的 `epl-v10.html` 转成文本）。用户在 2026-10-10 指示直接开始，许可分区按设计执行。
- `.gitignore` 补 boot 产物、`*.ifc`、`*.clj.dll`、`artifacts/`。
- 工作约定修正：按用户指示，每个任务完成后提交，提交信息不带 Co-Authored-By；LLVM 后端任务搁置（见 TASK.md）。
