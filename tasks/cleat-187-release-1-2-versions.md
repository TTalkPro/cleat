# cleat-187 1.2.0：版本号、包清单与依赖审查

- 里程碑：M22　依赖：cleat-162（CI 在 GitHub 上跑绿）　状态：未开始

## 问题

M19 ~ M21 都没有发版：运行时 / 工具包仍是 1.1.0（`CoreInfo.Version`），而这三个里程碑换了编译器（C#）、加了 6 个新包（nanopass、spec.alpha、
test.check、tools.reader、tools.analyzer、core.async）、改了包程序集的格式（cleat-186 的 `CleatBootImageAttribute`）。包之间的依赖版本
（`packages/*/cleat.edn` 的 `:nuget/version`）是各自移植时写的，没有一起核对过。

## 工作内容

1. 版本号：`Cleat.Runtime` / `Cleat.Cli`（`CoreInfo.Version`）定为 1.2.0；各包的版本按"上游版本 + cleat 修订"的既有规则核对，
   有 cleat 补丁的（tools.analyzer、core.async、tools.reader 等）是否需要修订号。
2. 依赖审查：`packages/*/cleat.edn` 与 `build.clj` 里的包依赖、运行时版本下限（`:runtime-version`）一致；画出包依赖图写进 notes。
3. 1.1.0 → 1.2.0 的兼容说明：1.1.0 编的包程序集在 1.2.0 上会怎样（镜像 id 不同 → StaleModuleException / 不登记），写进 CHANGELOG 的升级说明。
4. CHANGELOG 的"未发布"改为 1.2.0（日期），README 的版本与安装说明同步。
5. 用 `tools/build.cs pack` 打出全部包，在空目录里从本地源冷装：C# / F# 引用运行时、`dotnet tool install Cleat.Cli`、各包 restore 后跑一遍上游测试。

## 验收

- 全部包能从本地源冷装并运行；依赖图与版本表进 notes；CHANGELOG / README 一致；全量门禁通过。
