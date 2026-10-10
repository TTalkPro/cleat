# cleat-021 常量编码与 fasl

- 里程碑：M3　依赖：cleat-019、cleat-006、cleat-007　状态：未开始
- 设计依据：[05 §3](../designs/05-cil-backend.md)

## 目标
编译器侧的中立编码（Clojure，`cleat.compiler.constants`，先用 C# 原型验证格式）与发射器 / 运行时的二进制 fasl。

## 工作内容
1. 编码规格（05 §3）写入规格附录；map / set 排序规则；共享 `def`/`ref`。
2. `Cleat.Emit.FaslEncoder`：编码 → 字节（标签 + 变长计数 + UTF-16 字符串），Deflate，嵌入 `$Constants` RVA 数据；`ldconst` 展开。
3. `Cleat.Core.Runtime.Fasl`：`Read`（重建对象；`Var.intern`、`Keyword.intern`、`defrecord` 的 `create`、`Type.GetType`）与 `Write`（供 cleat-036 种子与接口文件）。
4. 测试：全部编码的往返；共享与环；`-0.0`；带元数据的集合；`#inst`/`#uuid`/`#"re"`。

## 验收标准
- 往返测试通过；同一输入两次编码逐字节相同。
