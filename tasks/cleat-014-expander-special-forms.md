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
