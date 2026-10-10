# cleat-012 RT 拆分、加载器与运行时钩子

- 里程碑：M1　依赖：cleat-007、cleat-009、cleat-010　状态：未开始
- 设计依据：[02 §6、§9](../designs/02-runtime.md)

## 目标
`RT` 只剩值操作与初始化；加载与编译经钩子。

## 工作内容
1. `RT.cs` 复制改写：去 `Microsoft.Scripting.Hosting`、`Compiler.*`；`load`/`loadFile`/`classForName` 的编译器部分移到 `Cleat.Core.Runtime.Loader`，通过 `RuntimeHooks.Compile`/`LoadSource` 回调。
2. `RuntimeHooks`：`PrintMethod`、`Compile`、`LoadSource`、`Macroexpand`（展开器登记）、`Err`；`$set-hook!` 原语。
3. `Loader`：加载路径（`CLEAT_LOAD_PATH`、`-I`）、`.clj.dll` 与源码的新旧比较、`[CleatModule]` 入口调用、内存程序集 `Run`。
4. 启动流程：登记原语 → 建 `clojure.core`/`cleat.kernel` → （有 boot 镜像）加载镜像 →（无）等待 stage0 从源码加载。
5. 测试：钩子未登记时的错误退路；加载路径解析。

## 验收标准
- `Cleat.Core` 不引用任何编译器类型；初始化在无 boot 镜像时也能完成。
