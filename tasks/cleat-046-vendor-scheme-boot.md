# cleat-046 纳入 Scheme 元层并在本仓库自举

- 里程碑：M2′　依赖：cleat-045　状态：未开始
- 设计依据：[15 §1、§6](../designs/15-scheme-core-vendoring.md)、IronScheme `designs/05-bootstrap.md`

## 目标
不改任何 Scheme 源码，证明"种子 → stage0 → Scheme 镜像 → 不动点"在本仓库成立。

## 工作内容
1. `src/scheme/boot/`（boot 库、展开器、`primitives.ss`、`boot-seed.core`、构建脚本）、`src/scheme/compiler/`（去 `async.sls`，`targets.sls` 类名改 `Cleat.Scheme.*`）、`src/scheme/nanopass/`（原样）。
2. `tools/build.cs`：`scheme-stage1` / `seed` / `ilverify`；`Cleat.Host.csproj`（或单独的 `Cleat.Boot` 项目）的 `GenerateSchemeBootImage` 目标。
3. `tests/Cleat.Scheme.Tests` 的 `Bootstrap` 类别：stage0 → stage1 → stage2 → stage3，Lcil 与 dll 不动点、ILVerify。
4. 编译器快照测试（IronScheme `tests/compiler/`）纳入，作为"nanopass 未受影响"的基线。

## 验收标准
- 不动点成立；快照测试通过；记录 Phase A 耗时。
