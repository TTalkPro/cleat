# cleat-172 C# 分析器：形式 → L0

- 里程碑：M20　依赖：cleat-171　状态：完成（2026-11-04；调用点优化随 cleat-178 一并移植）
- 设计依据：[30 §2 D5 / D8（G1）](../designs/30-csharp-compiler.md)；现行实现 `src/clojure/cleat/compiler/analyzer.clj`

## 目标

用 C# 实现 `analyze-top`：宏展开、符号决议、特殊形式、宿主重载选择，输出 L0。调用点优化（known / inline / loopinl / accinline / crossnstype）
**只留挂钩、默认关**，由 cleat-178 补。

## 工作内容

1. 上下文与局部：对应 analyzer.clj 的 context（`:tail? :loop :locals :fn-name :hint :pos`）与 `Local`；局部名生成规则与现在一致（`g$name$N$c`）。
2. 宏：调用约定 `(&form &env args…)`，`&env` 为 sym → `LocalBinding`；错误按 Clojure 1.12 阶段分类；`macroexpand-1` 钩子（含 `rewrite-to-dot` 宿主糖）。
3. 决议：直接在 `clojure.lang.Namespace` 上决议；`:const` Var 变常量。
4. 特殊形式：def、if、do、let*、loop*、recur、fn*、quote、var、throw、try、monitor-enter / exit、.、new、set!、letfn*、case*、deftype*、reify*、
   proxy-class*、gen-class*、await*、escape*、import*；裸 `catch` / `finally` / `&` 报错。
5. 分析期副作用：`def` 先 intern 并写元数据再分析初值；`deftype*` / `definterface` 调 `HostSyntax.DefineStub` / `DefineInterfaceStub`。
6. 宿主互操作：`select-overload`、`host-form-type`、`check-host-call`、`parse-dot` 的 C# 版；反射警告。
7. async：`fn*` 的 `AsyncStart` / `AsyncRunSync`，`await*` 的 `AwaitAsync` / `AwaitBlocking`。
8. 源位置：`:pos` 记文件（cleat-158 的规则）。
9. **G1 差分**：Host 测试里新增差分用例——对 `src/clojure` 全部源码与 `tests/Cleat.Host.Tests/data/*.clj`，两个分析器在同一镜像上逐形式各出 L0
   （Clojure 侧关掉调用点优化），打印后比较，局部编号按出现顺序规范化。

## 验收

- G1：上述语料 L0 全部相同；不同处只允许是已记账的、可解释的差异（逐条列在本任务书）。
- 宏展开相关的错误黄金文件（`tests/errors/`）在只换分析器时不变（用 Clojure 编译器后端接 C# 分析器的 L0，经 L0 打印 / 读入衔接）。

## 实施记录（2026-11-04）

**范围调整**：调用点优化（cleat-178 的 known / inline / loopinl / accinline / crossnstype）没有"只留挂钩"，而是与分析器一起忠实移植——
这些决定本来就在 analyzer.clj 的 `var-call` 里做，分开移植就要先产出一个不同于 Clojure 分析器的 L0，G1 无法逐字比较。

**产物**（`src/Cleat.Compiler/Analyzer/`，命名空间 `Cleat.Compiler.Analyzer`，入口 `FormAnalyzer`，约 2350 行；各成员注明所移植的 Clojure 函数）
- `Context.cs`：`Ctx`（`:tail? :loop :locals :fn-name :hint :pos :in-loop`，不可变 record）、`Local` / `LocalKind`、局部表、`Pos`；
  全局局部名序号与 `fresh-name`（`g$名字$N$c`）；三个可变开关 `InlineCalls`（缺省看 `CLEAT_NO_INLINE`）、`ReflectionWarnings`、`RestrictLoopInlines`；
  `Clj` 小工具（`str` = `RT.CultureToString`；`Boundary` 值边界，见下）。
- `Resolve.cs`：`source-of` / `position`、错误（`expander-exception`、`err`、`special-head`）、`resolve-in`、`tag-type` / `class-for-name` /
  `require-type-ns!` / `import-class-for-name`（crossnstype）、`static-member-of`、`rewrite-to-dot`、`transfer-source`、`call-macro`（&form &env、
  `LocalBinding`、Clojure 1.12 的阶段分类与 ArityException 修正）、`macroexpand-1`，以及 driver.clj 的 `macroexpand-full`。
- `Analyze.cs`：`analyze-top` / `analyze`（`Node.Pos` 照 `set-node-meta!` 只记一次，词法变量不记）、`const-value` / `analyze-datum`（WithMetaOf）、
  `analyze-body`、`analyze-symbol`、`analyze-seq`、特殊形式分派。
- `SpecialForms.cs`：def（分析期 intern；`:arglists` / 原始返回提示 / `:escape-params` 先上 Var；动态名警告文本）、if、quote、var、throw、
  monitor-*、import*、let* / loop*（`init-tag`、先体后初值的分析顺序、`:in-loop`）、recur、fn*（原始签名、CljFnName 的 `:variadic` / `:prims` /
  `:async?` / `:once`、async 体）、escape*、await*、letfn*、try、case*。
- `HostInterop.cs`：`host-arg-type` / `select-overload` / `host-form-type` / `check-host-call` / `check-host-ctor`（反射警告文本逐字）、
  `host-args`（Tagged）、`parse-dot`、`.` / `new` / `set!`。
- `Types.cs`：deftype*（`HostSyntax.DefineStub`）、gen-interface（`DefineInterfaceStub`）、reify*、proxy-class*、gen-class* 的描述向量。
- `CallSites.cs`（cleat-178）：`var-call` 的全部分支，见 cleat-178 的实施记录。
- 驱动：`CsDriver` 的 `Analyze` / `MacroexpandFull` / `TransferSource` / `Macroexpand1` 缺省指向 C# 分析器；`CLEAT_CS_ANALYZER=clj`
  仍借用 Clojure 分析器（诊断用；其他值在 `CompilerHooks.Install` 报错）。`CompilerHooks.Install` 只在 `clj` 时加载 Clojure 编译器，
  macroexpand-1 钩子走 `CsDriver.Macroexpand1`。`CsDriverPersist.WithAnalyzerFlags` 在 C# 分析器下切换 `FormAnalyzer` 的三个属性，
  只在 `clj` 时动 Clojure 分析器的 atom；`DriverOps.State` 的 `:package` 照旧设置（两个分析器的 inline-target 都读它）。
  `designs/README.md` 环境变量表加 `CLEAT_CS_ANALYZER`（22 → 23 个），analyzer 类记号的读取处注明 Clojure / C#。
- `DiffSupport`：`AnalyzeL0`（C# 分析器 L0 的 S 表达式）、`NumberTypes`（在 C# 侧列出数据里各个数的装箱类型）、`Int32ify`（测试用）。
- `tools/cs-diff.clj` 扩展：每个形式先比较两边的 `macroexpand-full`，再比较两个分析器的 L0（G1），再做原有的前中端七阶段比较（`--no-front` 跳过）。
  两边各自先把 `RT` 的 id 计数器置成同一值；局部名按首次出现重编号；桩类型按全名、正则按文本、数组转向量；另比较数的装箱类型；
  一边报错时另一边须报同样的文本；`--pos` 另外比较 L0 列表上的源位置。读形式改用驱动的 `Diagnostics.ReadForm`（原为 `read` + `:features #{:cljr :clj}`）。
- `tests/Cleat.Compiler.Tests/AnalyzerTests.cs`：19 个用例（let* / loop* / letfn* / fn* 的形状、常量与非常量集合、def 的分析期 intern、
  静态宿主调用、6 个语法错误的文本 / 阶段 / 符号、transfer-source、开关）。

**移植中确认的细节**
- **Clojure 代码里的整数装箱成 Int64**：`count` 的结果与字面量放进 L0 数据（CljFnName 的 `:variadic` 与 `:prims` 里的个数、clj-static-call 的实参个数、
  deftype 描述的 drops）都是 long，下游按 `is long` 认（例如 `InlineClauses` 判断变长子句）。第一版写成 int 时 knowncall_semantics 在 C# 编译器上失败
  （变长函数的子句被当成固定子句内联），而 `=` 不区分 Int32 / Int64，差分看不出来——差分因此加了装箱类型的比较。
- **值边界**：编译后的 Clojure 代码经宿主调用（`first` / `second` / `nth` / 解构 / `get`）取出的标量要过 `ClojureStaticCalls.RetObject`（Int32 → Int64 等），
  对向量的 reduce（mapv）不经过。reader 元数据里的行列是 Int32，语法引用会把它们带进形式（`^{:tag ~t} (…)`），于是 Clojure 分析器 L0 里的
  `(quote 30)` 是 Int64，常量向量里的却仍是 Int32。C# 分析器在同样的位置（被分析的标量、quote、常量映射 / 集合的元素、case* 的测试值、
  transfer-source 带过去的值、proxy / gen-class / 桩布局里 `nth` 取出的值）做同样的转换（`Clj.Boundary`）。第一版没有做，用 C# 分析器编出的镜像
  在 `clojure.core$gen_delegate` 的一个方法上与借用分析器编出的 IL 不同，由此发现。
- **惰性序列的实现时机影响 fn 类名**：宏展开里 `(map #(gensym …) …)` 的惰性部分在第一次遍历时才取 id。fn* 的子句、reify* 的方法按 Clojure 的
  顺序惰性地逐个实现（类名先取 id），analyze-seq 在分派特殊形式之前不实现实参。差分侧把展开结果先整个实现，否则先跑的分析器替后跑的那个消耗 id。
- 源位置：Clojure 编译器把 `{:pos nil}` 也算"已记"，C# 的 `SourcePos` 没有这个区分；外层返回内层节点时内层上下文的位置就是外层的位置，
  两者结果相同，`--pos` 差分全语料 0 处不同。
- 借用路径经 unparse 只留文件名；C# 分析器的 `Node.Pos.File` 是加载名（`clojure/core.clj`），与 Clojure 编译器自己编出的 PDB 一致（见下）。

**验收**
- **G1 差分**（`tools/cs-diff.clj`，同一进程、同一形式；比较展开、L0、数的装箱类型与原有前中端七阶段）：
  - 镜像文件（boot-order.edn 的 41 个条目，`:loaded-by` 的用 `路径@ns`，只分析）：2238 个形式，L0 **0 差异**（其中 17 个两边报同样的错误），
    前中端 2221 个形式 0 差异，展开 0 差异；
  - `src/clojure/cleat/**/*.clj`：1226 个形式，0 差异；
  - 包（四个包的 src 都加 `-I`，`--eval`）：nrepl 533、neorepl 315、tools.build 150、tools.namespace 71，全部 0 差异；
  - `tests/Cleat.Host.Tests/data/*_semantics.clj*`（21 个，`--eval`）：470 个形式，0 差异（knowncall 里 2 个两边报同样的错误）；
  - `--pos`：以上全部语料源位置 0 处不同。
  - 没有需要记账的残余差异。
- 21 个 `*_semantics.clj*` 分别用 Clojure 编译器与 `CLEAT_COMPILER=cs`（C# 分析器）运行，标准输出 **21/21 相同**。
- 用 C# 编译器 + C# 分析器从 seed-stage1 编镜像（`--compile-boot`，约 7 s）：两次构建逐字节相同；与借用分析器编出的镜像相比，
  `.inl` 内容（反序列化后）相同，`ilspycmd -il` 反汇编**逐字相同**，只有 PDB 的文档路径不同——C# 分析器保留 `clojure/core.clj` 这样的加载名
  （与 Clojure 编译器编出的 `src/boot/cleat.boot.pdb` 一致），借用路径经 unparse 只剩 `core.clj`。
- 在这个**不含 Clojure 编译器包**的镜像上 `CLEAT_COMPILER=cs`：`-e "(+ 1 2)"`、一个脚本（ns + require clojure.set / string / walk / pprint、
  defmacro、defprotocol / defrecord / deftype、macroexpand-1、eval 报语法错误、clojure.repl/doc）、管道输入的 REPL（def、require）都正常，
  `cleat.compiler.analyzer` 命名空间不存在；同一镜像上 `CLEAT_CS_ANALYZER=clj` 按预期因找不到 `cleat/compiler/driver` 失败。
- 错误黄金文件：`CLEAT_COMPILER=cs dotnet test tests/Cleat.Host.Tests --filter "ClassGenerationTests|ValueBoundaryTests|ErrorGolden"`
  187/190；失败的 3 个（`errors/runtime/divide-by-zero-in-fn`、`RuntimeErrorsPointAtTheThrowingLine`、`StackTracesHideCompilerFramesUnlessAskedFor`，
  都是运行时错误报调用处的行而不是抛出处的行）在 `CLEAT_CS_ANALYZER=clj` 下同样失败，缺省（Clojure 编译器）190/190——
  属于 C# 编译器的发射 / 序列点，不是分析器，未在本任务处理。宏展开相关的错误黄金文件全部通过。
- `dotnet test tests/Cleat.Compiler.Tests`：45/45（原 26 + 新增 19）。
- 缺省模式不受影响：`dotnet build src/Cleat.Host -c Release` 0 警告 0 错误；`Cleat.Core.Tests` 125/125；Host 全量（`dotnet test tests/Cleat.Host.Tests -c Release`）291/291（32 m 58 s）。
