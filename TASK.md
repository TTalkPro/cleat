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
| 后续（LLVM，搁置） | cleat-004、cleat-037 ~ cleat-041 | 原生可执行文件、原生语料通过率。**决定（2026-10-10）：先完成 CIL 能力，LLVM 后端整体搁置**，设计保留在 [06](designs/06-llvm-backend.md) |

## 关键路径

**M8（2026-10-14 起，现行）**：081 → 082 → 083 → 084 → 085 → 086 → 087 → 088 → 089 → 090 → 091 → 092 → 093 → 094；095 不在关键路径上（[19 §5](designs/19-clojure-self-hosting.md)）。
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
