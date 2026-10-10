# cleat-048 可扩展语法对象

- 里程碑：M2′　依赖：cleat-047　状态：未开始
- 设计依据：[15 §2](../designs/15-scheme-core-vendoring.md)

## 工作内容
1. 新库 `(cleat scheme syntax-ext)`：`syntax-ext` 记录与注册表；绑定扩展表；`syntax-error-hook`。
2. "零扩展"重构：`add-mark`、`syntax-kind?` 一族、`strip` / `strip-annotations`、`syntax-dispatch`（模式 `#(ext kind p)`）与 `syntax-case` 的模式编译、`syntax->datum`、`do-macro-call` 输出检查、quasisyntax 模板——全部改为查表，表里只有 pair / vector；快照与不动点逐字节不变（重生成种子）。
3. 注册 Clojure 集合（向量 / map / set / 带元数据对象）：wrap → `syntax->datum` 往返测试，模式匹配 Clojure 向量，模板重建 Clojure 向量并保留元数据；reader 的 `:line` / `:column` / `:file` 转注解。
4. `chi-expr` 对未知绑定种类查绑定扩展表；注册一个测试用种类验证。

## 验收标准
- 零扩展阶段产物逐字节不变；Clojure 集合的往返、匹配、模板测试通过；nanopass 快照不变。
