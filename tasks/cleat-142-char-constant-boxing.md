# cleat-142 char 常量装箱缓存

- 里程碑：M16　依赖：无　状态：完成（2026-10-20，commit 25e115a）
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

## 结论

只做了常量发射点与缓存范围两项：`codegen.clj` 的 `(char? d)` 分支改调
`Cleat.Core.Runtime.CharBox.Get`，`CharBox.Hi` 按 28 §2.1 从 127 扩到 255（Latin-1，多 2 KB）。
`=` / `==` 对已知 char 操作数发原始比较（原计划第 3 项）**未做**：装箱点消除后该比较不再是
这条链上的主导成本，且需要与 cleat-111 的数值快路径决议一起改，留给 cleat-147。
`CLEAT_NO_OPT=charbox` 退回 `ldc.i4 + box char-type` 的旧发射，开 / 关为完整对照。

## 验证（2026-10-20）

- 语义：`tests/Cleat.Host.Tests/data/charbox_semantics.clj`（`=` / hash / pr-str / str / condp /
  集合键 / nth / seq / mapv / 越界异常）默认与 `CLEAT_NO_OPT=charbox` 输出均与 `.out` 逐字相同；
  Host 测试 `CharConstantsAreBoxedThroughTheCache` 通过。
- 构建 / 门禁 / 基线（合并 main 6063c39 上复跑）：Release 0 error / 13 warning（= 仓库基线）、
  Core 123/123、Host 278/278、语料门禁 `gate: OK (521)`、CTS 6868/29/6 与 test-clojure 3688/18 与基线一致。
- 种子：改 codegen 后 `dotnet run tools/build.cs -- clojure-seed` 重生成并自举到不动点
  （stage2 == stage3，242 s），种子文件随 commit 25e115a 进入仓库；6063c39 上重跑种子无改动。
- 分配（本地 `tools/alloc.cs`，browse 需带 `inputs/browse.input` 标准输入）：
  `System.Char` 1844.5 MB（27.8%）退出 top，browse 总分配 6633 → 4778 MB。
- 远端同窗口开 / 关 A/B（192.168.3.111，3 次取最小）：browse 5.728 → 5.503 s（-3.9%，
  与 143 合并生效，见 notes 的 M16 §1）；本地 browse 3 次取最小 7.64 → 7.35 s。
  单开关隔离（notes M16 §2）显示本项在 lattice / paraffins / slatex 上没有可测回退。
