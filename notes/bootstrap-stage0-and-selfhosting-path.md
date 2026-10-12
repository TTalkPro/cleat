# stage0 的本质与"第三段"自寄宿路径

> 背景问题:(1) stage0 到底是什么——一个解释执行的解释器吗?(2) 若放弃原生(Scheme)nanopass、改用 Clojure 重写版([07](../designs/07-nanopass-port.md) 的 `cleat.nanopass`),cleat 能否自举?"追加第三段"方案具体指什么,与已作废的 Clojure stage0 方案有何区别?
> 相关文档:[08](../designs/08-bootstrap.md)(自举)、[15](../designs/15-scheme-core-vendoring.md)(现行方向)、[07](../designs/07-nanopass-port.md)(nanopass Clojure 移植,已随 cleat-023 搁置)。

## 核心结论

1. **stage0 是一个 ~1050 行的 C# 树遍历解释器,解释的不是 Scheme 源码而是 Lcore**(宏全部展开后的核心形式语言)。它的唯一职责是在没有任何编译产物的冷启动时刻,把展开器与编译器(Scheme 代码)跑起来,点着第一把火;不动点成立后退居幕后,仅在重生成种子或从零重建时出场。
2. **"放弃原生 nanopass 改用 Clojure"不是换一个库,而是换掉编译器的实现语言层**——使用 nanopass 的编译器本身是 Scheme 写的(3,925 行、10 define-pass、58 nanopass-case),所以该决策等价于复活 15 号文搁置的 cleat-023 + 024~029 + Clojure stage0 整个任务群。
3. **正确的走法是"追加第三段"而非"替换"**:先由现行两段产出编译后的 Clojure 镜像,再在镜像上以编译方式生长 Clojure 版 nanopass/编译器,用逐字节等价门控夺权,最后把 Scheme 层降级为种子文物。它与旧方案的全部区别在于**动手写 Clojure 编译器的时机**——等编译后的 Clojure 存在之后再写,而不是之前。

## 1. stage0 的精确画像

来源:IronScheme.Stage0 纳入([15](../designs/15-scheme-core-vendoring.md) §1),C# 树遍历解释器,无优化、行为可预测。

| 属性 | 事实 |
|---|---|
| 解释对象 | **Lcore**(lambda / letrec / if / set! / 字面量 / 原语调用……宏已全部展开),不是源码 |
| 输入 | 文本种子 `boot-seed.core`(1.5MB,Scheme boot 源码展开后的核心形式)或展开器输出的 Lcore |
| 语言能力 | 只认 **Scheme 的 Lcore 节点**;**不认 Clojure 节点** |
| 裁剪 | 去 `escape-frame`/`call/cc`,加 `$try/catch`(15 §3) |
| 平时是否使用 | 不用;仅从零重建或改展开器后按两步法重生成种子时出场(08 §6) |

冷启动流程(08 §3 第一段,cleat-046 已达不动点):

```
boot-seed.core ──stage0 读入并解释──▶ 运行中的展开器(psyntax) + (rnrs)
   ──展开器(被解释着)从源码展开 nanopass + 编译器,Lcore 同样由 stage0 解释──▶ 运行中的编译器
   ──编译器(被解释着)编译全部 boot 源码──▶ Lcil 文本 ──Cleat.Emit(C# CIL 发射器)──▶ stage1
stage1 自跑构建脚本 → stage2 → stage3;判据: 逐字节相同(2026-10-10 实测)
```

存在理由:展开器与编译器是 Scheme 代码,点着它们之前不存在任何编译后的 Scheme,只能由别的语言(C#)写一个尽可能小、尽可能笨、因此尽可能可信的解释器。命名即自举惯例:stage0 点第一把火,stage1 起自己编自己。

### 三个"解释器/执行体"不要混

1. **`Cleat.Stage0`(现役)**:本节主角,Scheme Lcore 解释器;
2. **Clojure 版 stage0(已作废)**:08"已作废"一节的原方案(cleat-015/016/018),C# 解释 Clojure Lcore,供解释运行 Clojure 写的编译器;
3. **cleat-052 的 mini 解释器**:只认简单 Lcore 形式的 `eval` 用,与自举无关。

## 2. "放弃 native nanopass"的真实含义与三个风险

native nanopass 不是可替换依赖,而是**编译器的实现语言层**。改用 Clojure 版意味着编译器也要用 Clojure 重写(024~029),且框架宏要在某个执行体上展开运行。若按旧方案(Clojure stage0)闭合循环,三个现行路线已消除的风险重新背上:

| 风险 | 内容 |
|---|---|
| 双语义一致性 | 不动点判据要求"解释执行的编译器"与"编译后的编译器"输出逐字节相同(08 §5)。现行路线白捡(IronScheme stage0 与编译器同源成熟);旧方案要把 Clojure 语义在 C# 解释器与新编译器各实现一遍,分歧只在整条链跑完时暴露 |
| 解释期性能 | 锚点:IronScheme 调优的 stage0 展开 nanopass+编译器 16.9s(08 §4);新写解释器跑元编程密度更高的 `cleat.nanopass` 展开,Phase A 可能从分钟级变小时级 |
| 冷启动子集夹逼 | 框架只能用 stage0 支持的子集写(07 D7,含 `deftype*` 经发射器生成真实 CLR 类型),而 stage0 要覆盖多大 core 又由框架决定——循环无不动点 |

## 3. "追加第三段"方案

```
第一段 Scheme 镜像(psyntax + Scheme 编译器 + 原生 nanopass;不动点已成立,不动)
第二段 Clojure 镜像(Scheme 编译器编 core.clj → cleat.boot.dll;cleat-053,现行路线本来就要做)
第三段 自寄宿(新增):
   用 Clojure 写 cleat.nanopass(07, ~2000 行)与 Clojure 编译器(原 024~029)
   由现役 Scheme 编译器编译,跑在第二段镜像上(编译执行,无解释器)
   门控 1: Clojure 编译器编 core.clj 的产物 == Scheme 编译器的产物(逐字节)
   门控 2: Clojure 编译器自编译 B → C,B == C
   门控过后: Scheme 层不删除,降级为种子文物(从零重建时的信任根,08 §1 原则不变)
```

**关键时机倒转**:旧方案的死结是 Clojure 编译器必须在"任何编译后的 Clojure 存在之前"就能运行,故被迫写解释器;第三段先让"能跑 Clojure 的机器"(第二段的编译后镜像)存在,再动手写 Clojure 编译器。三个风险因此消失:双语义问题变为两套**编译器**输出的逐字节门控(可逐 pass 差分);框架宏在编译后镜像上全速展开;写框架时 `clojure.core` 已完整,无子集夹逼。类比 GCC 自举:gcc 由手头已有的 C 编译器编出再编自己,没人为了自举先写 C 解释器。

## 4. 常见误解澄清(问答记录)

- **"现在已有可用的 Clojure 编译器"——不成立**。现有的是"能编 Clojure 节点的 Scheme 编译器"(cleat-051 的 Clojure 节点支持);Clojure 写的编译器尚不存在,正是第三段要写的东西。
- **"第三段需要重写一个匹配 Clojure 的 stage0"——不成立**。需要 stage0 的唯一理由是没有东西能跑 Clojure 代码;第二段产出编译后镜像后该理由消失。第三段里 Scheme 编译器与 stage0 的 C# 代码全程不动。
