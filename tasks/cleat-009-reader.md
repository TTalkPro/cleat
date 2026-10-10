# cleat-009 reader 提取与决议接口

- 里程碑：M1　依赖：cleat-006、cleat-007　状态：未开始
- 设计依据：[02 §7](../designs/02-runtime.md)、[03 §1](../designs/03-language-core.md)

## 目标
`LispReader`、`EdnReader` 可独立于编译器工作。

## 工作内容
1. 复制 `LispReader.cs`、`EdnReader.cs`、`Readers/*`；19 处编译器引用改为 `IReaderResolver`（`CurrentNamespace`、`ResolveSymbol`、`IsSpecial`、`NamesStaticMember`、`IsMacro`）；默认实现只做命名空间表查找（展开器在 cleat-013 提供完整实现）。
2. reader 条件特性集 `:cleat :cleat-clr :cljr :default`（`:cleat-llvm` 由编译目标参数给出）。
3. `*read-eval*`、`*data-readers*`、`#=` 限制。
4. 测试：从 ClojureCLR `Csharp.Tests/ReaderTests` 移植；语法引用的输出与 Clojure 一致（`clojure.core/seq` `concat` `list` 的展开形状）；源位置元数据。

## 验收标准
- 读 `core.clj` 全部顶层形式不报错，形式数与 ClojureCLR 相同。
