# cleat-178 优化·调用：known、inline、loopinl、accinline、crossnstype

- 里程碑：M20　依赖：cleat-177　状态：实现完成，性能门控待 181（2026-11-04，随 cleat-172 移植）
- 设计依据：[30 §4](../designs/30-csharp-compiler.md)；现行 `analyzer.clj` 的 `var-call`（:658）、`inline-target`（:596）、`inline-call`（:644）、`known-call-class`（:537）、原始签名（:502-533）

## 工作内容

在 `Analyze` 的调用点决策里逐项打开，每项沿用原开关记号：
1. `known`（042 / 110）：直接链接（`AotPaths.DirectLinkTarget` → `clj-static-call`）、`KnownCall`、`PrimInvoke`。
2. `inline`（及 `CLEAT_NO_INLINE`）：从 `.inl` 读 L0 子句、alpha 改名、`IsInstanceOf` 守卫 + 内联体 + 慢路径。
3. `loopinl`：循环体内小函数内联（含包构建时的 `restrict-loop-inlines?` 规则）。
4. `accinline`：小访问器跨单元内联（默认关，保持 opt-in）。
5. `crossnstype`：跨命名空间 deftype 决议。

## 验收

- 每项：开 / 关两臂在 C# 编译器上做 G4 配对，针对的基准达到原任务（042 / 110 / 120 / 144 / 145）记录的方向；全表无可声明回退。
- `ClojureCompilerTests` 中对应的用例（KnownCalls、AccessorInlining 等）在 `CLEAT_COMPILER=cs` 下通过。

## 实施记录（2026-11-04）

**随 cleat-172 一起实现**：这些调用点决定在 analyzer.clj 的 `var-call` 里，与分析不可分。C# 版在
`src/Cleat.Compiler/Analyzer/CallSites.cs`（crossnstype 的决议在 `Resolve.cs` / `Types.cs`），开关记号与环境变量不变
（`CLEAT_NO_OPT=known / loopinl / crossnstype / accinline` 经 `OptFlags.Off`；`CLEAT_NO_INLINE`；`CLEAT_OPT_ACCINLINE=1`）。
- known：`AotPaths.DirectLinkTarget` → `clj-static-call`；`known-call-class` → `KnownCall`；`var-prim-sig` → `PrimInvoke`。
- inline：`inline-target`（`InterfaceFile.FindKnown` / `FindInline` + `Fasl.Read`，按 (条目, 参数个数) 缓存）、`alpha-rename-clause`、
  `IrReader` 读子句、`inline-call`（IsInstanceOf 守卫 + 内联体 + 慢路径，有原始签名时慢路径为 PrimInvoke）；NaN 字面量实参不内联；
  另有 `:inline` 元数据展开、`instance?` → IsInstanceOf、`^:escape` 形参的 `EscapePropagate`。
- loopinl：`:in-loop` 上下文 + `clause-has-calls?`；`FormAnalyzer.RestrictLoopInlines` 由包构建关掉（`CsDriverPersist.WithAnalyzerFlags`）。
- accinline（默认关）：包构建期 `PackageIndex.PrepareNamespaceInterface`。C# 驱动写 `.inl` 时的访问器特化仍未移植（cleat-176 起如此，见 `CsDriverPersist` 的注释）。
- crossnstype：`tag-type` / `class-for-name` 的编译期登记表与 `require-type-ns!`、`import*` 的重试、gen-interface 的 `DefineInterfaceStub`。

**验收（已做的部分）**
- G1：这些分支产出的 L0 与 Clojure 分析器逐字相同（cleat-172 的差分，全语料 0 差异；语义测试文件 knowncall / loopinl / accinline /
  crossns / escape 等覆盖这些分支）。
- 21 个 `*_semantics` 文件在 `CLEAT_COMPILER=cs` 下与 Clojure 编译器输出相同。
- `ClojureCompilerTests` 的对应用例在 `CLEAT_COMPILER=cs` 下：`KnownCallsToUserFunctions`、`CrossNamespaceGeneratedTypesResolve`、
  `UserCallsInLoopsAreNotInlined` 通过；`AccessorInliningCrossCompileUnits` 的开 / 关两臂输出都等于 `.out`，随后检查
  `CLEAT_DUMP_EVAL_LCIL` 的 LCIL 转储时失败——C# 编译器不产 LCIL，文件不存在（借用 Clojure 分析器时同样失败），与分析器无关。
- **G4 性能门控（每项开 / 关两臂在 C# 编译器上配对）尚未运行**，留给 cleat-181 的整体门控。

## 补记（2026-11-05，cleat-177）

访问器内联（accinline，cleat-145，默认关）原来只移植了分析器侧：C# 驱动没有访问器表（eval 与接口文件都没有），后端的直读 / 直存是占位，`CLEAT_OPT_ACCINLINE=1`
在 cs 下等于没开（输出不受影响）。177 补上 `Driver/Accessors.cs` 与 `CgGetInstanceFieldDirect` / `CgSetInstanceField`，eval 单元 IL 与 Clojure 编译器逐行相同，
AccessorInliningCrossCompileUnits（改为断言 IL 文本）两种模式都过。
