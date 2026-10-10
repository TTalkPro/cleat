# cleat-036 Lcore 种子（可选缓存）

- 里程碑：M7（若 cleat-018 测得 Phase A > 5 分钟则提前到 M2）　依赖：cleat-021、cleat-018　状态：未开始
- 设计依据：[08 §5](../designs/08-bootstrap.md)

## 工作内容
1. `tools/build.cs seed [--stage0]`：把 03 §5 全部 boot 命名空间展开为 Lcore 文本 `src/boot/boot-seed.edn`（常量用 05 §3 编码，`Fasl.Write` 的文本形态）。
2. stage0 `--seed` 模式：读种子跳过 reader 与宏展开；`deftype` 仍需生成类型。
3. 种子不动点：新系统与 stage0 生成的种子逐字节相同。
4. 种子作为 cleat-024 快照测试的输入来源。

## 验收标准
- Phase A 用种子的耗时与不用的对比记入本文件；种子不动点成立。
