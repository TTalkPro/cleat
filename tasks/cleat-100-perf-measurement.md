# cleat-100 性能测量基建：分配采样、装箱探针、基准补充

- 里程碑：M10　依赖：cleat-099　状态：进行中
- 设计依据：[20 §0 / §7](../designs/20-skeg-optimizations.md)；skeg `tools/alloc.cs`（直接搬）

## 工作内容
1. `tools/alloc.cs`：从 skeg 搬来，改成在本进程里跑 cleat 宿主（`Cleat.Core.Runtime.Startup` + 脚本），按类型汇总 `GCAllocationTick`，给出各代 GC 次数与总分配量。
2. `bench/` 补：`untyped_loop`（初值来自无提示参数的 long / double 循环）、`nested_arith`（无提示参数的嵌套算术）、`ctor`（deftype / record / BCL 构造）、
   `property`（属性读）、`mbrot`（skeg r7rs 的 mbrot 形状，Clojure 写）。README 表与 `baseline.edn` 更新（`bench --write`）。
3. 现状数字记入 designs/20 §0。

## 验收标准
- `dotnet run tools/alloc.cs -- script.clj` 能给出分配表；新基准进 baseline。
