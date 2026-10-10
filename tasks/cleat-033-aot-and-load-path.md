# cleat-033 命名空间 AOT 与加载路径

- 里程碑：M7　依赖：cleat-032　状态：完成
- 设计依据：[04 §9](../designs/04-compiler.md)、[02 §9](../designs/02-runtime.md)、[05 §6](../designs/05-cil-backend.md)

## 工作内容
1. `(compile 'ns)` 写 `*compile-path*/ns.clj.dll` + `.ifc`；`Loader` 的新旧比较与回退源码；镜像 id 不符时的报错与回退。——cleat-052 起已有（驱动 `CompileOutputPath`、`Loader.TryLoad` 的时间比较与 `StaleModuleException` 回退），本任务验证并补全。
2. 跨单元直接链接到已加载 `.clj.dll`（接口文件登记）。——见下"直接链接"。
3. `tools/build.cs compile-libs`：预编译全部随附命名空间。
4. `load-file`、`require` 的 `:reload`/`:reload-all`、`*loaded-libs*`。——已有；`:reload` 对 dll 重跑入口（程序集缓存但 `Initialize` 再次执行），已测试。
5. 测试：AOT 后的 test_clojure 与语料结果与源码加载相同。

### 直接链接（cleat-033 的主体）

- **invokeStatic**：没有自由字段的 `fn*` 类为每个固定子句把体编成静态 `invokeStatic(self, args…)`（体只编这一份）；实例 `invoke` 转发并传 `this` 作 self——`(fn* me [] me)` 的 self 仍是实例本身（cleat-032 的"每次求值新对象"不变，无 Instance 单例）。变长子句保留实例体（RestFn 语义）。
- **受保护的直接调用**：调用点参数个数恰为接口文件条目的某固定 arity 时，展开器生成 `$clj-static-call`：先取 Var 的当前根，还是这个函数类就直接 `call 类::invokeStatic`（`newobj` 一个实例作 self），被 with-redefs / 重定义换掉的根走普通 `IFn.invoke`（语料行"with-redefs on deref"因此保持通过）。
- **接口文件**：`WriteInterfaceFile` 的条目全部 `:static true`；顺带修复候选检测（cleat-052 起的缺陷）：`defn` 的 fn 带 `:arglists` 元数据时初值包在 `($clj-call "WithMetaOf" #f fn meta)` 里，且展开器产物是 Scheme 序对——候选检测改为两种表示通吃后，boot 镜像条目从 24 条升到 1238 条。
- 直接链接只在运行镜像（含用户 dll）的接口文件登记后发生；boot 镜像自举 A 段（--no-boot）不含直调，B == C 不动点保持逐字节一致。

## 验收标准
- 全部随附命名空间 AOT 成功并通过 ILVerify；启动时间记入本文件。

结果（2026-10-11）：

- **AOT**：boot-order.edn 里 `:boot false` 的 5 个随附命名空间中 4 个 AOT 成功（`clojure.core.reducers`、`clojure.core.server`、`clojure.test.junit`、`clojure.test.tap`），ILVerify 全部通过。`clojure.repl.deps` 无法 AOT：它 require 的 `clojure/clr/basis` 不在本仓库（Maven 集成层），从源码加载同样失败——REPL 的 repl-requires 一直跳过它（cleat-052 注释），非回归。其余随附命名空间都在 boot 镜像里（cleat.boot.dll 本身通过 ILVerify）。
- **启动时间**（Release，开发布局，5 次）：镜像启动 ~1.19 s（Scheme 镜像 ~375 ms + 驱动库（fasl）~86 ms + Clojure 镜像 ~713 ms，`CLEAT_TIME=1`）。四个 AOT 命名空间的 require（含启动）~1.30 s，同样四个从源码加载 ~2.76 s——边际加载 ~0.11 s vs ~1.57 s（~14×）。
- **测试**：`dotnet test` 全绿（553：Core 129、Host 199、Scheme 225），含符合性门禁（语料 560 条偏离 = 基线）与 cts 基线（AOT dll 在加载路径上运行）。新增 `DirectLinkingAcrossUnitsAndWithRedefs`（跨单元直调、with-redefs 回退、:reload 重跑入口）；`CompileToCljDllThenLoadInAFreshProcess` 更新为 `:static true`。
