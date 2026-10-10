# 任务索引

本文件只做索引。任务详情见 `tasks/` 目录，设计见 [designs/](designs/README.md)。
状态：未开始 / 进行中 / 完成 / 搁置。工作约定：每个任务完成后提交一次，提交信息不带 Co-Authored-By。

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
| [cleat-015](tasks/cleat-015-stage0-interpreter.md) | stage0：Lcore 解释器核心 | M2 | cleat-008、cleat-013 | 未开始 |
| [cleat-016](tasks/cleat-016-stage0-deftype.md) | stage0：deftype* / reify* 生成真实类型 | M2 | cleat-015、cleat-019、cleat-020 | 未开始 |
| [cleat-017](tasks/cleat-017-kernel-and-core-adaptation.md) | cleat.kernel 与 core.clj 分层适配 | M2 | cleat-011、cleat-013 | 完成 |
| [cleat-018](tasks/cleat-018-stage0-load-all-boot.md) | 在 stage0 上加载全部 boot 命名空间；语料子集通过 | M2 | cleat-016、cleat-017 | 未开始 |
| [cleat-019](tasks/cleat-019-lcil-spec-and-reader.md) | Lcil 规格（继承 + 扩展）与读取器 | M3 | cleat-003 | 完成 |
| [cleat-020](tasks/cleat-020-emitter-core.md) | 发射器核心：类型、接口、方法、switch、try | M3 | cleat-019 | 完成 |
| [cleat-021](tasks/cleat-021-constants-fasl.md) | 常量编码与 fasl | M3 | cleat-019、cleat-006、cleat-007 | 完成 |
| [cleat-022](tasks/cleat-022-module-conventions-and-loader.md) | 程序集约定、接口文件与加载器对接 | M3 | cleat-020、cleat-021、cleat-012 | 完成 |
| [cleat-023](tasks/cleat-023-nanopass-port.md) | cleat.nanopass 移植 | M4 | cleat-018 | 未开始 |
| [cleat-024](tasks/cleat-024-languages-and-parse-core.md) | 语言 L0 ~ L3 与 parse-core | M4 | cleat-023、cleat-011 | 未开始 |
| [cleat-025](tasks/cleat-025-frontend-passes.md) | 前中端 pass（循环识别、cp0-lite、box、自由变量） | M4 | cleat-024 | 未开始 |
| [cleat-026](tasks/cleat-026-codegen-cil-functions.md) | codegen-cil：函数、调用、Var、原语内联、控制流 | M4 | cleat-025、cleat-020 | 未开始 |
| [cleat-027](tasks/cleat-027-codegen-cil-types-case-try-interop.md) | codegen-cil：deftype / reify、case、try、互操作 | M4 | cleat-026 | 未开始 |
| [cleat-028](tasks/cleat-028-compiler-driver.md) | 编译驱动：compile-form / eval、compile、构建脚本 | M4 | cleat-027、cleat-022 | 未开始 |
| [cleat-029](tasks/cleat-029-phase-a-stage0-build.md) | Phase A：stage0 编译出 cleat.boot.dll | M5 | cleat-028、cleat-018 | 未开始 |
| [cleat-030](tasks/cleat-030-host-and-repl.md) | 宿主 Cleat.Host 与 REPL | M5 | cleat-022、cleat-012 | 进行中（骨架完成） |
| [cleat-031](tasks/cleat-031-phase-b-fixpoint.md) | Phase B：自举不动点 | M6 | cleat-029、cleat-030 | 未开始 |
| [cleat-032](tasks/cleat-032-conformance-baseline.md) | 符合性基线：语料、clojure-test-suite、test_clojure | M6 | cleat-031、cleat-002 | 未开始 |
| [cleat-033](tasks/cleat-033-aot-and-load-path.md) | 命名空间 AOT（.clj.dll）与加载路径 | M7 | cleat-032 | 未开始 |
| [cleat-034](tasks/cleat-034-publish-and-repl-editing.md) | 自包含发布与 REPL 行编辑 | M7 | cleat-033 | 未开始 |
| [cleat-035](tasks/cleat-035-embedding-and-error-goldens.md) | 嵌入 API 与错误黄金文件 | M7 | cleat-033 | 未开始 |
| [cleat-036](tasks/cleat-036-lcore-seed.md) | Lcore 种子（可选缓存；Phase A 过慢时提前到 M2） | M7 | cleat-021、cleat-018 | 未开始 |
| [cleat-037](tasks/cleat-037-targets-and-llvm-primitives.md) | 机器描述表与原语表的 llvm 列 | 后续（LLVM） | cleat-004、cleat-028 | 搁置 |
| [cleat-038](tasks/cleat-038-native-runtime-kernel.md) | 原生运行时 cleat-rt 内核（C） | 后续（LLVM） | cleat-004、cleat-037 | 搁置 |
| [cleat-039](tasks/cleat-039-codegen-llvm.md) | codegen-llvm | 后续（LLVM） | cleat-037、cleat-038、cleat-025 | 搁置 |
| [cleat-040](tasks/cleat-040-native-core-collections.md) | 原生 core：持久集合、惰性序列与协议（Clojure 实现） | 后续（LLVM） | cleat-039 | 搁置 |
| [cleat-041](tasks/cleat-041-native-driver-and-conformance.md) | 交叉编译驱动与原生语料验收 | 后续（LLVM） | cleat-040 | 搁置 |
| [cleat-042](tasks/cleat-042-performance.md) | 性能：拆箱、协议内联缓存、跨单元直接链接 | 后续 | cleat-032 | 未开始 |
| [cleat-043](tasks/cleat-043-genclass-proxy-async.md) | gen-class、proxy 全量与异步 | 后续 | cleat-033 | 未开始 |
| [cleat-044](tasks/cleat-044-spike-clojure-on-ironscheme.md) | PoC：Clojure on IronScheme（语法对象、命名空间、`(clojure syntax)`、卫生宏、接缝） | 决策前 | cleat-017 | 未开始（待用户决定） |

## 里程碑

| 里程碑 | 任务 | 产出 |
|--------|------|------|
| M0 准备与验证 | cleat-001 ~ cleat-003、cleat-005 | 骨架、测试资产、.NET 与提取两项验证结论 |
| M1 新运行时 | cleat-006 ~ cleat-012 | 无 DLR 的 `Cleat.Core`，`primitives.edn` |
| M2 展开器与 stage0 | cleat-013 ~ cleat-018 | 能从源码加载全部 core 的解释器 |
| M3 Lcil 与发射器 | cleat-019 ~ cleat-022 | 手写 Lcil → dll 可运行 |
| M4 nanopass 与编译器 | cleat-023 ~ cleat-028 | 能编译单个命名空间 |
| M5 首次自举 | cleat-029 ~ cleat-030 | `cleat.boot.dll`（stage1）、宿主 |
| M6 自举闭合与符合性 | cleat-031 ~ cleat-032 | 不动点、三个基线 |
| M7 工程 | cleat-033 ~ cleat-036 | AOT、发布、REPL、嵌入、种子 |
| 后续 | cleat-042 ~ cleat-043 | 性能、gen-class / proxy / 异步 |
| 后续（LLVM，搁置） | cleat-004、cleat-037 ~ cleat-041 | 原生可执行文件、原生语料通过率。**决定（2026-10-10）：先完成 CIL 能力，LLVM 后端整体搁置**，设计保留在 [06](designs/06-llvm-backend.md) |

## 关键路径

cleat-001 → 005 → 006 → 008 → 013 → 015 →（019 → 020 →）016 → 018 → 023 → 024 → 025 → 026 → 027 → 028 → 029 → 031 → 032。
M3（019 ~ 022）与 M1 / M2 可并行。LLVM 相关任务搁置，不在当前计划内。
