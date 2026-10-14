# cleat-029 Phase A：stage0 编译出 cleat.boot.dll

- 里程碑：M5　依赖：cleat-028、cleat-018　状态：由 cleat-083 ~ 091 取代（[19](../designs/19-clojure-self-hosting.md)，2026-10-14）
- 设计依据：[08 §3](../designs/08-bootstrap.md)

## 目标
`dotnet build` 的 `GenerateBootImage` 目标：stage0 从源码加载全部 boot 命名空间 + nanopass + 编译器，运行 `build-boot.clj`，产出 stage1。

## 工作内容
1. `Cleat.Host.csproj` 的 `GenerateBootImage` 目标（输入：`src/clojure/**`、`src/cleat/**`、`primitives.edn`、`Cleat.Core`/`Emit`/`Stage0` 程序集；增量）；`tools/build.cs stage1 <dir>`。
2. 在 stage0 上运行 `build-boot.clj`：每个顶层形式"展开 → 编译器（解释执行）→ Lcil；同时解释执行"。
3. 修复解释执行的编译器与编译后行为不一致处（gensym、哈希顺序、`deftype` 临时类型泄漏到常量等）。
4. 记录 Phase A 耗时、`boot.lcil` 大小、镜像大小与类型数。

## 验收标准
- `cleat.boot.dll` 生成；`Cleat.Host --boot stage1` 能启动并 `(println (+ 1 2))`；两次构建的 `boot.lcil` 逐字节相同。
