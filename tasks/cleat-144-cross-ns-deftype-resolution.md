# cleat-144 跨 ns 的 deftype / definterface 编译期决议

- 里程碑：M16　依赖：无　状态：未开始
- 设计依据：[28 §2.3](../designs/28-hot-path-optimizations.md)

## 问题

实测兼容性缺口：`(:import (r7rs.util MPair))`、`^IMPair` 类型提示、`(MPair. 1 2)` 在 cleat 上
全部 "Unable to resolve classname"，JVM Clojure 正常。用户代码因此只能走工厂函数 + 接口访问器
（`mcons` / `mcar`），既多一层包装调用（+2 ns / 次，shapes3 实测），又拿不到字段直读，
还使 cleat-145 的访问器内联无从下手。

## 工作内容

1. 定位缺口：deftype / definterface 生成的 CLR 类型名在跨编译单元（其他 ns 的 AOT / 运行时编译产物）
   时如何登记；对照 cleat-042 的内联接口文件与 cleat-106 的类型决议机制。
2. TypeResolver / 命名空间映射补生成类决议：`ns` 里 deftype 定义的类可被其他 ns `:import`、
   用作类型提示与构造（`Foo.`），行为对齐 JVM（含未编译时的 require 触发加载）。
3. 接口文件（ifc）或程序集清单登记生成类，AOT 与运行时编译两条路都覆盖。
4. 补差分 / 符合性用例：跨 ns `:import` deftype、definterface、协议类型的构造与提示。

## 验收

- r7rs.util 的 `(:import (r7rs.util MPair))` + `^IMPair` + `(MPair. 1 2)` 在 cleat 上可用，
  行为与 JVM 一致；bench 里至少一项改用直接构造作对照。
- 语料门禁（gate 521 分类）、CTS、test-clojure 基线不变；Core / Host 测试全过。
- 涉及 codegen / driver 时种子不动点（stage2 == stage3）。
