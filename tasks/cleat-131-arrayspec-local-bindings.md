# cleat-131 数组推测候选的作用域修复

- 里程碑：M13 后续　依赖：cleat-127、cleat-130　状态：完成（2026-10-17）
- 设计依据：[23 §2.6](../designs/23-type-specialization.md)

## 问题

cleat-130 的 r7rs nohint 变体中，`maze` 编译 `print-hexmaze` 失败：

```text
codegen: unbound variable g$t$1192$inl
```

关闭 `arrayspec`、`loopspec` 或 `infer` 任一项都能通过。调试 L3 后确认：数组推测扫描循环体时，
把内联结果留在循环体内 `let` 绑定的临时变量误当成“循环外数组”。随后生成的入口守卫
`(isinst T[] temp)` 在该 `let` 绑定之前引用了 `temp`，codegen 因此报未绑定变量。
循环推测先复制体、数组推测再包装副本，使该错误只在三个优化组合时出现。

## 修复

数组推测候选必须真正来自循环体外的绑定。选择候选前收集 `spec-binders` 返回的循环体内
`let` 变量、label 与形参；候选命中这些名字时不再推测，直接保留原循环。这不改变已有
外层数组的推测路径，只修正错误候选的作用域判断。

## 验证

- `data/arrayspec_semantics.clj` 新增循环体内已知调用返回数组的 `local-temp` 用例；
  `CLEAT_NO_OPT=arrayspec` 开 / 关输出逐字相同。
- r7rs nohint `maze` 使用原始 `inputs/maze.input` 完整运行成功，stderr 为空。
- Release 构建通过；专项 `UnhintedArraysInLoopsAreSpeculated` 通过；selfhost 不动点通过。
- compiler 产物变化后按 `tools/build.cs -- clojure-seed` 重生成种子（2091 KiB）；
  种子发射回的 boot / compiler 与 stage3 逐字节一致，`BuildFromTheSeedReproducesTheSeed` 通过。

