# cleat-089 codegen 二：生成类、AOT 常量与接口文件

- 里程碑：M8（阶段 A）　依赖：cleat-088　状态：未开始
- 设计依据：[19 D5](../designs/19-clojure-self-hosting.md)、[cleat-042](cleat-042-performance.md)、[cleat-079](cleat-079-type-lattice-narrowing.md)

## 目标
codegen 的其余部分，Lcil 与 Scheme 编译器在全部单元上规范化后相同。

## 工作内容
1. ~~类型格与 `expr-type`、var-facts 收窄、l64 / 原始签名、宿主调用的类型、拆箱局部变量与 `cg-raw`、commonize~~、~~`clj-case`、`clj-static-call`、`^:async` fn~~
   （cleat-088 已做；推测循环与 071 收窄按普查不移植）。
2. `deftype*` / `reify*`、`proxy-class*`、`gen-class*`、gen-interface 的类生成。
3. AOT 单元：常量编码（`(constants …)`，Clojure 值与 Scheme 数据）、接口文件 `.ifc` 的写出（`unit-interface`，HasStatic 与 arity）。

## 验收标准
- G2：boot 镜像、随附包、语料的全部单元 Lcil 规范化后相同。
- ILVerify 通过（`tools/build.cs ilverify`）。
