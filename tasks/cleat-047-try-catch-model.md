# cleat-047 异常模型简化为 try / catch

- 里程碑：M2′　依赖：cleat-046　状态：完成
- 设计依据：[15 §3](../designs/15-scheme-core-vendoring.md)

## 工作内容
1. 运行时：删 `EscapeContinuation` / `ContinuationException` / `call/cc` 原语、`ExceptionHandlers` 与 `$current-handlers`；`raise` = 抛 `SchemeException(条件)`；`raise-continuable` 等同 `raise`；`with-exception-handler` 删除。
2. 核心形式 `($try/catch body ((type handler) …))`：特殊原语表加入；codegen 生成 Lcil `try/catch`（try 区域内不发 `tail.`）；删 `escape-frame`；stage0 加节点。
3. 展开器：`guard` 改为展开到 `$try/catch`；`main.sls` 3 处、`compat.sls`、`compiler.sls:116` 改写；`boot/exceptions.sls` 按新模型重写。
4. 按 IronScheme 05 §5 两步法重生成种子；不动点与快照测试再次通过；nanopass 的 2 处 `guard` 用例有测试。

## 验收标准
- 仓库中不再有 `call/cc`、处理器栈；自举不动点与快照通过；种子已重生成且 stage0 与新镜像生成的种子一致。

## 结论（2026-10-10）

完成。元层的异常模型是 CLR 的 try / catch：没有 `call/cc`、没有处理器栈。（执行中会话中断过一次，最后的测试修正与核对在恢复后完成。）

**删除**
- 运行时：`EscapeContinuation`、`ContinuationException`、`call/cc` / `call-with-current-continuation` 原语（`Control.cs` 只剩 `apply` / `call-with-values`，50 行）；处理器栈 `ExceptionHandlers` 与 `$current-handlers` / `$set-current-handlers!`（`Parameters.cs`）；`Exceptions.cs`、`IO.cs` 等处放行 `ContinuationException` 的分支；运行时钩子由 15 个减为 13 个（去掉 `raise-continuable`、`with-exception-handler`）。
- 编译器：L0 ~ L3 的 `escape-frame`、`call/cc` 特殊原语、async 的死代码路径；stage0 的对应节点。
- 规模：C#（Cleat.Scheme / Stage0 / PrimGen）+63 / −118 行；Scheme（boot 与编译器，不含种子）+147 / −438 行。

**新增：核心形式 `$try/catch`**
- 形式：`($try/catch body 类型1 处理器1 类型2 处理器2 …)`，类型为字符串形式的 CLR 类型名，或 `#f` 表示"任何 Scheme 条件"（即 `Cleat.Scheme.SchemeException`，处理器收到它的 `Condition`）；处理器是单参数过程；体的多值作为一个值穿过 try。
- 编译：parse-core 识别为特殊原语 → L0 的 try-catch 节点 → codegen 生成 Lcil `(try (body …) (catch "Cleat.Scheme.SchemeException" … get_Condition …) (catch "T" …))`；try 区域内不发 `tail.`（区域外照发），区域内的循环仍是循环。stage0 用 C# try / catch 实现同一节点。PrimGen 的特殊原语表加入 `$try/catch`。
- Scheme 侧：`boot/exceptions.sls` 重写——`raise` = 抛出包着条件的 `SchemeException`（照旧按 `display-stacktrace` 附栈），`raise-continuable` 保留为 `raise` 的同义名（元层不依赖可继续语义），`with-exception-handler` 不再导出；展开器的 `guard` 核心宏展开到 `$try/catch`，子句都不匹配时在 catch 处重新 `raise`；`main.sls` 的 REPL / load 错误捕获、`compiler.sls` 失败时转储、`io/ports.sls`、`base.sls` 的相关处改写；`dynamic-wind` 仍是 `$try/finally`（after 先于外层 catch 处理器运行）。
- nanopass 源码未改：它的 2 处 `(guard (c [else #f]) …)` 经新 `guard` 正常工作（有测试）。

**种子与不动点**
- 按 IronScheme 05 §5 两步法重生成种子（先让运行时 / stage0 / 编译器同时支持新旧两套，生成一次；再删旧机制、改写 Scheme 源码，生成第二次）。种子文本 +12,545 / −12,587 行。
- 种子不动点：镜像与 stage0 再生成的种子都与仓库中的逐字节相同（2026-10-10 复核，镜像约 16 s）。
- 自举不动点：`Bootstrap` 类别全部通过（stage1 == stage2 == stage3，ILVerify）；stage3 镜像 sha256 前缀 `ddef42cffe5220aa`（dll）、`63f6deab6a546e95`（Lcil）。

**测试**
- 新增 `tests/scheme/compiler/try-catch.sps`（23 项）：stage0 与编译两条路径下接住 Scheme 条件与 CLR 异常、嵌套 try / catch / finally 的顺序、`guard` 匹配 / 不匹配再抛 / 多值 / 区域内循环、处理器中 `raise`、nanopass 式 `(guard (c [else #f]) (error …))` 得 `#f`、Lcil 形状（只在 try 区域内检查没有 `(tail)`）；`compile-small.sps` 的 call/cc 与处理器用例改为新语义。
- `dotnet test`：Cleat.Core.Tests 129 通过；Cleat.Scheme.Tests 217 通过（约 3 分 45 秒，含自举）。

**留下的**
- psyntax 的核心原语名单仍声明 `await`、`task-*`、`ffi-*`、`pinvoke-call-internal` 等已无实现的名字（只是导出名，调用时报未绑定）；随 cleat-054 的库整理一并删除。
- 上游 nanopass 的 `tests/synforms.ss` 里有 `call/cc`，该文件不参与构建与测试。
