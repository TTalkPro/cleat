# 符合性语料

设计见 [designs/09-testing.md](../../designs/09-testing.md)。

| 文件 | 来源 | 说明 |
|---|---|---|
| `corpus.edn` | jolt `test/chez/corpus.edn`（jolt-lang/jolt `d9d454a2`，EPL-2.0，见 `LICENSE-EPL-2.0.txt`） | 冻结的契约：每行 `{:suite :label :actual :expected :portability}`，`:expected` 由 JVM Clojure 1.12.5 取证。2026-10-10 复制时 7168 行：`:common` 4842、`:jvm` 2324、1066 个 suite |
| `known-divergences.edn` | jolt `test/conformance/known-divergences.edn` | jolt 的分类白名单（104 行 `:entries`），只作分类用词的参照；cleat 自己的偏离表是 `cleat-divergences.edn`（cleat-032 建立） |
| `profile.edn` | jolt `test/conformance/profile.edn` | 按宿主能力对非可移植用例分级（生成时对应较早的语料：5754 + 237 条） |
| `SPEC.md` | jolt `test/conformance/SPEC.md` | 语料规格 |

运行：`dotnet run tools/build.cs -- conformance --stats`（现在只做解析与统计；对宿主求值在 cleat-018 / cleat-032 接入）。
`:expected` 为字符串 `":throws"` 时断言 `:actual` 抛出；比较一律按 `=`。
