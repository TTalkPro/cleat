# cleat-015 stage0：Lcore 解释器核心

- 里程碑：M2　依赖：cleat-008、cleat-013　状态：未开始
- 设计依据：[08 §4](../designs/08-bootstrap.md)

## 目标
`Cleat.Stage0`：把 Lcore 分析成闭包树并执行，覆盖除 `deftype*`/`reify*`（cleat-016）外的全部节点。

## 工作内容
1. `Analyzer`：Lcore → Node；`local` 解析为（层数, 下标）；Var 解析为对象；原语按名字绑定；`fn` 的 arity 表。
2. `InterpretedFn : AFunction` / `InterpretedRestFn : RestFn`；参数帧；`^:once` 清空。
3. `label`/`jump`（`loop`/`recur`）在循环中重赋参数；`letfn` 先建后填；`box`。
4. `try`/`throw`/`monitor-*`；`case` 用 `Dictionary` + 哈希；`host-*` 经 `Reflector`；`def`/`set!`/`var-ref`/`the-var`；集合节点。
5. 登记 `RuntimeHooks.Compile` 为"分析 + 返回无参 `IFn`"。
6. 命令行：`stage0 [--time] [-I dir] <script.clj> args…`、`--core <file.edn>`；512 MB 栈线程。
7. 测试（`Stage0Tests`）：闭包与捕获变量赋值、多 arity 与变长、`loop`/`recur` 一百万次、`letfn` 互递归、`try`/`finally` 与异常穿透、`case` 三种形态、互操作（静态 / 实例 / 字段 / 构造 / 推断类型）、`def` 与宏定义后立即使用。

## 验收标准
- 测试通过；规模 1500 ~ 2500 行 C#。
