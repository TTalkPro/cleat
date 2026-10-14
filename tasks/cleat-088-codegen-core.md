# cleat-088 codegen 一：骨架、调用、控制流与 fn*

- 里程碑：M8（阶段 A）　依赖：cleat-087　状态：未开始
- 设计依据：[19 D5](../designs/19-clojure-self-hosting.md)、[05](../designs/05-cil-backend.md)、`src/Cleat.Emit/LCIL.md`

## 目标
不含类型推断的 codegen：L3 → Lcil S 表达式（Clojure 数据），交给 `Cleat.Emit`。

## 工作内容
1. 单元与方法上下文（`unit` / `mctx`：指令、标签、局部变量、try 深度、`operands!`）、Lcil 输出（发射器接受 Clojure 数据，`ClojureData.cs`）。
2. 常量编码（Clojure 值与普查需要的其余标签）、常量表。
3. Var 读写与 def、`clj-invoke` / `clj-keyword-invoke` / `clj-call`、`if` / `do` / `let` / `letrec` / `label` / `jump`、try / catch / finally（照现行：处理器闭包）。
4. fn* 类（AFunction / RestFn、`invokeStatic`、子句方法、闭包）、局部变量清除。
5. 接口文件 `.ifc` 的写出与读入（boot 与包）。

## 验收标准
- G2：不经类型推断的单元（普查给出名单，或以开关关掉推断后两边比较）Lcil 规范化后相同。
- 用 Clojure 编译器编出的小程序集能加载运行（Core / Host 测试里挑一组）。
