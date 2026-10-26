# cleat-160 ReadyToRun：Core、镜像、编译器包与随附库

- 里程碑：M19　依赖：cleat-159　状态：未开始
- 设计依据：[29 §3.2](../designs/29-usability-and-engineering.md)

## 问题

`cleat.boot.dll`（2.0 MB）、`cleat.compiler.dll`（3.3 MB）、`Cleat.Clojure.Libs.dll` 是 PersistedAssemblyBuilder
产出的纯 IL，C# 项目也没有开 ReadyToRun，启动时所有初始化代码都要 JIT。

## 工作内容

1. 引入 crossgen2（`Microsoft.NETCore.App.Crossgen2`；本机 NuGet 缓存里暂时没有，需要恢复），在 `tools/build.cs`
   增加 R2R 步骤，覆盖 `Cleat.Core` / `Cleat.Emit` / `Cleat.Host` / `Cleat.Hosting` 与三个 Clojure 程序集。
2. **不动 `src/boot/*.dll`**：种子比较对象保持纯 IL。R2R 只作用在 `bin/` 输出、`publish`、`pack` 产物上；
   Core 重编后镜像必须一起重新 R2R（版本泡），构建链按此排序并加核对。
3. 评估 composite 模式（Core + 镜像一个版本泡，允许跨程序集内联）与逐程序集模式的差异，两种都量。
4. 确认 R2R 代码仍会被分层编译重编为 tier-1，长跑基准不受影响。

## 验收

- 159 画像中 JIT 份额下降，`-e nil` 达到或接近 159 定的目标值。
- `HostProcessTests`、`NuGetColdLoadTests`、`PackageTests` 在 R2R 包上通过；ilverify 只针对 IL 产物，不受影响。
- r7rs 56 项同轮配对（本机 5 轮）没有可声明的回退。
- 包体积变化写进任务书。
