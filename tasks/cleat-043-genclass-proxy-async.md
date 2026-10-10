# cleat-043 gen-class、proxy 全量与异步

- 里程碑：后续　依赖：cleat-033　状态：未开始
- 设计依据：[03 §4](../designs/03-language-core.md)

## 工作内容
1. `proxy` 全量（基类方法重写、`proxy-super`）：发射器生成子类，方法表转发（与 stage0 的 `deftype` 做法相同）。
2. `gen-class`（ClojureCLR `genclass.clj` 的语义）与 `gen-interface`。
3. 异步：评估 ClojureCLR net11 的 `await*` 与 IronScheme 10 的 `AsyncFrame` 状态机，决定 cleat 的表面（`async-fn` / `await`）。

## 验收标准
- 各项有设计附录与测试；语料与基线不退化。
