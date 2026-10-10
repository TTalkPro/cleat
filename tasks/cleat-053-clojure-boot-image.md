# cleat-053 Clojure 自举

- 里程碑：M5′　依赖：cleat-052　状态：未开始
- 设计依据：[15 §1](../designs/15-scheme-core-vendoring.md)、[08 §7](../designs/08-bootstrap.md)（确定性）

## 工作内容
1. `GenerateBootImage`：Scheme 镜像按 `src/boot/boot-order.edn` 编译 `cleat/kernel.clj`、`clojure/core.clj` … → `cleat.boot.dll` + `cleat.boot.ifc`；cleat-017 列出的新原语（`maybe-type`、`maybe-special-tag`、`gen-interface`）与 C# 移植（`Printf`、`ImmutableDictionaryEnumerator`）补齐。
2. 不动点：镜像 A 编 core 得 B，B 编得 C，B == C（Lcil 文本与 dll）；确定性措施按 08 §7。
3. `Cleat.Bootstrap.Tests`：两段不动点 + ILVerify。

## 验收标准
- `dotnet build` 从零产出两个镜像；不动点成立；宿主用 `cleat.boot.dll` 启动并跑语料 `:common` 子集（通过率记入 09 §3）。
