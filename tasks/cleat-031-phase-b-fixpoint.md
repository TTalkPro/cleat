# cleat-031 Phase B：自举不动点

- 里程碑：M6　依赖：cleat-029、cleat-030　状态：未开始
- 设计依据：[08 §3、§7、§8](../designs/08-bootstrap.md)

## 工作内容
1. `Cleat.Host --boot stage1 build-boot.clj` → stage2；→ stage3；比较 Lcil 文本与 dll 字节；另比较 stage1（stage0 产出）与 stage2。
2. 定位并修复不一致来源（gensym 计数重置、`Symbol`/`Keyword` 哈希、map 遍历顺序、MVID、浮点格式）。
3. `Cleat.Bootstrap.Tests`（`Category=Bootstrap`）：stage0 → 1 → 2 → 3，断言 2 == 3（Lcil 与 dll）、1 == 2（dll）、ILVerify stage3，把 stage3 装为工作区镜像。
4. `CLEAT_DUMP_FAILED_CORE`、`CLEAT_DUMP_PASSES` 调试开关。

## 验收标准
- stage2 与 stage3 的 Lcil 一致、dll 逐字节一致；ILVerify 通过；耗时记入本文件。
