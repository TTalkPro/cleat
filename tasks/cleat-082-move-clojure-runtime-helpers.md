# cleat-082 Clojure 运行时辅助类搬进 Cleat.Core

- 里程碑：M8（阶段 A）　依赖：cleat-081　状态：未开始
- 设计依据：[19 D8](../designs/19-clojure-self-hosting.md)

## 目标
编出的 Clojure 代码只依赖 Cleat.Scheme 里"与 Scheme 无关"的辅助类，这些类先搬到 Cleat.Core，让 Clojure 编译器从第一天起就面向最终的运行时名字。

## 工作内容
1. 搬：`Locals`（`Take`）、`Clojure/ClojureOps`、`Clojure/ClojureStaticCalls`、`Clojure/HostSyntax`、`Clojure/HostSyntaxClasses`、`RT.IsTrue` / `RT.Bool`、
   `Driver.cs` 中与 Scheme 无关的部分（简单形式解释 `TryEvalSimple`、错误格式化与栈过滤、接口文件 / 内联文件读写、直接链接目标）。新命名空间在本任务里定（建议 `Cleat.Core.Compiler`、`Cleat.Core.Interop`）。
   依赖 Scheme 类型的方法（如 `SeqToList`、`HoldResult` / `TakeResult` 的 Int32 处理）留在 Cleat.Scheme 的薄垫片里。
2. Scheme 编译器（codegen、syntax.sls、compile.sls、driver.sls）改引用新名字；`Cleat.Emit` 按名字决议的类型表同步。
3. 重生成 Scheme 镜像、种子（不动点）、Clojure 镜像与包程序集。

## 验收标准
- `cleat.boot.lcil` 里不再出现 `Cleat.Scheme.Locals` / `Cleat.Scheme.Clojure.*` / `Cleat.Scheme.RT::IsTrue`；剩下的 Scheme 引用只有 D8 第二条所列（Procedure、ContinuationException、EmptyList、StaticProcedure、Fasl、RT.Box）。
- 种子不动点；全量测试通过；Clojure 基准不退化。
