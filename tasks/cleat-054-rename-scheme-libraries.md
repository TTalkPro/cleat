# cleat-054 Scheme 库名更名

- 里程碑：后续　依赖：cleat-053　状态：未开始
- 设计依据：[15 §1](../designs/15-scheme-core-vendoring.md)

## 工作内容
1. `(ironscheme …)` → `(cleat scheme …)`、`(boot …)` 保持；源码、`targets.sls`、构建脚本、测试同步。
2. 按 IronScheme 05 §5 重生成种子，不动点通过。

## 验收标准
- 仓库中不再出现 `ironscheme` 库名；两段不动点通过。
