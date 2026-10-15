# cleat-099 反射警告（*warn-on-reflection*）

- 里程碑：M9　依赖：cleat-097　状态：未开始

## 背景
cleat-097 分类 test_clojure 的失败：`clojure.test-clojure.rt` 的 18 条全是 `*warn-on-reflection*` 为真时期望的反射警告，cleat 从不输出。
分析器（`cleat.compiler.analyzer`）决议宿主调用时知道哪些落到了运行时反射（`Reflector`），只是没有报出来。

## 工作内容
1. 分析器：`*warn-on-reflection*` 为真且宿主成员在编译期决议不了时，向 `*err*` 打印一行警告，措辞按 ClojureCLR（test_clojure 的正则）：
   - `Reflection warning, FILE:LINE:COL - reference to field/property NAME can't be resolved (target class is unknown).`
   - `Reflection warning, FILE:LINE:COL - reference to field/property NAME on TYPE can't be resolved.`
   - `Reflection warning, FILE:LINE:COL - call to method NAME on TYPE can't be resolved (no such method).`
   - `Reflection warning, FILE:LINE:COL - call to method NAME on TYPE can't be resolved (argument types: T1, T2).` / `(argument types: unknown)`
   - `Reflection warning, FILE:LINE:COL - call to method NAME can't be resolved (target class is unknown).`
   - `Reflection warning, FILE:LINE:COL - call to static method NAME on TYPE can't be resolved (argument types: …).`
   - `Reflection warning, FILE:LINE:COL - call to TYPE ctor can't be resolved.`
2. 镜像构建与编译器包构建时 `*warn-on-reflection*` 为假（不改输出）；core.clj 里现有的反射调用不受影响。
3. `tests/test-clojure-baseline.edn`：rt 的 18 条转为通过后用 `--write` 更新。

## 验收标准
- test_clojure 的 rt 命名空间 0 失败；全量测试通过；种子不动点。
