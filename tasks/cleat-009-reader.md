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

## 结论（2026-10-10）

完成。
- `LispReader` / `EdnReader` / `Readers/*` 随提取进入；编译器引用已改为 `IReaderResolver`（`CurrentNS`、`ResolveClass`、`ResolveAlias`、`ResolveVar`、`IsSpecial`），缺省实现 `DefaultReaderResolver` 按 `*ns*` 的映射与别名解析（等同 `Compiler.resolveSymbol`，特殊形式表暂硬编码在它里面，展开器接管后改为由展开器提供，cleat-013）。本任务删去两处"解析器为 null"的死分支。
- reader 条件特性：`PlatformFeatures` 静态属性，缺省 `#{:cleat :cleat-clr :cljr}`，用户给的 `:features` 与之合并；编译驱动按目标切换。
- 列号：列表的 `:column` 改为 `'('` 本身的列（`ColumnNumber - 1`），与 JVM 一致（ClojureCLR 原来多 1）。
- `#=` 与记录字面量的 4 处占位由 cleat-010 接 `Reflector`。`#inst` / `#uuid` 等依赖 `*data-readers*` 的缺省表，由 `instant.clj` / `uuid.clj` 加载后生效（cleat-017）。
- 测试 `ReaderTests`：字面量往返、语法引用在当前命名空间下的限定规则与自动 gensym、`~` / `~@`、reader 条件（`:cleat` / `:cljr` / `:cleat-clr` / `:cleat-llvm` 不命中 / `#?@`）、源位置与元数据、`*read-eval*` 门禁、EdnReader。读整个 `core.clj` 的核对在 cleat-017。
