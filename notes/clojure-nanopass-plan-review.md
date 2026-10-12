# Clojure 版 nanopass 方案评估

> 评估对象：[07](../designs/07-nanopass-port.md)（`cleat.nanopass` 移植设计）、[16](../designs/16-nanopass-host-survey.md)（宿主语言调研）、
> [bootstrap-stage0-and-selfhosting-path](bootstrap-stage0-and-selfhosting-path.md)（"追加第三段"自寄宿路径）。
> 时间：2026-10-12（cleat-042、cleat-058 完成之后）。

## 结论

技术上可行，难点判断准确（ellipsis 是唯一的硬骨头）。但**现在不建议启动**：第三段要替换的范围比笔记估计的大一倍以上；
"逐字节等价"的门控过严；"为什么现在做"缺少论证。将来要做的话，按 pass 渐进迁移比全量重写稳。

## 1. 三份文档的状态

| 文档 | 内容 | 状态 |
|---|---|---|
| 07 | 约 2000 行 Clojure 重写 nanopass；显式 `(out …)` 模板、语言注册表、deftype 记录 | 为"stage0 解释运行 Clojure 编译器"的旧路线而写，随 cleat-023 搁置；D7 等 stage0 约束已失效（见 07 文首的标注） |
| 16 | SBCL / Erlang / Clojure 宿主对比；Clojure 难度：ellipsis > 编译期文法校验 > 无尾调用 > API 风格 | 对 07 的可行性背书 |
| bootstrap 笔记 | 现有两段产出 Clojure 镜像后，在镜像上编译出 Clojure 版 nanopass + 编译器；门控一为与 Scheme 编译器逐字节等价，门控二为自编译不动点；Scheme 层降级为种子 | 新提出的路线 |

## 2. 站得住的部分

1. **第三段的时机倒转**正确。编译器编译后在镜像上运行，消掉旧方案的三个结构性风险：解释器与编译器的语义一致性、
   解释期性能、冷启动可用子集互相夹逼。类比 GCC 自举成立。
2. **Clojure 宿主的事实判断**准确。反引号在读入时就展开，宏看不到模板结构，所以模板只能显式 `(out …)`；
   没有相位分离，"生成代码"可以改为"数据驱动的函数"；复杂度集中在 ellipsis（多层 `...` 的匹配与重建）。
3. **栈深度不是实际障碍**，比 16 §4.3 说的乐观。说明：CLR 目标上 Clojure 的 IFn 调用**不发** `tail.` 前缀
   （`target-clojure-tail-calls?` 为假，cleat-051 的决定），但 pass 是对 IR 的结构递归，栈深 = IR 嵌套深度；
   循环用 `recur`。只有极深的 `begin` / `let` 链需要注意。

## 3. 问题

### 3.1 范围被低估

笔记只算了 nanopass 与编译器。要真正夺权，替换的是整条工具链：

| 部分 | 现状（Scheme） | 规模（行） |
|---|---|---|
| nanopass 框架 | `src/scheme/nanopass` | 约 4970（07 估 Clojure 版约 2000） |
| 编译器 pass 与 codegen | `src/scheme/lib/cleat/scheme/compiler/` | 约 4650，其中 codegen 2891 |
| Clojure 前端（特殊形式、宏、命名空间、语法引用、内联） | `src/scheme/clojure/` | 约 2250 |
| 前端依赖的展开器 | psyntax | 数千 |

现行 Clojure 前端建在 psyntax 之上（cleat-049 / 050）。第三段若不重写前端，Scheme 层仍在关键路径上，"降级为种子"不成立；
若重写，规模接近重做 M4′ + 编译器，粗估 8000 ~ 10000 行 Clojure。

### 3.2 "逐字节等价"门控不现实

cleat-042 之后 codegen 含类型推断与拆箱、静态决议、原始签名、`.inl` 内联、局部变量清除。要 Lcil 逐字节相同，
gensym 命名、常量表顺序、局部变量编号、临时变量分配都得一致——等于逐行翻译 codegen，重写失去意义。更现实的分级门控：

1. 每个 pass 的输出 unparse 后结构等价；
2. 最终 Lcil 规范化（局部变量与 gensym 重命名）后等价；
3. 语料、cts、test_clojure 基线与基准不退化。

逐字节只留给新编译器编译自身的不动点（B == C）。

### 3.3 动机不足

- 用 Clojure 维护编译器：cleat-058 已让 Scheme 层与 skeg 对齐，`#nil`、`'()` = EmptyList、try / catch / finally
  使两层语义贴近，"Scheme 层是负担"的论据变弱。
- 让 Clojure 用户写 nanopass：[cleat-056](../tasks/cleat-056-nanopass-clojure-surface.md)（原生 nanopass 的 Clojure 表面语法）代价小得多。
- 自举纯度：Scheme 仍保留为种子，收益有限。

### 3.4 双轨维护的窗口期

新编译器夺权前，两套编译器必须同步演进；改动最频繁的正是 codegen（cleat-042 一个任务就增加数百行）。
窗口期越长追赶成本越高，这是全量重写最大的实际风险。

## 4. 建议路线（若要做）

1. **先做 cleat-056**：Clojure 能定义语言、写 pass、读写编译器现有的 IR 记录。本身有用户价值，也是后续迁移的接口。
2. **逐 pass 迁移**：从小而独立的 pass 开始（`closures.sls` 82 行、`loops.sls` 237 行），改为 Clojure 实现后插回现有 pass 链；
   门控为该 pass 输出结构等价，出错能定位到 pass，随时可回退。
3. **Clojure 版框架的 ellipsis 引擎**：真要脱离原生框架时再攻，以上游 `tests/` 的多层省略号用例验收（16 的建议）。
4. **codegen 与 Clojure 前端最后**：体量最大、改动最频繁，等模式与差分测试工具成熟后再动。

每一步可以单独交付、单独回退，避免两套编译器长期并行。
