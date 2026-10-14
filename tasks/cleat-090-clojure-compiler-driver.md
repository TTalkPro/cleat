# cleat-090 Clojure 写的编译驱动与切换

- 里程碑：M8（阶段 B）　依赖：cleat-089　状态：未开始
- 设计依据：[19 §1 阶段 B、D5 G3](../designs/19-clojure-self-hosting.md)

## 目标
`CLEAT_COMPILER=clojure` 时运行时的 eval / load / compile / `--compile-boot` / `--compile-package` 全部走 Clojure 编译器，全量测试通过。

## 工作内容
1. 驱动：`compile-forms->lcil`、eval（简单形式解释 → 发射到内存）、load 钩子、`*compile-files*` 与包程序集、`--compile-boot` 按 `boot-order.edn` 编 `cleat.boot.dll`。
2. 宿主接入：钩子登记不再经 `ClojureHost` 的 Scheme 程序；Scheme 镜像在此模式下不加载。
3. G3：全量测试、语料 / cts / test_clojure 基线、错误黄金文件、`SchemeInvisibilityTests`、包测试、示例、基准（`tools/build.cs bench`）；记录编 boot 镜像、REPL 单次 eval 的耗时并与 Scheme 编译器对比。

## 验收标准
- 两种模式下全量测试都通过，基线不退化；编译耗时达到 19 D5 的目标或在结论里给出差距与计划。
