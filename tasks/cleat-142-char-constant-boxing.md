# cleat-142 char 常量装箱缓存

- 里程碑：M16　依赖：无　状态：未开始
- 设计依据：[28 §2.1](../designs/28-hot-path-optimizations.md)

## 问题

codegen.clj 的常量发射对 char 字面量走 `ldc.i4 + box char-type`，每次求值都装箱。
browse 的真实分配画像里 `System.Char` 占 28%（1.84 GB），来源是 `my-match` 里
`(= (nth (name …) 0) \?)` / `\*` 一类字面量；`(= \a \a)` 探针 8.2 ns。
`CharBox`（cleat-121）只覆盖了 `RT.nth` 的返回路径，没覆盖常量加载点。

## 工作内容

1. 运行时的 char 装箱缓存：对齐 JVM `Character.valueOf` 的语义（0 ~ 255 预装箱，或按 `CharBox`
   现有协议扩展覆盖范围），提供 `CharBox.Get(char)` 一类入口。
2. codegen 常量发射改为读缓存：`(char? d)` 分支不再发 `box char-type`，改为对缓存的静态字段 /
   调用；超出缓存范围的值维持现状装箱。
3. `=` / `==` 对已知 char 操作数发原始比较（与 cleat-111 的数值快路径同一套决议）。
4. `CLEAT_NO_OPT` 开关（如 `charbox`）保留旧发射路径做 A/B。

## 验收

- 语义用例（char 字面量的 `=` / `identical?` / 哈希 / 打印 / case）开 / 关输出逐字相同；
  缓存内 char 的 `identical?` 行为与 JVM Clojure 对照记录（JVM 0 ~ 127 缓存）。
- alloc.cs：browse 的 `System.Char` 分配量显著下降（目标：该类型退出 top）。
- 同窗口 A/B：browse 及受影响项不回退、有可测收益；语料门禁 / CTS / test-clojure 基线不变；
  改 codegen 后种子不动点（stage2 == stage3）。
