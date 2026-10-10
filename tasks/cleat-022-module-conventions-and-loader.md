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

## 结论（2026-10-10）

完成（加载器本身在 cleat-012 已做，本任务补接口文件与镜像核对）。
- 程序集约定：`[Cleat.Core.CleatModuleAttribute("入口类型", "Initialize")]`，入口 `public static object`，发射器在 `(entry …)` 时写入并校验（cleat-020）；`Loader.LoadModule` / `Run` 读属性调入口（cleat-012）。
- `Cleat.Core.Runtime.InterfaceFile`：EDN 格式 `{:assembly "名字" :image-id "MVID" :entries [{:var ns/name :class "T, asm" :arities [n … -(required+1)] :static bool} …]}`，`ToEdn` / `Parse` / `Save` / `Load`，`PathFor(dll)` = 同名 `.ifc`；已知函数登记表 `FindKnown(var)`；`Register(file, asm)` 只在程序集 MVID 与文件相符时登记（镜像重建后的旧文件被忽略）；`Loader.LoadModule` 加载 dll 后自动尝试旁边的 `.ifc`。
- `BootInterface.Check(imageId)`：产物入口开头调用，与 `CurrentImageId`（宿主加载 boot 镜像时设置，cleat-030）不符时抛 `StaleModuleException`；`Loader.TryLoad` 捕获它并回退到源码（有源码时），打印警告。
- 测试 `InterfaceFileTests`（3 项）：EDN 往返与排序确定；加载 dll 时不相符的接口文件被忽略、相符的登记；镜像核对。
- 留给后续：编译驱动（cleat-028）写接口文件与在产物入口生成 `Check` 调用；`CurrentImageId` 由宿主设置（cleat-030）。
