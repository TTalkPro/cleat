# cleat-033 命名空间 AOT 与加载路径

- 里程碑：M7　依赖：cleat-032　状态：未开始
- 设计依据：[04 §9](../designs/04-compiler.md)、[02 §9](../designs/02-runtime.md)、[05 §6](../designs/05-cil-backend.md)

## 工作内容
1. `(compile 'ns)` 写 `*compile-path*/ns.clj.dll` + `.ifc`；`Loader` 的新旧比较与回退源码；镜像 id 不符时的报错与回退。
2. 跨单元直接链接到已加载 `.clj.dll`（接口文件登记）。
3. `tools/build.cs compile-libs`：预编译全部随附命名空间。
4. `load-file`、`require` 的 `:reload`/`:reload-all`、`*loaded-libs*`。
5. 测试：AOT 后的 test_clojure 与语料结果与源码加载相同。

## 验收标准
- 全部随附命名空间 AOT 成功并通过 ILVerify；启动时间记入本文件。
