# 符合性语料

设计见 [designs/09-testing.md](../../designs/09-testing.md)。

| 文件 | 来源 | 说明 |
|---|---|---|
| `corpus.edn` | jolt `test/chez/corpus.edn`（jolt-lang/jolt `d9d454a2`，EPL-2.0，见 `LICENSE-EPL-2.0.txt`） | 冻结的契约：每行 `{:suite :label :actual :expected :portability}`，`:expected` 由 JVM Clojure 1.12.5 取证。2026-10-10 复制时 7168 行：`:common` 4842、`:jvm` 2324、1066 个 suite |
| `known-divergences.edn` | jolt `test/conformance/known-divergences.edn` | jolt 的分类白名单（104 行 `:entries`），只作分类用词的参照；cleat 自己的偏离表是 `cleat-divergences.edn`（cleat-032 建立） |
| `profile.edn` | jolt `test/conformance/profile.edn` | 按宿主能力对非可移植用例分级（生成时对应较早的语料：5754 + 237 条） |
| `SPEC.md` | jolt `test/conformance/SPEC.md` | 语料规格 |
| `cleat-divergences.edn` | cleat-032 | cleat 在 `:common` 行上的失败，每行一个 `:category` + `:reason`（`:legend` 解释类别）；`--gate` 的依据 |
| `cleat-results.edn` | cleat-032，生成 | 每行的结果 `[[suite label] :pass]` / `:fail` / `:throw` / `:timeout` + 简短原因 |

运行：
- `dotnet run tools/build.cs -- conformance --stats`：解析与统计。
- `dotnet run tools/build.cs -- conformance --run [--gate] [--results tests/conformance/cleat-results.edn]`：在 cleat 宿主上运行（`tools/conformance.clj`；每行在新的 `user` 命名空间里，先读 `:expected` 再求值 `:actual`）。`--gate` 时失败行不在 `cleat-divergences.edn` 中（新失败）或表中的行已通过（过期）都以退出码 1 失败；`dotnet test --filter Category=Conformance` 跑的就是它。

`:expected` 为字符串 `":throws"` 时断言 `:actual` 抛出；比较一律按 `=`。
