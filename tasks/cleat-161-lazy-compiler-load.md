# cleat-161 编译器延迟加载

- 里程碑：M19　依赖：cleat-159　状态：未开始
- 设计依据：[29 §3.3](../designs/29-usability-and-engineering.md)

## 问题

`Startup`（`src/Cleat.Core/Runtime/Startup.cs:57 ~ 94`）在镜像之后会无条件 require 编译器驱动，耗时约 193 ms。
`-e nil`、只调 AOT 函数的 `-m` / `-X` / `-T` 都要付这笔成本。

## 工作内容

1. 编译器驱动改成第一次需要编译时再加载：eval 非平凡形式、`load` 源文件、`compile`、`macroexpand` 涉及编译期设施时。
   简单求值路径（`CLEAT_NO_SIMPLE_EVAL` 关掉的那条）的语义不变。
2. 包程序集里的 ns 走 `-m`、`-X`、`-T` 调用已编译函数时，不加载编译器。
3. REPL：首个提示符之前不能变慢，可以在后台线程预热编译器；首个输入要等预热完成，不能出现竞态。
4. 嵌入 API（`CleatRuntime`）沿用同一个惰性入口。
5. `CLEAT_COMPILER_NS`（自举用的改名工具编译器）路径保持现状。

## 验收

- 159 定的目标值；`CLEAT_TIME` 显示 `-e nil` 不加载编译器。
- 自举不动点、`HostProcessTests`、`NuGetColdLoadTests`、nREPL / neorepl 包测试通过。
- 29 §9 的全套门禁。
