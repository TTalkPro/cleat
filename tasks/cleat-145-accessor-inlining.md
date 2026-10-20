# cleat-145 小访问器的跨编译单元内联

- 里程碑：M16　依赖：cleat-144　状态：未开始
- 设计依据：[28 §2.3](../designs/28-hot-path-optimizations.md)

## 问题

归因实验 3：JVM 关内联（`-XX:-Inline`）后 browse / scheme / peval / array1 掉到 cleat 的 ±20% 量级
——pair 密集项 3 ~ 5x 差距的主体是 C2 把小访问器与包装调用内联掉。cleat 侧：defn 包装比接口直调
贵约 2 ns（3.4 vs 1.3），JVM 为 0；`register-eval-inlines!` 只在 eval 单元注册内联接口文件
（driver.clj 对 `:boot` / `:package` 关闭），r7rs 普通 ns 的 `mcar` / `mcdr` / `set-mcar!`
一类访问器都不会被内联。`not` 的 `:inline` 曾让 tak 3.7 → 1.9 s，这层收益在 r7rs 尺度已被验证。

## 工作内容

1. 内联接口文件扩展到镜像 / 包构建：解决"内联决定跨自举阶段必须稳定"的反馈问题
   （driver.clj 现注释的原因），必要时按固定点迭代或把内联决定与阶段解耦。
2. 内联对象：deftype 字段读 / 写访问器、小 defn 包装（体量阈值 + 无副作用判定），
   命中后发字段直读 / 直存，绕过接口调用。
3. 与 `restrict-loop-inlines?`（cleat-120）协调：循环体内非 core 用户函数的内联限制不回退
   （array1 的 tier-0 活性教训），访问器类内联需单独评估是否豁免。
4. `CLEAT_NO_OPT` 开关（如 `accinline`）保留旧行为。

## 验收

- 种子不动点：内联决定变化后 stage2 == stage3，BuildFromTheSeedReproducesTheSeed 通过。
- 语义用例：访问器内联开 / 关输出逐字相同（含中途重定义 Var、异常顺序、副作用次数）。
- 同窗口 A/B：browse / peval / scheme / destruc 等 pair 密集项可测收益；array1 不回退
  （对照 cleat-120 的结论）；门禁与两套基线不变。
