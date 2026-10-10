# cleat-047 异常模型简化为 try / catch

- 里程碑：M2′　依赖：cleat-046　状态：未开始
- 设计依据：[15 §3](../designs/15-scheme-core-vendoring.md)

## 工作内容
1. 运行时：删 `EscapeContinuation` / `ContinuationException` / `call/cc` 原语、`ExceptionHandlers` 与 `$current-handlers`；`raise` = 抛 `SchemeException(条件)`；`raise-continuable` 等同 `raise`；`with-exception-handler` 删除。
2. 核心形式 `($try/catch body ((type handler) …))`：特殊原语表加入；codegen 生成 Lcil `try/catch`（try 区域内不发 `tail.`）；删 `escape-frame`；stage0 加节点。
3. 展开器：`guard` 改为展开到 `$try/catch`；`main.sls` 3 处、`compat.sls`、`compiler.sls:116` 改写；`boot/exceptions.sls` 按新模型重写。
4. 按 IronScheme 05 §5 两步法重生成种子；不动点与快照测试再次通过；nanopass 的 2 处 `guard` 用例有测试。

## 验收标准
- 仓库中不再有 `call/cc`、处理器栈；自举不动点与快照通过；种子已重生成且 stage0 与新镜像生成的种子一致。
