# cleat-094 自寄宿后的新版本与 cleat.nanopass 文档

- 里程碑：M8（阶段 E）　依赖：cleat-093　状态：未开始
- 设计依据：[19](../designs/19-clojure-self-hosting.md)、[cleat-080](cleat-080-republish-after-skeg-port.md)

## 目标
发布不含 Scheme 的运行时与工具，`cleat.nanopass` 作为用户可用的库有文档。

## 工作内容
1. 新版本：Cleat.Runtime / Cleat.Cli 1.1.0（运行时契约变了，次版本号升），ToolsNamespace、ToolsBuild、Nrepl、Neorepl 按依赖顺序重编重发；示例、README、designs/18、`NuGetColdLoadTests` 同步。
2. 照 cleat-080 的方法验证（本地目录源、隔离包缓存与 `DOTNET_CLI_HOME`、两个示例、jack-in），通过后推 BaGetter。
3. `cleat.nanopass` 的用户文档与示例（定义语言、写 pass、模板写法与上游的对照表）。

## 验收标准
- 两个示例对着 BaGetter 实跑通过；冷加载（C# / F# 引用包）不加载编译器程序集。
