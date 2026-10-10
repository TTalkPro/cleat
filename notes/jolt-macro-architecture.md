# Jolt 宏系统架构调研：对卫生宏设计的启示

> 调研对象：`~/workspace/jolt`（Clojure on Scheme：读 Clojure 源码 → 宿主无关 IR → 发射 Scheme → Chez/Gambit 运行，编译器自举）
> 背景问题：能否在保留 Clojure defmacro 写法的同时支持 syntax-case，并让 defmacro 建立在 syntax-case 之上？

## 核心结论

Jolt 是"Clojure on Scheme"路线的完整落地，但它在宏系统的分岔口上明确选择：**不借用 Chez 的卫生展开器，而是在自己的 analyzer 里重新实现 defmacro / syntax-quote 的 JVM 语义**。这个选择不是偏好，是结构性必然。

## Jolt 宏系统的四层结构

### 1. defmacro 是 analyzer 的 special form（不是宏）

`jolt-core/jolt/analyzer.clj` 中，defmacro 由分析器直接降级：

```
defmacro → (def name (fn [&form &env & declared-args] body...)) + (. var setMacro)
```

- 与 JVM Clojure 展开结果逐字节对齐；`macro-fn-arities` 与 `defmacro-expansion` 共用同一套降级逻辑（注释明确说明：防止 shape drift）
- `&form`/`&env` 是普通词法参数（闭包可捕获），`:arglists` 元数据只保留声明参数
- 效果：tools.analyzer 风格的消费方（注释点名 typedclojure）可无改动工作

### 2. 宏展开发生在 Jolt 自己的分析器里

- `analyze-list` 按 canonical 顺序：macroexpand 优先于 special form 分派；local 可 shadow 宏，但不能 shadow 真正的 special form（与 `macroexpand1` 的 `isSpecial` 检查一致）
- 展开入口收敛于 `form-expand-1`：把宏函数当普通函数 apply，`&env` 以 map 传入（`amp-env-map env`）
- 用户 Clojure 代码从不经过 Chez 的展开器

### 3. syntax-quote 也是 special form，实现为纯数据变换

- analyzer 将 `syntax-quote` 交给 `form-syntax-quote-lower` / `form-syntax-quote-expand`
- 底层为 `host/chez/host-contract.ss` 的 `hc-sq-*` 一族：
  - 每次 syntax-quote 建一个 gsmap（保证同一模板内 `x#` 一致）
  - auto-gensym 用全局计数器 + 互斥锁（`hc-sq-gensym`）
  - 符号限定在 `jsq-resolve-symbol` 完成（携带编译期命名空间 ctx）
- 即：Clojure 的"土制 hygiene"原样移植到 Scheme，**不借用宿主 quasisyntax**

### 4. Chez 的 syntax-case 只存在于宿主运行时层

- `syntax-rules` / `syntax-case` 仅出现在 `collections.ss`（HAMT 位运算宏）、`hasheq.ss`、`fn-form-registry.ss` 等 Scheme 实现文件
- 与用户宏系统零交集

## 为什么不能用宿主卫生展开器（结构性约束）

1. **自举约束**：编译器是 Clojure 写的且编译自己。若宏展开委托给 Chez 展开器，"编译编译器"就依赖宿主展开器先存在——循环依赖。展开器必须活在被编译语言的语义里。
2. **多后端约束**：IR 宿主无关，还要发射到 Gambit→JS。Chez 的展开结果是 Chez 特有语法对象，其他后端拿不到。宏必须在进入 IR 之前展开干净。

推论：给 Clojure 加 syntax-case，必须实现在 Clojure 自己的展开器内部，不能委托给任何宿主 Scheme。

## 对 defmacro-on-syntax-case 设计的三个参考点

### ① 宏调用协议 `[&form &env & args]` 是唯一接缝

所有宏调用收敛于 `form-expand-1` 一处。卫生化改造只需动这一个点：hygienic 宏收到带 marks 的输入，legacy 宏收到剥光的。

### ② `&env` 管道已存在

词法环境在展开时已以 map 形式流入宏，只是内容贫瘠（仅 locals 名字）。`free-identifier=?` 需要的解析信息是往这根已有管道里加内容，不用新铺管道。

### ③ `jsq-resolve-symbol` 是挂 definition-site context 的天然位置

syntax-quote 目前在此做静态命名空间限定；按设计，这里是"静态限定"升级为"记录定义处 scope"的挂钩点——`hc-sq-symbol` 已持有编译期命名空间状态，挂 metadata marks 是顺手的事。

## 设计回顾：分层方案（前两轮讨论）

- syntax-case 是更一般的机制，defmacro 是其特例：输入剥上下文（`syntax->datum`），输出挂使用处上下文（`datum->syntax`）——Racket 的 defmacro 兼容层即此模式（约 20 行）
- Clojure syntax-quote 三规则可精确映射到 scope marks：
  | syntax-quote 行为 | scope marks 解释 |
  |---|---|
  | `` `foo `` 自动限定 | 挂宏定义处上下文 |
  | `~'sym` 有意捕获 | 挂使用处上下文 |
  | `x#` gensym | 引入全新 scope |
- 兼容性关键：marks 挂 symbol 的 metadata（Clojure 的 `=`/`hash` 忽略 metadata，旧代码无感知）；list/vector/map 保持裸数据；"剥元数据即降级为 defmacro 语义"作为明确规则
- 成本排序：ellipsis 匹配器 > analyzer 的 scope 引入改造 > `free-identifier=?` 环境回传 > defmacro 兼容层（几乎免费）

## 行动建议

Jolt 是最佳实验场：编译器用 Clojure 写，改它的 analyzer 比改 JVM Clojure 快几个数量级，且前提齐备（自含 analyzer、宏接缝单一、&env 管道在位、syntax-quote 有明确符号解析点）。

最小 PoC 路径：

1. analyzer 加 `defsyntax` special form，宏 var 元数据打标
2. `form-expand-1` 按标记分派（裸数据 / 带 marks 的 symbol）
3. `jsq-resolve-symbol` 处给限定符号挂 definition-site scope
4. 用 hygienic 原语重实现 `when`/`cond` 等 kernel 宏，验证"defmacro 语义可由 syntax-case 无损表达"

关键文件索引：

- `jolt-core/jolt/analyzer.clj` — defmacro special form（约 740–826 行）、宏展开分派（约 2270–2309 行）、syntax-quote 分派（1423 行）
- `host/chez/host-contract.ss` — `hc-sq-*` syntax-quote 实现（约 55、227、317、623–714 行）
- `jolt-core/clojure/core/00-syntax.clj` — kernel 宏层（when/cond/and/or/->/fn/defn，可作为重实现验证对象）

---

## 补充：Jolt 的最小宿主基底量化（"整个 Clojure 建立在少数 Scheme 元语上"）

问题：按照 Jolt 的实现方法，能否将整个 Clojure 建立在少数 Scheme 元语上？
答案：能，且元语集可精确量化——**17 个 special form + 约 20 种 IR 节点 + 87 个 native op + 一道约 160 函数的宿主契约 + 约 2 万行 Scheme 运行时**。

### ① 编译面：极小

- special form 全集（`analyzer.clj` 的 `handled`）：`quote / if / do / def / fn* / let* / loop* / recur / throw / try / syntax-quote / var / letfn* / set! / defmacro / monitor-enter / monitor-exit`，共 17 个
- JVM 上的 `case`/`deftype`/`defprotocol` 等全部做成宏或库降级
- IR（`jolt/ir.clj`）：约 20 种节点构造器（`const / local / var(-ref) / the-var / host(-ref/static/new) / if / do / invoke / def / let / fn / vector / map / set / quote / throw / coerce`），纯 map 数据、不含宿主值 → IR 天然可移植、AOT 安全

### ② Native op 注册表：87 个

- `jolt/op_registry.clj`（316 行）是单一事实源：87 个 `clojure.core` 操作被特化为直接发射 Scheme 过程调用
- 每条目带快速路径（`:dbl` flonum / `:lng` fixnum / `:bd` BigDecimal）与纯度/折叠元数据供优化 pass 消费
- 这就是 clojure.core 下沉到宿主的全部——只有 87 个

### ③ 宿主契约：约 160 个函数

- `host/chez/host-contract.ss`（1086 行）是 portable jolt-core（Clojure 写的、交叉编译到 Scheme 的分析器+发射器）与宿主之间的**全部**接口：form 谓词、syntax-quote lowering、ns/var 解析、类 hint
- "少数元语"的形式化答案 = 这道契约

### ④ 运行时基底：约 2 万行 Scheme（真正的质量所在）

| 组件 | 文件 | 行数 |
|---|---|---|
| 运行时核心（var 表/真值/等价/调用协议） | `rt.ss` | 2811 |
| seq 抽象（含惰性桥接） | `seq.ss` + `lazy-bridge.ss` | ~3000 |
| Clojure reader | `reader.ss` | 2516 |
| 持久集合（RRB-trie vector + HAMT map/set + transient） | `collections.ss` + `transients.ss` | ~2340 |
| 相等/哈希（值语义） | `hasheq.ss` + `values.ss` | ~1570 |
| 多态（协议/记录/多分派） | `protocols.ss` + `records*.ss` + `multimethods.ss` | ~3650 |
| ns/var/动态绑定 | `ns.ss` + `vars.ss` + `dyn-binding.ss` | ~1700 |
| 并发 | `atoms/refs/locks/fibers` | ~3250 |
| 87 个 native op 的实体 | `natives-*.ss` | ~1800 |
| 打印/正则/数值 | `printing/regex*/number-print` | ~1600 |

`host/chez` 总量 5.3 万行，其余为构建/加载/镜像/测试门工具链（`build.ss` 3926、`loader.ss` 3154、`state-image.ss` 2465、`run-*.ss` 门脚本等）。

### ⑤ clojure.core 本体：大部分是 Clojure

- `jolt-core/clojure/core/` 约 9000 行（其中 `90-docs.clj` 3567 行是 docstring）
- 分 tier 加载：`00-kernel.clj` **仅 85 行**（自举最小内核）→ `00-syntax`（宏层 1003 行）→ seq → coll → macros → lazy → io → gvec
- clojure.core 绝大部分逻辑用 Clojure 自写，站在 87 个 native op + 宿主契约之上

### 两个保持元语集小的关键取舍

1. **不依赖 Scheme 高级控制流**：recur 编译为循环，无尾调用依赖；`continuations.ss` 仅 177 行，call/cc 不是基底的一部分
2. **interop 用 shim 而非反射**：`java.*` 类 token 只是 `host-table.ss` 注册表里的名字，把宿主互操作从反射降级为查表

### 对卫生宏方案的含义

整个宏系统（defmacro special form、macroexpand 分派、未来 defsyntax 的位置）都住在 portable 的 Clojure 层；宿主侧只有 `host-contract.ss` 里约 4 个 syntax-quote 函数（`hc-sq-*`）沾边。因此加 hygiene = 改 Clojure 写的 analyzer + 在契约里加少数 marks 操作函数，2 万行运行时基底零改动。

### 结论

Clojure 语言的"重"不在语法和编译（编译面极小），而在持久化、值相等、seq 这套运行时约定——这部分在任何宿主上都要自己造，Scheme 只提供 record types、数值、线程等建材。
