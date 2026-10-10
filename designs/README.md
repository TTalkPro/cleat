# 设计文档索引

Cleat：.NET 上的 Clojure 实现。运行时 C#，编译器用 Clojure 写（nanopass），CIL 与 LLVM IR 两个后端，分阶段自举，只依赖 .NET SDK。

| 文档 | 内容 |
|------|------|
| [00-overview.md](00-overview.md) | 目标、约束、总体架构、关键决策 D1 ~ D16、仓库布局、里程碑、风险 |
| [01-reference-analysis.md](01-reference-analysis.md) | 蓝本分析：IronScheme、ClojureCLR、jank、jolt、nanopass 各取什么、不取什么；许可事实 |
| [02-runtime.md](02-runtime.md) | 运行时 `Cleat.Core`：值表示、Var、函数模型、原语表、钩子、reader、无 DLR 的互操作、加载器、从 ClojureCLR 提取的清单 |
| [03-language-core.md](03-language-core.md) | 核心语法范围、C# 展开器、Lcore 文法、与 Clojure 的偏离、core 库分层与加载顺序 |
| [04-compiler.md](04-compiler.md) | nanopass 编译器：L0 ~ L3、pass 链、机器描述表、原语编译、直接链接、互操作节点、`deftype`、驱动 |
| [05-cil-backend.md](05-cil-backend.md) | CIL 后端：继承 IronScheme 的 Lcil 与发射器，扩展项、常量编码、代码生成约定、决议规则、加载约定 |
| [06-llvm-backend.md](06-llvm-backend.md) | LLVM IR 后端：AOT 交叉编译、C 内核运行时、用 Clojure 写的原生集合、对象模型与调用约定、M0 验证项 |
| [07-nanopass-port.md](07-nanopass-port.md) | nanopass 框架移植到 Clojure：表面语法、上下文切换、注册表、运行时表示、验证 |
| [08-bootstrap.md](08-bootstrap.md) | 自举：stage0 解释器、Phase A / B、可选 Lcore 种子、交叉编译纪律、确定性 |
| [09-testing.md](09-testing.md) | 符合性判据（jolt 语料、clojure-test-suite、test_clojure）、测试分层、基线 |
| [15-scheme-core-vendoring.md](15-scheme-core-vendoring.md) | **现行方向**：纳入 IronScheme 元层的取舍与更名、可扩展语法对象协议、try / catch 异常模型、为何 nanopass 不改、新里程碑 M2′ / M4′ / M5′ |
| [14-three-layer-architecture.md](14-three-layer-architecture.md) | **探讨**：VM / Scheme 元层 / Clojure 三层架构的四个结构性问题与修正、剩余风险 |
| [13-syntax-objects-and-scheme-core.md](13-syntax-objects-and-scheme-core.md) | **探讨**：语法对象重塑展开流程、驱动 nanopass 的最小 Scheme 元语言、"Clojure on IronScheme"方案 C 与 A / B 的取舍、PoC 计划 |

任务索引见仓库根目录 [TASK.md](../TASK.md)。
