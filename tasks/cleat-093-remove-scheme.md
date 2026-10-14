# cleat-093 删除 Scheme

- 里程碑：M8（阶段 E）　依赖：cleat-092　状态：未开始
- 设计依据：[19 §1 阶段 E](../designs/19-clojure-self-hosting.md)

## 目标
仓库里不再有 Scheme：源码、程序集、测试、构建目标、文档引用。

## 工作内容
1. 删除 `src/scheme`、`src/Cleat.Scheme`、`src/Cleat.Stage0`、`Cleat.PrimGen` 的 Scheme 原语表部分（Clojure 内核表若仍需要则保留）、`src/boot/cleat.scheme.*`、`Cleat.Core/Expander`（保留 `LocalBinding` / `ExpanderException`）。
2. 测试：删 `tests/Cleat.Scheme.Tests`、`tests/scheme`（R6RS、Chez mats、Scheme 快照）；Core 测试去掉 Scheme / Stage0 引用；`SchemeInvisibilityTests` 改为"生成代码与诊断中没有 Scheme"之类仍有意义的检查或删除；Clojure 编译器的快照测试补齐。
3. 构建：csproj 目标、`tools/build.cs`（scheme-stage1、seed 旧义等）、`Cleat.slnx`、`PackageIndex.cs` 里的 `Cleat.Scheme.dll`、打包清单。
4. NOTICE / 许可：去掉随 Scheme 删除的条目（psyntax、nanopass-framework-scheme 快照、Chez mats、R6RS 套件、UCD 表若随之删除）；Cleat.Emit 等仍源自 IronScheme 的 C# 保留条目。
5. 文档：designs/13、14、15、08 旧版标为历史；00、04、README 改为现行架构；`CLEAT_SCHEME_*` 环境变量与 `CLEAT_COMPILER` 开关删除。

## 验收标准
- `git grep -i scheme` 只剩历史文档与许可说明；从干净仓库构建与全量测试通过。
