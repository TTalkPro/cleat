# cleat-086 Clojure 前端（分析器）

- 里程碑：M8（阶段 A）　依赖：cleat-085　状态：完成
- 设计依据：[19 D3](../designs/19-clojure-self-hosting.md)

## 目标
用 Clojure 写的前端取代 psyntax + `src/scheme/clojure/{syntax,namespaces,syntax-kinds}.sls`，直接产出 L0，与现行"展开 + parse-core"的输出相同。

## 接入（cleat-085 之后）
- 前端产出的 L0 只含普查非零的产生式（见 [cleat-085 结论](cleat-085-compiler-skeleton-and-diff.md)）；局部变量名取 `g$名字$N` 一类（差分工具按 `g$` 前缀规范化）。
- 影子模式：实现 `cleat.compiler.pipeline/shadow-stages`（`cleat.compiler.shadow` 调用），对每个形式转储 `p0` 起的阶段；`tools/build.cs ir-dump <目录> --shadow` 生成输入集，
  `tools/compiler-diff.clj diff <目录>/scheme <目录>/clojure` 验收 G1@L0。

## 工作内容
1. 宏展开：Var 的 `:macro`、`(&form &env …)`、`&env` 为 `LocalBinding` 映射、`macroexpand-1` / `macroexpand` 的停止点、展开错误的 phase 与源位置。
2. 决议：直接用 `clojure.lang.Namespace`（别名、refer、import、重定义、`:const`）；特殊形式只在头部位置识别。
3. 全部特殊形式（`def`、`if`、`do`、`let*`、`loop*` / `recur`、`fn*`、`quote`、`var`、`throw`、`try`、`monitor-*`、`.`、`new`、`set!`、`letfn*`、`case*`、`deftype*`、`reify*`、
   `proxy-class*`、`gen-class*`、`await*`、`import*`）与宿主语法糖；类型提示与原始签名经 `HostSyntax`（cleat-082 后在 Cleat.Core）。
4. `:inline`、小函数内联（`.inl`：Clojure 编译器写自己的格式，两边各自从源码构建时各用各的）、直接链接（`.ifc`）。
5. 产出 L0 记录（cleat.compiler.languages），形状照 parse-core：包括 Scheme 痕迹（catch 处理器 lambda、`'()` 占位），留到 cleat-092 再清理。

## 验收标准
- G1@L0：差分工具对输入集全部顶层形式零差异（规范化后）。
- 错误黄金文件（`tests/errors`）在只换前端的模式下通过（若 085 的开关允许"Clojure 前端 + Scheme 后续 pass"组合则用它，否则在 090 一并验收）。

## 结论（2026-10-14）

完成（G1@L0 在关掉小函数内联时达成；内联与错误黄金文件的验收移到 cleat-090，见下）。

**实现**：`src/clojure/cleat/compiler/analyzer.clj`（约 1170 行）。照 `syntax.sls` / `namespaces.sls` 逐条移植，没有语法对象：
- 上下文 `{:tail? :loop :locals :fn-name :hint :pos}`（Scheme 版的 ctx），局部 `Local`（符号、L0 名字、提示、可变、种类）；
  局部变量名 `g$名字$N`（名字是符号的完整写法，限定符号带命名空间——psyntax 的改名同形）；源位置 `:pos` 照 psyntax 注解沿用最近的带 `:line` 的外层形式，宏展开结果取宏调用处的位置。
- 决议 `resolve-in` 直接用 `clojure.lang.Namespace`；特殊形式只在头部、不被局部遮蔽；宏以 `(&form &env 实参…)` 调用（`&env` 为 `LocalBinding` 映射），
  异常按 Clojure 1.12 的 macroexpand1 分阶段；`:inline`、`instance?`、`PrimInvoke`、直接链接（`.ifc`）、宿主语法糖（`.m`、`T.`、`T/m`、`T/.m`）、静态成员、数组类型 `long/1`。
- 全部特殊形式与 deftype* / reify* / gen-interface / proxy-class* / gen-class* 的类型描述（`HostSyntax` 的 C# 规则不变）；字面量与集合（常量判定、`VectorOf` / `MapOf` / `SetOf`、`WithMetaOf`、`Namespace` 对象）。
- 产出的形状照"psyntax 展开 + parse-core"：`let*` 每个绑定一层 `(call (lambda #f (clause (x) #f 体)) 初值)`；`loop*` 与 fn* 子句是命名 let
  `(call (letrec ((lp (lambda "loop"/"recur" …))) lp) 初值…)`，recur 是 `(call lp …)`；try 的处理器是 lambda；codegen 读的描述（case* 的表、deftype* 等的类型描述、fn* 的原始签名表）
  与"无泛型实参"的 `'()` 是 Scheme 的数据（`Cleat.Scheme.Cons` / `Symbol` / `EmptyList`，cleat-092 改成 Clojure 数据）。
- 分析顺序照 psyntax（影响报哪个错误、宏与 deftype* 桩类型的副作用顺序）：应用先展开运算符，所以 let* 先分析体、再从后往前分析初值；try 先体、再各 catch、再 finally；
  其余从左到右，一律立即求值（惰性 `map` 曾让体的最后一项先于前面的项分析，`import*` 早于 `deftype*` 的桩类型）。
- 重复的绑定名照 `dedupe-ids`：前面的同名者用临时名（基名 `t`）。
- `macroexpand-1`：照 `macroexpand-1-in-ns`（与现行 `clojure.core/macroexpand-1` 抽查一致）。
- 入口 `analyze-top`；`cleat.compiler.pipeline/eval-unit-l0` 包上驱动的 `HoldResult`，`shadow-stages` 在影子模式下转储 `p0`。

**差分工具与驱动的补充**：Scheme 驱动在影子模式下展开出错时也写一条 `error` 记录（`IrDump.WriteError`），两侧的诊断文本一起比较；
`compiler-diff.clj` 的规范化去掉 psyntax 名字里 gensym 的序号（`g$seq_123`、`g$p__45`）；`tools/build.cs ir-dump --corpus` 的参数改正（语料文件作第一个参数）。

**验收（G1@L0）**：`CLEAT_NO_INLINE=1 tools/build.cs ir-dump <目录> --shadow --corpus`（boot 镜像、随附库、tools.build、tools.namespace、语料），
`compiler-diff.clj diff <目录>/scheme <目录>/clojure --stage p0 --stage error`：13605 条记录全部相同（13330 个形式的 p0 与 275 个语法错误的文本），
只在 Scheme 一侧的 8 条是驱动内部的编译（序号 1000000 起）。Host 测试 `CompilerDiffTests.ShadowFrontEndMatchesSchemeAtL0`：21 个覆盖各类形式与错误的顶层形式，p0 与错误文本零差异。

**移到 cleat-090 的**：
- 小函数内联（`.inl`）：现行的 `.inl` 存的是 Scheme 的 Lcore，Clojure 编译器写自己的格式（L0 的数据）并在调用点照 `inline-call` 的形状展开；在那之前 G1 在 `CLEAT_NO_INLINE=1` 下比较。
- 错误黄金文件（`tests/errors`）：没有"Clojure 前端 + Scheme 后续 pass"的混合模式，在 `CLEAT_COMPILER=clojure` 下一并验收；影子模式已经逐条比较了语法错误的文本。
