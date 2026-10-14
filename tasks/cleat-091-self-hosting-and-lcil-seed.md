# cleat-091 自举不动点与 Lcil 文本种子

- 里程碑：M8（阶段 C）　依赖：cleat-090　状态：未开始
- 设计依据：[19 D7、G4](../designs/19-clojure-self-hosting.md)

## 目标
Clojure 编译器编自己与 core，达到逐字节不动点；Lcil 文本种子成为信任根；Clojure 编译器成为默认。

## 工作内容
1. stage1（Scheme 编译器编出的 cleat.compiler.dll + cleat.boot.dll）编出 stage2，stage2 编出 stage3；stage2 == stage3（Lcil 与 dll 逐字节）。
2. 种子：`src/boot/seed/*.lcil.gz`；`Cleat.Emit` 读 gzip；记录种子体积与从种子构建的耗时。
3. 构建流程：`Cleat.Host.csproj` 的目标改为"种子 → stage1 → 编 boot / compiler"，不再需要 Scheme 镜像与 stage0；`tools/build.cs seed` 重生成种子并验证不动点；测试 `Bootstrap` 改为验证 Clojure 不动点。
4. 默认编译器切到 Clojure（`CLEAT_COMPILER=scheme` 暂时保留作对照，cleat-093 删）。
5. 运行时契约两步法写进 designs/08 的新版。

## 验收标准
- 从干净仓库（只有种子与源码）构建出的镜像与 stage3 相同；全量测试在默认模式下通过。
