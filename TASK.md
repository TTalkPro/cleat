# 任务索引

本文件只做索引。任务详情见 `tasks/` 目录，设计见 [designs/](designs/README.md)。
状态：未开始 / 进行中 / 完成 / 搁置。工作约定：每个任务完成后提交一次，提交信息不带 Co-Authored-By。

**双轨纪律（M8，designs/19 D9，cleat-085 起到 cleat-091 默认切换为止；cleat-093 删除 Scheme 编译器后结束）**：Scheme 编译器（`src/scheme/lib/cleat/scheme/compiler*`、`src/scheme/clojure/`）只修缺陷、不加功能；修的缺陷同时在 Clojure 编译器（`src/clojure/cleat/compiler/`）里修，并补一条差分用例（`tools/compiler-diff.clj` 能看到的输入，或 `tests/nanopass` 的快照对照）。新的编译器功能等切换之后只做在 Clojure 编译器里。

| ID | 标题 | 里程碑 | 依赖 | 状态 |
|----|------|--------|------|------|
| [cleat-001](tasks/cleat-001-repository-skeleton.md) | 仓库骨架与构建约定（含许可分区确认） | M0 | 无 | 完成 |
| [cleat-002](tasks/cleat-002-test-assets.md) | 引入测试资产：jolt 语料、clojure-test-suite、test_clojure | M0 | cleat-001 | 完成 |
| [cleat-003](tasks/cleat-003-spike-dotnet-capabilities.md) | 验证 .NET 10 能力（Clojure 相关补充） | M0 | cleat-001 | 完成 |
| [cleat-004](tasks/cleat-004-spike-llvm-toolchain.md) | 验证 LLVM 工具链与原生 ABI（异常方案、调用约定） | 后续（LLVM） | cleat-001 | 搁置 |
| [cleat-005](tasks/cleat-005-spike-extract-clojurelang.md) | 提取试验：clojure.lang 不带 DLR 能否编译 | M0 | cleat-001 | 完成 |
| [cleat-006](tasks/cleat-006-core-values-and-collections.md) | Cleat.Core 骨架、值表示与持久集合 | M1 | cleat-005 | 完成 |
| [cleat-007](tasks/cleat-007-var-namespace-refs.md) | Var、Namespace、引用类型、STM 与 MultiFn | M1 | cleat-006 | 完成 |
| [cleat-008](tasks/cleat-008-function-model.md) | 函数模型 IFn / AFn / AFunction / RestFn | M1 | cleat-006 | 完成 |
| [cleat-009](tasks/cleat-009-reader.md) | reader 提取与决议接口 | M1 | cleat-006、cleat-007 | 完成 |
| [cleat-010](tasks/cleat-010-reflector.md) | Reflector 与 TypeResolver（无 DLR） | M1 | cleat-008 | 完成 |
| [cleat-011](tasks/cleat-011-primitives-and-primgen.md) | 原语标注与 PrimGen（primitives.edn） | M1 | cleat-006 ~ cleat-010 | 完成 |
| [cleat-012](tasks/cleat-012-rt-split-loader-hooks.md) | RT 拆分、加载器与运行时钩子 | M1 | cleat-007、cleat-009、cleat-010 | 完成 |
| [cleat-013](tasks/cleat-013-expander-macroexpand-resolve.md) | 展开器：宏展开与符号决议 → Lcore | M2 | cleat-009、cleat-010、cleat-012 | 完成 |
| [cleat-014](tasks/cleat-014-expander-special-forms.md) | 展开器：特殊形式检查、case 预处理、deftype 签名、类型提示 | M2 | cleat-013 | 完成 |
| [cleat-015](tasks/cleat-015-stage0-interpreter.md) | stage0：Lcore 解释器核心 | 替换（15 §5） | cleat-008、cleat-013 | 搁置 |
| [cleat-016](tasks/cleat-016-stage0-deftype.md) | stage0：deftype* / reify* 生成真实类型 | 替换（15 §5） | cleat-015、cleat-019、cleat-020 | 搁置 |
| [cleat-017](tasks/cleat-017-kernel-and-core-adaptation.md) | cleat.kernel 与 core.clj 分层适配 | M2 | cleat-011、cleat-013 | 完成 |
| [cleat-018](tasks/cleat-018-stage0-load-all-boot.md) | 在 stage0 上加载全部 boot 命名空间；语料子集通过 | 替换（15 §5） | cleat-016、cleat-017 | 搁置 |
| [cleat-019](tasks/cleat-019-lcil-spec-and-reader.md) | Lcil 规格（继承 + 扩展）与读取器 | M3 | cleat-003 | 完成 |
| [cleat-020](tasks/cleat-020-emitter-core.md) | 发射器核心：类型、接口、方法、switch、try | M3 | cleat-019 | 完成 |
| [cleat-021](tasks/cleat-021-constants-fasl.md) | 常量编码与 fasl | M3 | cleat-019、cleat-006、cleat-007 | 完成 |
| [cleat-022](tasks/cleat-022-module-conventions-and-loader.md) | 程序集约定、接口文件与加载器对接 | M3 | cleat-020、cleat-021、cleat-012 | 完成 |
| [cleat-023](tasks/cleat-023-nanopass-port.md) | cleat.nanopass 移植 | M8 取代 | cleat-018 | 由 cleat-083 ~ 091 取代 |
| [cleat-024](tasks/cleat-024-languages-and-parse-core.md) | 语言 L0 ~ L3 与 parse-core | M8 取代 | cleat-023、cleat-011 | 由 cleat-083 ~ 091 取代 |
| [cleat-025](tasks/cleat-025-frontend-passes.md) | 前中端 pass（循环识别、cp0-lite、box、自由变量） | M8 取代 | cleat-024 | 由 cleat-083 ~ 091 取代 |
| [cleat-026](tasks/cleat-026-codegen-cil-functions.md) | codegen-cil：函数、调用、Var、原语内联、控制流 | M8 取代 | cleat-025、cleat-020 | 由 cleat-083 ~ 091 取代 |
| [cleat-027](tasks/cleat-027-codegen-cil-types-case-try-interop.md) | codegen-cil：deftype / reify、case、try、互操作 | M8 取代 | cleat-026 | 由 cleat-083 ~ 091 取代 |
| [cleat-028](tasks/cleat-028-compiler-driver.md) | 编译驱动：compile-form / eval、compile、构建脚本 | M8 取代 | cleat-027、cleat-022 | 由 cleat-083 ~ 091 取代 |
| [cleat-029](tasks/cleat-029-phase-a-stage0-build.md) | Phase A：stage0 编译出 cleat.boot.dll | M8 取代 | cleat-028、cleat-018 | 由 cleat-083 ~ 091 取代 |
| [cleat-030](tasks/cleat-030-host-and-repl.md) | 宿主 Cleat.Host 与 REPL | M5 | cleat-022、cleat-012 | 完成 |
| [cleat-031](tasks/cleat-031-phase-b-fixpoint.md) | Phase B：自举不动点 | 并入 cleat-053 | cleat-029、cleat-030 | 搁置 |
| [cleat-032](tasks/cleat-032-conformance-baseline.md) | 符合性基线：语料、clojure-test-suite、test_clojure | M6 | cleat-053、cleat-002 | 完成 |
| [cleat-033](tasks/cleat-033-aot-and-load-path.md) | 命名空间 AOT（.clj.dll）与加载路径 | M7 | cleat-032 | 完成 |
| [cleat-034](tasks/cleat-034-publish-and-repl-editing.md) | 自包含发布与 REPL 行编辑 | M7 | cleat-033 | 完成 |
| [cleat-035](tasks/cleat-035-embedding-and-error-goldens.md) | 嵌入 API 与错误黄金文件 | M7 | cleat-033 | 完成 |
| [cleat-036](tasks/cleat-036-lcore-seed.md) | Lcore 种子（可选缓存；Phase A 过慢时提前到 M2） | 替换（15 §5） | cleat-021、cleat-018 | 搁置 |
| [cleat-037](tasks/cleat-037-targets-and-llvm-primitives.md) | 机器描述表与原语表的 llvm 列 | 后续（LLVM） | cleat-004、cleat-028 | 搁置 |
| [cleat-038](tasks/cleat-038-native-runtime-kernel.md) | 原生运行时 cleat-rt 内核（C） | 后续（LLVM） | cleat-004、cleat-037 | 搁置 |
| [cleat-039](tasks/cleat-039-codegen-llvm.md) | codegen-llvm | 后续（LLVM） | cleat-037、cleat-038、cleat-025 | 搁置 |
| [cleat-040](tasks/cleat-040-native-core-collections.md) | 原生 core：持久集合、惰性序列与协议（Clojure 实现） | 后续（LLVM） | cleat-039 | 搁置 |
| [cleat-041](tasks/cleat-041-native-driver-and-conformance.md) | 交叉编译驱动与原生语料验收 | 后续（LLVM） | cleat-040 | 搁置 |
| [cleat-042](tasks/cleat-042-performance.md) | 性能：拆箱、协议内联缓存、跨单元直接链接 | 后续 | cleat-032 | 完成 |
| [cleat-043](tasks/cleat-043-genclass-proxy-async.md) | gen-class、proxy 全量与异步 | 后续 | cleat-033 | 完成 |
| [cleat-044](tasks/cleat-044-spike-clojure-on-ironscheme.md) | PoC：Clojure on IronScheme | 决策前 | cleat-017 | 并入 cleat-049 |
| [cleat-045](tasks/cleat-045-vendor-scheme-runtime.md) | 纳入并更名 C# 元层：Cleat.Scheme、Cleat.Stage0、Emit / PrimGen 合并 | M2′ | cleat-017 | 完成 |
| [cleat-046](tasks/cleat-046-vendor-scheme-boot.md) | 纳入 Scheme 元层：boot、展开器、编译器、nanopass、种子；本仓库自举不动点 | M2′ | cleat-045 | 完成 |
| [cleat-047](tasks/cleat-047-try-catch-model.md) | 异常模型简化：删 call/cc 与处理器栈，加 `$try/catch`，重生成种子 | M2′ | cleat-046 | 完成 |
| [cleat-048](tasks/cleat-048-extensible-syntax-objects.md) | 可扩展语法对象：三张扩展表、8 处改造、Clojure 集合注册 | M2′ | cleat-047 | 完成 |
| [cleat-049](tasks/cleat-049-psyntax-clojure-extensions.md) | psyntax 的 Clojure 扩展：命名空间模型、rib 索引、macroexpand-1、错误钩子（含原 PoC 判据） | M4′ | cleat-048 | 完成 |
| [cleat-050](tasks/cleat-050-clojure-syntax-library.md) | `(clojure syntax)` 库：特殊形式、defmacro 垫片、语法引用、&env（不含 defsyntax） | M4′ | cleat-049 | 完成 |
| [cleat-051](tasks/cleat-051-compiler-clojure-nodes.md) | 编译器的 Clojure 节点：Var、IFn 约定、多类型 try/catch、deftype、switch、keyword-invoke | M4′ | cleat-050 | 完成 |
| [cleat-052](tasks/cleat-052-compile-driver-on-scheme.md) | 编译驱动：eval、load / compile 钩子、.clj.dll 与接口文件、宿主接入 | M4′ | cleat-051 | 完成 |
| [cleat-053](tasks/cleat-053-clojure-boot-image.md) | Clojure 自举：Scheme 镜像编译 core.clj → cleat.boot.dll，镜像不动点 | M5′ | cleat-052 | 完成 |
| [cleat-054](tasks/cleat-054-rename-scheme-libraries.md) | Scheme 库名 `(ironscheme …)` → `(cleat scheme …)`，同步重生成种子 | 后续 | cleat-053 | 完成 |
| [cleat-055](tasks/cleat-055-scheme-invisibility-boundaries.md) | "看不到 Scheme"的三道边界：macroexpand 停止点、值边界、诊断边界的测试与黄金文件 | M4′ | cleat-052 | 完成 |
| [cleat-056](tasks/cleat-056-nanopass-clojure-surface.md) | Clojure 表面的 nanopass：在 Scheme 侧给原版 nanopass 套 Clojure 语法 | 后续 | cleat-053 | 由 cleat-084 / 095 取代 |
| [cleat-057](tasks/cleat-057-clojure-defsyntax.md) | Clojure 的用户级卫生宏（defsyntax / syntax-rules） | 后续 | cleat-053 | 搁置 |
| [cleat-058](tasks/cleat-058-scheme-layer-skeg-alignment.md) | Scheme 元层按 skeg 对齐：guard / call-with-return / try·catch·finally·throw / #nil / EmptyList | 后续 | cleat-047、cleat-054 | 完成 |
| [cleat-059](tasks/cleat-059-ironscheme-name-cleanup.md) | 清理 IronScheme 名字残留：原语、格式头、清单、环境变量、nanopass 覆盖库扩展名 | 后续 | cleat-054、cleat-058 | 完成 |
| [cleat-060](tasks/cleat-060-library-fasl-extension.md) | 预编译库的扩展名统一为 .fasl（照 skeg 481f319） | 后续 | cleat-059 | 完成 |
| [cleat-061](tasks/cleat-061-r6rs-test-suite.md) | 纳入 R6RS 测试套件（照 skeg）、`bytevector-uint-set!` 修复、R6RS 门禁 | 后续 | cleat-059 | 完成 |
| [cleat-062](tasks/cleat-062-nrepl-package.md) | nREPL 包（packages/nrepl）：nREPL 1.8.0 移植到 CLR，上游测试全部移植 | 后续 | cleat-053、cleat-064 | 完成 |
| [cleat-063](tasks/cleat-063-neorepl-package.md) | neorepl 包（packages/neorepl）：终端 REPL 与 CLI，System.Console 上的行编辑器，jack-in 只认 cleat 项目 | 后续 | cleat-062、cleat-064 | 完成 |
| [cleat-064](tasks/cleat-064-port-found-runtime-fixes.md) | 移植 nREPL / neorepl 时发现的缺陷：宏展开出的 def 的源位置、以 - 开头的协议方法、带超时的 deref、ReaderException 的消息、spit 不截断 | 后续 | cleat-053 | 完成 |
| [cleat-065](tasks/cleat-065-literal-metadata-evaluation.md) | 集合字面量上的非常量元数据求值 | 后续 | cleat-064 | 完成 |
| [cleat-066](tasks/cleat-066-tools-build-package.md) | tools.build 包（packages/tools.build）：照官方构建方案，NuGet 依赖（兼容 BaGetter）、编包程序集、dotnet build C# / F#、pack / push | 后续 | cleat-067 | 完成 |
| [cleat-067](tasks/cleat-067-lazy-runtime-startup.md) | 包程序集（每包一个，去掉源码旁 .clj.dll）、运行时 NuGet 包、生成类型惰性启动 CleatRuntime（C# / F# 冷加载） | 后续 | cleat-043、cleat-063 | 完成 |
| [cleat-068](tasks/cleat-068-version-and-banner.md) | 版本号与 banner：cleat 1.0.0、`*clojure-version*` 1.13.0、`*cleat-version*`、nREPL describe 的 `:cleat`、`Cleat 1.0.0 (Clojure 1.13.0)` | 后续 | cleat-062、cleat-063 | 完成 |
| [cleat-069](tasks/cleat-069-example-project.md) | 完整的示例项目 examples/WebServer：BaGetter 取依赖、neorepl 交互开发（jack-in 用 basis、`cleat-run -M:别名`）、`dotnet build` 混合 C#、gen-class 给 C# 用 | 后续 | cleat-066、cleat-067、cleat-068 | 完成 |
| [cleat-070](tasks/cleat-070-error-phase-and-republish.md) | 错误 phase 对齐 JVM（不带命名空间）、cleat-run 转发 SIGTERM、包的新版本（Cleat.Runtime 1.0.1 等）重推 BaGetter | 后续 | cleat-069 | 完成 |
| [cleat-071](tasks/cleat-071-cleat-dotnet-tool.md) | cleat 宿主作为 dotnet tool（Cleat.Cli），内置 `-M:别名`（clj -M）与 `-T:build`（clj -T），随附 tools.build；删掉 cleat-run / cleat-build 脚本 | 后续 | cleat-066、cleat-069、cleat-070 | 完成 |
| [cleat-072](tasks/cleat-072-cli-modes-and-gen-class-main.md) | Clojure CLI 的执行模式（`-X`、按别名隔离的 `-T`、`-Sdeps` / `-Spath` / `-P`）与示例里带 `-main` 的 gen-class（`Web.Cli`）；Cleat.Runtime / Cleat.Cli 1.0.2 | 后续 | cleat-071 | 完成 |
| [cleat-073](tasks/cleat-073-sqlite-console-example.md) | 控制台示例 SqlTable（Clojure 为主，SQLite NuGet，文件 + 一行 SQL 输出表格）与 NuGet 包的原生库（basis 的 native 资产、加载器找原生库）；Cleat.Runtime / Cleat.Cli 1.0.3、ToolsBuild 0.10.17、Neorepl 0.1.2 | 后续 | cleat-072 | 完成 |
| [cleat-074](tasks/cleat-074-uber-launcher-name.md) | uber 的启动脚本以应用命名（`:launcher`，缺省为 `:uber-dir` 的目录名，代替写死的 `run`）；Cleat.ToolsBuild 0.10.18、Cleat.Runtime / Cleat.Cli 1.0.4 | 后续 | cleat-073 | 完成 |
| [cleat-075](tasks/cleat-075-textual-port-eol-from-skeg.md) | 文本端口的行尾与缓冲模式（移植 skeg iron-074）：get-line 只认换行符、转码端口在字符层转换行尾、output-port-buffer-mode 返回打开时的模式；R6RS 套件 8971 项全过；tests/scheme/base/r6rs-edges.sps | 后续 | cleat-061 | 完成 |
| [cleat-076](tasks/cleat-076-skeg-m10-runtime-and-control.md) | Scheme 元层的 M10 性能项（移植 skeg iron-061 ~ 068）：数值快路径、Karatsuba 平方根、guard / try 正常返回不逃逸、局部逃逸编成跳转、内联 with-clr-exception-handler、equal? / assoc / append / values 等热点；base-library.sps；顺带修 neorepl 会话缓存的并发读 | 后续 | cleat-075 | 完成 |
| [cleat-077](tasks/cleat-077-primdata-folding-chez-mats.md) | 原语元数据与查表折叠、Chez mats 对拍（移植 skeg iron-069 / 072）：原语表版本 2（签名、mifoldable / discard 等标志）、primdata.sls、cp0-lite 查表折叠；Chez 10.5 的 18 个 mats 文件（ChezMatsTests，known.ss 按 cleat 的结果生成）；对拍修的 R6RS 边界语义、Unicode 派生属性；div / mod 的 fixnum 快路径（cleat 补） | 后续 | cleat-076 | 完成 |
| [cleat-078](tasks/cleat-078-cp0-inlining.md) | 过程内联与 let 传播（移植 skeg iron-070）：cp0-inline / simplify-lets（只用于 Scheme 代码，Clojure 的 fn* 不参与）、vector-ref / vector-set! 内联、letrec 局部过程直接调用、eqv? → eq?、list → cons；NuGetColdLoadTests 隔离 DOTNET_CLI_HOME | 后续 | cleat-077 | 完成 |
| [cleat-079](tasks/cleat-079-type-lattice-narrowing.md) | 类型格与收窄、推测 fixnum 的拆箱循环、fixnum 装箱缓存扩大、simplify-if 与 commonize（移植 skeg iron-071 / 073）；与 cleat-042 的 l64 / 宿主调用类型推断合在一起 | 后续 | cleat-078 | 完成 |
| [cleat-080](tasks/cleat-080-republish-after-skeg-port.md) | 移植 skeg 之后的新版本：Cleat.Runtime / Cleat.Cli 1.0.5、ToolsNamespace 1.5.3、ToolsBuild 0.10.19、Nrepl 1.8.2、Neorepl 0.1.3，验证后重推 BaGetter | 后续 | cleat-079 | 完成 |
| [cleat-081](tasks/cleat-081-self-hosting-plan.md) | Clojure 自寄宿：设计与任务拆分（designs/19） | M8 | cleat-080 | 完成 |
| [cleat-082](tasks/cleat-082-move-clojure-runtime-helpers.md) | Clojure 运行时辅助类（Locals、ClojureOps、ClojureStaticCalls、HostSyntax、RT.IsTrue、Driver 的非 Scheme 部分）搬进 Cleat.Core | M8 | cleat-081 | 完成 |
| [cleat-083](tasks/cleat-083-nanopass-languages.md) | cleat.nanopass 语言层：define-language、extends、deftype 记录、unparse、注册表；L0 ~ L3 的 Clojure 拼写 | M8 | cleat-081 | 完成 |
| [cleat-084](tasks/cleat-084-nanopass-passes.md) | cleat.nanopass pass 层：define-pass、nanopass-case、(out …) 模板、catamorphism、自动补全；第一层；第二层拆到 cleat-095 | M8 | cleat-083 | 完成 |
| [cleat-085](tasks/cleat-085-compiler-skeleton-and-diff.md) | cleat.compiler.dll 骨架、CLEAT_COMPILER 开关、逐 pass 导出与差分工具、L0 普查 | M8 | cleat-082、cleat-084 | 完成 |
| [cleat-086](tasks/cleat-086-clojure-analyzer.md) | Clojure 前端（分析器）直接产出 L0，取代 psyntax 与 (clojure syntax)；G1@L0 | M8 | cleat-085 | 完成 |
| [cleat-087](tasks/cleat-087-middle-passes.md) | 前中端 pass 用 cleat.nanopass 重写；G1@la/l1/l2/l3 | M8 | cleat-086 | 完成 |
| [cleat-088](tasks/cleat-088-codegen-core.md) | codegen 一：单元、常量、Var 与调用、控制流、try、fn* 类、局部变量清除、类型推断与原始签名、case、直接链接；G2（eval 单元） | M8 | cleat-087 | 完成 |
| [cleat-089](tasks/cleat-089-codegen-types-and-classes.md) | codegen 二：deftype / reify / proxy / gen-class、AOT 常量编码；G2（lcil + aot） | M8 | cleat-088 | 完成 |
| [cleat-090](tasks/cleat-090-clojure-compiler-driver.md) | Clojure 写的编译驱动，CLEAT_COMPILER=clojure 下全量测试（G3） | M8 | cleat-089 | 完成 |
| [cleat-091](tasks/cleat-091-self-hosting-and-lcil-seed.md) | 自举不动点（G4）、Lcil 文本种子、构建不再经 stage0、默认切到 Clojure 编译器 | M8 | cleat-090 | 完成 |
| [cleat-092](tasks/cleat-092-de-scheme-codegen.md) | 生成代码去 Scheme：catch 不用 Procedure、去 EmptyList 占位、Clojure 数据常量表与 .inl、(runtime clojure) | M8 | cleat-091 | 完成 |
| [cleat-093](tasks/cleat-093-remove-scheme.md) | 删除 Scheme：src/scheme、Cleat.Scheme、Cleat.Stage0、PrimGen、Scheme 测试、构建目标、NOTICE、文档归档；宿主只有 Clojure 编译器 | M8 | cleat-092 | 完成 |
| [cleat-094](tasks/cleat-094-republish-self-hosted.md) | 自寄宿后的新版本（Cleat.Runtime / Cli 1.1.0 等）重推 BaGetter，cleat.nanopass 用户文档 | M8 | cleat-093 | 完成 |
| [cleat-095](tasks/cleat-095-nanopass-user-layer.md) | cleat.nanopass 第二层：显式 catamorphism、多层省略号的单项、define-parser、trace、上游测试（取代 cleat-056；不阻塞阶段 B / C） | M8 | cleat-084 | 完成 |
| [cleat-096](tasks/cleat-096-defn-name-return-hint.md) | defn 名字上的原始返回类型提示：调用点与函数类的 primifs 签名一致（JVM 在此 AbstractMethodError） | M9 | cleat-095 | 完成 |
| [cleat-097](tasks/cleat-097-refresh-baselines.md) | 刷新 clojure-test-suite 与 test_clojure 基线，分类剩余失败 | M9 | cleat-095 | 完成 |
| [cleat-098](tasks/cleat-098-divergence-cleanup.md) | 偏离表清理：error-model / numeric / boxing / strictness / printf / not-yet 逐条判断；nREPL middleware 测试改回上游写法 | M9 | cleat-095 | 完成 |
| [cleat-099](tasks/cleat-099-reflection-warnings.md) | 反射警告：*warn-on-reflection* 为真时分析器按 ClojureCLR 的措辞报出决议不了的宿主调用（test_clojure rt 的 18 条）；顺带修复镜像文件被 require 重载 | M9 | cleat-097 | 完成 |
| [cleat-100](tasks/cleat-100-perf-measurement.md) | 性能测量基建：分配采样器（搬 skeg alloc.cs）、装箱探针、基准补充（无提示循环、嵌套算术、构造、属性、mbrot） | M10 | cleat-099 | 完成 |
| [cleat-101](tasks/cleat-101-static-resolution-scope.md) | codegen 静态决议范围：构造器、属性、值类型目标、任意引用形参、泛型方法（现在全走 Reflector） | M10 | cleat-100 | 完成 |
| [cleat-102](tasks/cleat-102-num-and-dual-locals.md) | Num：不装箱的未定类型数（skeg iron-080，Clojure 语义）；嵌套算术与 let 变量的双局部表示（082 / 083） | M10 | cleat-101 | 完成 |
| [cleat-103](tasks/cleat-103-loop-speculation.md) | 循环的 long / double 推测：快慢两份循环（skeg iron-071 / 080B） | M10 | cleat-102 | 完成 |
| [cleat-104](tasks/cleat-104-typed-entries.md) | 无提示已知函数的类型化入口（skeg iron-075 / 076，溢出抛异常不重算） | M10 | cleat-103 | 完成 |
| [cleat-105](tasks/cleat-105-closure-and-inlining-budget.md) | 量化后决定：封闭局部 fn 提升、闭包直接 Invoke、内联预算（skeg iron-085 / 070） | M10 | cleat-104 | 完成（量化后不实现） |
| [cleat-106](tasks/cleat-106-array-resolution.md) | 数组访问的静态决议：引用类型提示带到决议器、数组形参、类型化 aget（skeg 的 array1：long-array 版 10.7 s → 0.5 s） | M10 | cleat-102 | 完成 |
| [cleat-107](tasks/cleat-107-r7rs-vs-jvm.md) | r7rs-benchmarks 的 Clojure 移植：cleat 对 JVM Clojure（linux01，同一份 .cljc，只计内部时间；56 项，几何平均 cleat / JVM = 4.52） | M10 | cleat-105 | 完成 |
| [cleat-108](tasks/cleat-108-branch-join-raw-uses.md) | 推断的原始用途穿过分支汇合：if / let / begin 尾位置里的引用算原始用途 | M11 | cleat-107 | 完成 |
| [cleat-109](tasks/cleat-109-small-int-box-cache.md) | 小整数装箱缓存与运行时数值的快路径 | M11 | cleat-108 | 完成 |
| [cleat-110](tasks/cleat-110-user-known-calls.md) | 用户函数的已知调用：经 Var 调用、分析时 Var 的值是有 invokeStatic 的函数类时，isinst 守卫后直接 call | M11 | cleat-109 | 完成 |
| [cleat-111](tasks/cleat-111-runtime-fast-paths.md) | 运行时快路径：Util.equiv(Object, long / double)、zero? / pos? / neg? 作用于 Object、(byte x) 与 byte[] 的 aset、符号相等 | M11 | cleat-110 | 完成 |
| [cleat-112](tasks/cleat-112-result-types-to-inference.md) | 结果类型交给推断：宿主调用返回的数组类型给无提示局部、deftype 原始字段读取的类型进算术 | M11 | cleat-111 | 完成 |
| [cleat-113](tasks/cleat-113-float-loops.md) | 浮点循环：从对象数组 / 向量取出的 double 与 Num 的覆盖范围 | M11 | cleat-112 | 完成 |
| [cleat-114](tasks/cleat-114-tiered-eval.md) | eval 代码分层编译与动态 PGO（不可回收的装载上下文、tier-0 的 GC 报告） | M11 | cleat-113 | 完成 |
| [cleat-115](tasks/cleat-115-runtime-and-vars.md) | 运行时与 Var 访问：Numbers 的 object 快路径、$Vars 的 static readonly 字段、Symbol 密封 | M11 | cleat-114 | 完成 |
| [cleat-116](tasks/cleat-116-eval-inline.md) | eval 的小函数内联与运行时快路径（double 装箱、Cons 的 first / next） | M11 | cleat-115 | 完成 |
| [cleat-117](tasks/cleat-117-image-tiering-and-lifetimes.md) | 镜像与包分层编译、tier-0 生命周期与惰性序列根释放 | M11 | cleat-116 | 完成 |
| [cleat-118](tasks/cleat-118-typed-call-sites.md) | 类型化调用点：窄类型数组的类型化访问与原始值贯通（非递归数值函数、已知局部 lambda 直调量化后放弃） | M11 | cleat-117 | 完成 |
| [cleat-119](tasks/cleat-119-r7rs-rerun.md) | M11 收尾：r7rs 56 项与 bench/ 全部重测，更新 cleat-107 的表与 designs/21 | M11 | cleat-118 | 完成 |
| [cleat-120](tasks/cleat-120-osr-and-tiering.md) | array1 的 OSR 复测、诊断与保守分层规则（两机实测后先确认回退仍显著） | M12 | cleat-119 | 完成 |
| [cleat-121](tasks/cleat-121-runtime-allocation-fast-paths.md) | 运行时快路径的分配与 JIT 内联预算；LongBox 范围量化 | M12 | cleat-119 | 完成 |
| [cleat-122](tasks/cleat-122-local-throw-to-leave.md) | 本方法内已知 throw 编成 leave（保留其他异常捕获；先统计，预计关闭） | M12 | cleat-119 | 关闭（频率为零） |
| [cleat-123](tasks/cleat-123-limited-scalar-replacement.md) | 有限标量替换：非逃逸 primitive record / deftype | M12 | cleat-119 | 完成 |
| [cleat-124](tasks/cleat-124-string-copy-reduction.md) | 字符串热点的 UTF-16 搬运优化 | M12 | cleat-119 | 关闭（试过无收益） |
| [cleat-125](tasks/cleat-125-nonlocal-escape-protocol.md) | 非本地逃逸协议调研与原型 | M12 | cleat-122 | 完成 |
| [cleat-126](tasks/cleat-126-gc-performance-rerun.md) | M12 收尾：GC、分配与性能全量重测 | M12 | cleat-120 ~ 125 | 完成 |
| [cleat-127](tasks/cleat-127-array-param-speculation.md) | 数组推测：循环里无提示数组变量的类型化访问 | M13 | cleat-118 | 完成 |
| [cleat-128](tasks/cleat-128-type-feedback-profiling.md) | 类型反馈：剖面记录、触发与量化 | M13 | cleat-119 | 完成 |
| [cleat-129](tasks/cleat-129-type-feedback-specialization.md) | 类型反馈：特化单元的编译、安装、退回与失效 | M13 | cleat-128 | 关闭（量化后不实现） |
| [cleat-130](tasks/cleat-130-m13-rerun.md) | M13 收尾：数组推测微基准、r7rs 无提示变体与全量重测 | M13 | cleat-127、128 | 完成 |
| [cleat-131](tasks/cleat-131-arrayspec-local-bindings.md) | 数组推测候选的作用域修复 | M13 后续 | cleat-127、130 | 完成 |
| [cleat-132](tasks/cleat-132-signature-specialization.md) | 参数签名特化分发原型 | M13 后续 | cleat-104、128 | 完成 |
| [cleat-133](tasks/cleat-133-runtime-sigspec.md) | 运行时签名特化：观察 → 按需生成 → 查表分发 | M14 | cleat-132 | 完成 |
| [cleat-134](tasks/cleat-134-reference-signature-specialization.md) | 引用类型运行时签名特化：精确观察 + Tagged 静态决议 | M14 | cleat-133 | 完成 |
| [cleat-135](tasks/cleat-135-runtime-signature-tables.md) | 运行时签名表与高元数特化：5～20 参 + 多签名上限 | M14 | cleat-134 | 完成 |
| [cleat-136](tasks/cleat-136-runtime-signature-recycling.md) | 运行时签名回收与逐出 | M14 | cleat-135 | 完成 |
| [cleat-137](tasks/cleat-137-remove-ll-dd-pregeneration.md) | 删除 cleat-132 的 LL / DD 编译期预生成 | M15 | cleat-136 | 完成 |
| [cleat-138](tasks/cleat-138-signature-key-table.md) | 单一签名 key 与 RuntimeSigTable：最优路径查表、哨兵未命中、形参掩码 | M15 | cleat-137 | 完成 |
| [cleat-139](tasks/cleat-139-whole-function-specialization.md) | 整体函数特化（O 模式）：任意体重新生成、object 兜底、身份守卫自调用 | M15 | cleat-138 | 完成 |
| [cleat-140](tasks/cleat-140-call-path-type-inference.md) | 编译期调用路径类型推导：调用图、类型摘要、确认的快速路径编译期生成 | M15 | cleat-139 | 完成 |
| [cleat-141](tasks/cleat-141-m15-validation.md) | M15 收口：压力模式全量基线、种子重生成、r7rs 三方重跑 | M15 | cleat-140 | 完成 |
| [cleat-142](tasks/cleat-142-char-constant-boxing.md) | char 常量装箱缓存：常量发射点不再每次 box（`=` 的 char 原始比较留给 147） | M16 | 无 | 完成 |
| [cleat-143](tasks/cleat-143-string-seq-direct-read.md) | String 的 seq / first / next / more 直读快路径，不再每次新建 StringSeq（含分支顺序修正） | M16 | 无 | 完成 |
| [cleat-144](tasks/cleat-144-cross-ns-deftype-resolution.md) | 跨 ns 的 deftype / definterface 编译期决议（`:import` 兼容缺口） | M16 | 无 | 完成 |
| [cleat-145](tasks/cleat-145-accessor-inlining.md) | 小访问器跨编译单元内联（含镜像 / 包构建，与 restrict-loop-inlines 协调） | M16 | cleat-144 | 完成（2026-10-21，代码全落地但**开关默认关**：`accinline` 是 opt-in，要 `CLEAT_OPT_ACCINLINE=1` 才开 —— 111 的同轮配对 A/B 只是中性（中位几何平均 x1.002）且 browse 5/5 同向慢 5.8%，没有一项可声明收益，见 notes M16 §8；只做 eval / 包两条路径，"扩展到镜像"半段明确不做——内联决定不进种子） |
| [cleat-146](tasks/cleat-146-keyword-symbol-intern.md) | keyword intern 的字符串键命中不新建 Symbol，`(str <long>)` 专用路径 | M16 | 无 | 完成 |
| [cleat-147](tasks/cleat-147-nth-closure-varargs.md) | `RT.nth` 非 Indexed 判断链 + 闭包提升与变长参数数组的量化 | M16 | 无 | 完成（2026-10-22：第 1 条 nthfast 已落地并修复空列表回归；第 2 / 3 条 M17 量化 + 决策收口——闭包提升上限 ~17 ns/iter、变长参数数组 +57~63 ns/iter，均只记量化上限，不在 M17 塞实现） |
| [cleat-148](tasks/cleat-148-gc-profile-loh.md) | GC profile 可声明化（不改默认值）与 LOH 初始化无屏障写入评估 | M16 | 无 | 完成 |
| [cleat-149](tasks/cleat-149-regression-and-measurement.md) | parsing / mbrot / pi 回归排查（开关矩阵 + bisect）与测量口径修正 | M16 | 无 | 完成 |
| [cleat-150](tasks/cleat-150-m16-rerun.md) | M16 收口：全量验证、r7rs 56 项三臂整体重跑与开关贡献分解（runner `bench/r7rs/m16full.py` 已就位，`DEFAULT_NOPT` 已含 M16 的五项默认开关；`accinline` 是默认关的 opt-in，不进名单） | M16 | cleat-142 ~ 149 | 完成（2026-10-22：对 JVM x1.594（M15 1.61x → 可声明改善）、开关贡献 x0.981；array1 +2.9% 归因 intern、lattice +2.5% 归因 strseq/crossnstype，记账保留默认开；runner 补断点续跑；notes M16 收口节） |
| [cleat-151](tasks/cleat-151-noise-floor-and-full-rerun.md) | M17 口径：本机噪声地板标定（判定门槛写进汇总器）+ r7rs 56 项三臂全量重跑 | M17 | cleat-150 | 完成（2026-10-22：本机地板成表（pi 1.050 ~ takl 1.456，检出 5% 需 5~175 对），门槛落进 m16sum.py（R7_FLOOR，无表时行为逐字不变）；111 三臂复核 x1.596 / x0.983 与 150 一致） |
| [cleat-152](tasks/cleat-152-m1415-regression-bisect.md) | M14 / M15 回退（mbrot / pi / dynamic）的 commit 级定位与机制归因 | M17 | cleat-151 | 未开始 |
| [cleat-153](tasks/cleat-153-callsite-type-feedback.md) | 调用点的运行时类型反馈：接口调用单态化（复用 M14 / M15 的签名表基建） | M17 | cleat-145 | 未开始 |

## 里程碑

| 里程碑 | 任务 | 产出 |
|--------|------|------|
| M0 准备与验证 | cleat-001 ~ cleat-003、cleat-005 | 骨架、测试资产、.NET 与提取两项验证结论 |
| M1 新运行时 | cleat-006 ~ cleat-012 | 无 DLR 的 `Cleat.Core`，`primitives.edn` |
| M2 展开器 | cleat-013、014、017 完成；015 / 016 / 018 替换 | C# 展开器成为 Scheme 版的规格 |
| M2′ 纳入元层 | cleat-045 ~ cleat-048 | Scheme 镜像在本仓库自举、try / catch 模型、可扩展语法对象 |
| M3 Lcil 与发射器 | cleat-019 ~ cleat-022 | 手写 Lcil → dll 可运行 |
| M4 nanopass 与编译器 | 替换（15 §5） | — |
| M4′ Clojure 在元层上 | cleat-049 ~ cleat-052、cleat-055 | 命名空间模型、(clojure syntax)、编译器 Clojure 节点、驱动、Scheme 不可见边界 |
| M5 首次自举 | 029 替换；030 骨架完成 | — |
| M5′ Clojure 自举 | cleat-053 | `cleat.boot.dll` 与镜像不动点 |
| M6 符合性 | cleat-032（031 并入 053） | 三个基线 |
| M7 工程 | cleat-033 ~ cleat-035（036 替换） | AOT、发布、REPL、嵌入 |
| 后续 | cleat-042、043、054、056、058 ~ 080（057 搁置） | 性能、gen-class / proxy / 异步、库名更名、Clojure 表面的 nanopass |
| M8 Clojure 自寄宿 | cleat-081 ~ cleat-095 | Clojure 版 nanopass 与编译器、自举不动点、Lcil 文本种子、Scheme 全部移除（[19](designs/19-clojure-self-hosting.md)） |
| M9 质量与收口（2026-10-15 起） | cleat-096 ~ cleat-099 | 自举后的编译器对着两套大基线验收、已知缺陷、偏离表清理 |
| M10 性能（2026-10-15 起） | cleat-100 ~ cleat-107 | 从 skeg M10 ～ M12 借鉴：Num、双局部、循环推测、类型化入口；cleat 自己的静态决议范围（[20](designs/20-skeg-optimizations.md)） |
| M11 r7rs 差距（2026-10-15 起） | cleat-108 ~ cleat-119 | cleat-107 的 r7rs 对比找出的慢区：分支汇合、运行时快路径、结果类型、浮点循环、分层生命周期、类型化调用点（[21](designs/21-r7rs-gaps.md)） |
| M12 GC 与性能后续（2026-10-16 起） | cleat-120 ~ cleat-126 | tier-0 / OSR、分配快路径、局部异常跳转、有限标量替换、字符串搬运与非本地逃逸（[22](designs/22-gc-and-performance-followups.md)） |
| M13 按参数类型特化（2026-10-16 起） | cleat-127 ~ cleat-131 | 无提示数组形参的推测、运行时类型反馈特化与候选作用域修复（[23](designs/23-type-specialization.md)） |
| M14 运行时签名特化（2026-10-18 起） | cleat-132 ~ cleat-136 | 观察 → 按需生成 → 查表分发、引用形参、多签名表与回收（[24](designs/24-runtime-signature-specialization.md) ~ [26](designs/26-runtime-signature-tables.md)） |
| M15 JIT 式特化（2026-10-19 起） | cleat-137 ~ cleat-141 | 删除 LL / DD 预生成、单一签名 key、整体函数特化、编译期调用路径类型推导（[27](designs/27-jit-style-runtime-specialization.md)） |
| M16 热路径优化（2026-10-20 起） | cleat-142 ~ cleat-150 | M15 归因复核后的修正优先级：char 装箱、String 直读、跨 ns deftype 决议与访问器内联、intern、nth / 闭包 / 变长、GC profile、回归排查（[28](designs/28-hot-path-optimizations.md)） |
| M17 口径与回退收口（2026-10-21 起） | cleat-151 ~ cleat-153 | 本机噪声地板标定 + 56 项三臂全量、M14 / M15 回退的 commit 级定位、调用点类型反馈（承接 147 第 2 / 3 条与 149 遗留的三项待办） |
| 后续（LLVM，搁置） | cleat-004、cleat-037 ~ cleat-041 | 原生可执行文件、原生语料通过率。**决定（2026-10-10）：先完成 CIL 能力，LLVM 后端整体搁置**，设计保留在 [06](designs/06-llvm-backend.md) |

## 关键路径

**M16（2026-10-20 起，2026-10-22 完成）**：142 / 143 / 146 完成（远端同窗口开 / 关 A/B：wc -39%、browse -4%、read1 -2.5%，lattice 的 strseq 分支位置回退已修正，见 [notes M16](notes/r7rs-performance.md)）→ 149（先还债、稳口径）/ 144 / 148 并列 → 145（依赖 144，2026-10-21 落地，实测只是中性因此**默认关**、开关 opt-in）→ 147 → 150 收口完成（56 项三臂全量 + 开关贡献分解：对 JVM x1.594、开关贡献 x0.981，array1 / lattice 回退已归因记账，[28 §7](designs/28-hot-path-optimizations.md)）。

**M17（2026-10-21 起，现行）**：151（先标定）已于 2026-10-22 完成 → 152（依赖 151 的门槛，判据改造已于 2026-10-22 落地，下一步跑 bisect）→ 153（依赖 145 的守卫形态，**145 已于 2026-10-21 落地，依赖已就位**；量化探针已于 2026-10-22 落地）。147 的第 2 / 3 条（闭包提升、变长参数数组，量化基建已于 2026-10-22 落地，2026-10-22 量化 + 决策收口）也在 M17 内做，任务书仍挂在 cleat-147。

**M13（2026-10-16 起）**：127（依赖 118）与 128（依赖 119）可并列，129 在 128 之后，最后 130；与 M12 互不依赖（[23 §3](designs/23-type-specialization.md)）。

**M15（2026-10-19 起）**：137 → 138 → 139 → 140 → 141；JIT 式运行时重新编译与编译期调用路径类型推导（[27](designs/27-jit-style-runtime-specialization.md)）。

**M14（2026-10-18 起）**：133 → 134 → 135 → 136；134 扩展引用形参，135 扩展多签名表与 5～20 参候选，136 收口批次回收与逐出（[24 §6](designs/24-runtime-signature-specialization.md)、[25](designs/25-runtime-reference-signature-specialization.md)、[26](designs/26-runtime-signature-tables.md)）。

**M12（2026-10-16 起）**：119 收口后，122（先统计）、120（先复测）、121、123、124 并列，125 在 122 之后，最后 126（[22 §8](designs/22-gc-and-performance-followups.md)）。

**M11（2026-10-15 起）**：108 → 109 → 110 → 111 → 112 → 113 → 114 → 115 → 116 → 117 → 118 → 119（[21 §2](designs/21-r7rs-gaps.md)）。

**M10（2026-10-15，完成）**：100 → 101 → 102 → 106 → 103 → 104 → 105 → 107（[20 §7](designs/20-skeg-optimizations.md)）。

**M9（2026-10-15，完成）**：096 → 097 → 098 → 099（互不依赖，按此顺序做完）。

**M8（2026-10-14 ~ 15，完成）**：081 → 082 → 083 → 084 → 085 → 086 → 087 → 088 → 089 → 090 → 091 → 092 → 093 → 094；095 不在关键路径上（[19 §5](designs/19-clojure-self-hosting.md)）。
双轨纪律（19 D9）：从 cleat-085 起到 cleat-091 默认切换为止，Scheme 编译器只修缺陷、不加功能，修的缺陷同时在 Clojure 编译器里修并补差分用例；cleat-093 删除 Scheme 编译器后结束。

此前：cleat-045 → 046 → 047 → 048 → 049 → 050 → 051 → 052 → 055 → 053 → 032（原路径 013 → 015 → … → 029 → 031 已由此取代）。LLVM 相关任务搁置，不在当前计划内。

## 本轮讨论落实到任务的对应

| 讨论结论 | 任务 |
|---|---|
| 纳入 IronScheme 代码并更名，不作依赖（14 §7、15 §1） | 045、046、054 |
| 编译器用 Scheme 写，但语法对象必须可扩展（15 §2） | 048 |
| 尽量不改 nanopass（15 §4）：以快照与不动点为证据 | 046、048 的验收 |
| 异常模型简化为 try / catch，无 call/cc（15 §3） | 047 |
| 命名空间模型是通过 / 不通过判据（14 §8） | 049 |
| Clojure 完全看不到 Scheme 的三道边界（13 §7） | 055 |
| defmacro 完全兼容（2026-10-10：先保证 Clojure 语法兼容，用户级卫生宏不急） | 050；卫生宏 057 搁置（2026-10-12：无法保证完整卫生） |
| Clojure 用户也能用 nanopass（可选） | 056 |
