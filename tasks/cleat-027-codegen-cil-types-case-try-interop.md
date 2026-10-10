# cleat-027 codegen-cil：deftype / reify、case、try、互操作

- 里程碑：M4　依赖：cleat-026　状态：未开始
- 设计依据：[04 §7、§8](../designs/04-compiler.md)、[05 §2、§4、§5](../designs/05-cil-backend.md)

## 工作内容
1. `deftype` 节点 → `(type …)`：接口、字段、ctor、`override` 方法、`getBasis`、`IType`/`IRecord`；`reify` 的捕获字段；`definterface` → `interface` 类型。
2. `case` → `switch`（int）/ 哈希 `switch` + 比较；`try` 的语句提升（lift-try）与 `(try …)` 伪指令；`instance-of`。
3. `host-*` → `clr-call`/`clr-field-*`/`clr-new`/`clr-type` + 实参类型提示。
4. 测试：`compile-small` 加 `deftype`/`defrecord`/`reify`/协议/`case`/`try`/互操作用例。

## 验收标准
- 用新编译器编译 `core_deftype.clj` 与 `core/protocols.clj` 并加载运行。
