# cleat-089 codegen 二：类型推断、生成类、case、直接链接

- 里程碑：M8（阶段 A）　依赖：cleat-088　状态：未开始
- 设计依据：[19 D5](../designs/19-clojure-self-hosting.md)、[cleat-042](cleat-042-performance.md)、[cleat-079](cleat-079-type-lattice-narrowing.md)

## 目标
codegen 的其余部分，Lcil 与 Scheme 编译器在全部单元上规范化后相同。

## 工作内容
1. 类型格与 `expr-type`、var-facts 收窄、l64 / 原始签名（`clojure.lang.primifs`）、宿主调用的类型（Numbers、静态决议）、拆箱局部变量与 `cg-raw`、内联算术（Clojure 子集）、commonize（若普查认定有效）。
2. `deftype*` / `reify*`、`proxy-class*`、`gen-class*` 的类生成。
3. `clj-case`（switch）、`clj-static-call` 直接链接、`^:async` fn。
4. 普查认定不需要的部分（Scheme 推测循环等）不移植，结论里列出。

## 验收标准
- G2：boot 镜像、随附包、语料的全部单元 Lcil 规范化后相同。
- ILVerify 通过（`tools/build.cs ilverify`）。
