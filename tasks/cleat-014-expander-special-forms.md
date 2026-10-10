# cleat-014 展开器：特殊形式检查、case 预处理、deftype 签名、类型提示

- 里程碑：M2　依赖：cleat-013　状态：未开始
- 设计依据：[03 §2 第 3 ~ 4 条、§3](../designs/03-language-core.md)

## 目标
把 Clojure 编译器中"分析期需要反射"的部分做完，使 Lcore 成为纯数据。

## 工作内容
1. `fn*`：名字、参数向量、`&`、≤ 20 固定参数、唯一变长 arity、arity 冲突、`recur` 尾位置与个数、不跨 `try`。
2. `case*`：接受 `case` 宏的预哈希数据（shift/mask/low/high/switch-type/test-type/skip-check），校验并写入 Lcore。
3. `deftype*`/`reify*`：字段（`^:mutable`/`^:volatile-mutable`、提示）、接口决议、每个方法匹配接口方法（名字、参数个数、提示）得到参数 / 返回类型；`this` 绑定；`reify*` 的闭包变量；结果为数据。
4. `try`：`catch` 类型决议、`finally`；`throw`；`monitor-*`；`import*`；`set!` 目标检查；`the-var`。
5. 类型提示 `(tag …)` 统一为类型全名字符串；`*warn-on-reflection*` 警告（能静态决议的记为已决议提示，供发射器）。
6. 测试：`deftype`/`defrecord`/`reify`/`case`/`try` 的 Lcore 快照。

## 验收标准
- `core.clj`、`core_deftype.clj`、`core/protocols.clj`、`gvec.clj` 的全部顶层形式能展开为 Lcore。

## 结论（2026-10-10）

完成（见 cleat-013）。
- `fn*`：名字（自引用是 `IsThis` 的局部）、`^:once`、`&`、≤ 20 固定参数、单一变长、arity 冲突、变长参数个数检查；子句 `((x …) rest body)`。
- `loop*` / `recur`：尾位置检查、不跨 `try`、参数个数；fn 子句体内 `recur` 以参数为目标。
- `case*`：校验表达式是局部，整理 `((minhash test then) …)` 按哈希排序，`:int` 测试转为 long，`low` / `high` 由键算出。
- `deftype*` / `reify*`：接口决议（只许接口）、方法候选 = 接口（含继承）实例方法 + `Object` 的虚方法，按名字与参数个数匹配；多候选必须用提示按参数类型精确匹配（`Must hint overloaded method`）；`Iface.name` 显式实现；字段为 `IsField` 的局部（`^:unsynchronized-mutable` / `^:volatile-mutable` 才可 `set!`）；输出 `(method "I" "name" this ((x "T") …) "R" body)` 全是数据。`reify*` 的类名 `ns$reify__N`，捕获由编译器算。
- `try`：catch 类型决议与绑定、finally 位置；`throw`、`monitor-*`、`import*`、`var`、`set!`（局部可变字段 / Var / 字段）。
- 类型提示：`long` / `objects` 等特殊名、`T/1` 数组、import 别名、全名；解析不了的提示忽略（发射器回退运行时决议）。`(type-args T …)` 泛型方法实参记在 `host-call` 节点的元数据 `:type-args`。
- 未做：`by-ref` 实参（报错）；`*warn-on-reflection*` 的警告（展开器不做成员决议，留给发射器 / 运行时 cleat-026）。
