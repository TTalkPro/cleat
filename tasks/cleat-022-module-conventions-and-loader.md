# cleat-022 程序集约定、接口文件与加载器对接

- 里程碑：M3　依赖：cleat-020、cleat-021、cleat-012　状态：未开始
- 设计依据：[05 §6](../designs/05-cil-backend.md)、[02 §9](../designs/02-runtime.md)

## 目标
发射出的程序集能被 `Loader` 按约定加载与执行。

## 工作内容
1. `[CleatModule(入口类型, "Initialize")]`；`Initialize` 签名校验；boot 镜像与 `.clj.dll` 的入口类型命名。
2. 接口文件 `.ifc`（EDN）：镜像 id（MVID）、每个已知函数的 Var、类名、arity 表；`Fasl.Write` 写出。
3. `Loader.LoadModule(path, inMemory)`、`Run(Assembly)`；镜像 id 核对（`BootInterface.Check`）。
4. 测试：手写 Lcil 库 → dll → `Loader` 加载并执行入口；接口文件读写。

## 验收标准
- 测试通过。
